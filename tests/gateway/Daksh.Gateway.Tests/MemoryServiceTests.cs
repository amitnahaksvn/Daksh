using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Memory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Regression tests for two <see cref="MemoryService"/> behaviours fixed this session, each currently
/// exercised only indirectly (and only for one scenario) through <see cref="E2eScenarioTests"/>:
///
/// 1. <c>SearchAsync</c>'s full-text recall used <c>plainto_tsquery</c>, which ANDs every word of the query,
///    so a natural-language multi-word question essentially never matched a stored memory unless every one
///    of its words appeared in the memory verbatim. The fix (<c>BuildOrTsQuery</c> + <c>EF.Functions.ToTsQuery</c>)
///    OR's the query's words together instead, so a memory needs only some of them.
/// 2. <c>WriteAsync</c> dedupes by normalized-content hash: writing the same content twice must not create a
///    second row.
///
/// Needs a real PostgreSQL (full-text search via <c>ToTsVector</c>/<c>ToTsQuery</c> is not something EF's
/// in-memory provider can emulate), so this reuses <see cref="GatewayFactory"/>'s Testcontainers-backed
/// pgvector container rather than duplicating that infrastructure in Daksh.Platform.Tests.
/// </summary>
[Collection(nameof(GatewayCollection))]
public sealed class MemoryServiceTests(GatewayFactory factory)
{
    private async Task<Guid> SeedWorkspaceAsync(PlatformDbContext db)
    {
        db.Tenant.SetSystem();
        var owner = new User
        {
            Email = $"memory-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"memory-{Guid.NewGuid():N}@example.com".ToUpperInvariant(),
            DisplayName = "Memory test owner",
            PasswordHash = "not-a-real-hash",
        };
        db.Users.Add(owner);
        var workspace = new Workspace { Name = "Memory test workspace", Slug = $"memory-{Guid.NewGuid():N}", OwnerUserId = owner.Id };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        return workspace.Id;
    }

    [Fact]
    public async Task SearchAsync_finds_a_memory_that_contains_only_some_of_the_query_words()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var workspaceId = await SeedWorkspaceAsync(db);
        db.Tenant.SetWorkspace(workspaceId, [], 0);
        var memoryService = scope.ServiceProvider.GetRequiredService<MemoryService>();

        // The stored memory contains "pgvector" and "cosine" but not "distance" or "metric" - with the old
        // plainto_tsquery (AND) behaviour, a query containing words the memory doesn't have would never match
        // at all. The fix ORs the query's words together, so overlap on even one distinctive word is enough.
        await memoryService.WriteAsync(workspaceId, new MemoryWriteRequest(
            Content: "pgvector supports cosine similarity search over embeddings stored as a vector column.",
            Scope: "workspace", Kind: "semantic", AgentId: null, UserId: null, SessionId: null), CancellationToken.None);

        var hits = await memoryService.SearchAsync(
            workspaceId, agentId: null, userId: null, scopes: ["workspace"],
            query: "What distance metric does pgvector use for cosine similarity?",
            topK: 5, minScore: 0.0, CancellationToken.None);

        Assert.Contains(hits, h => h.Memory.Content.Contains("pgvector supports cosine similarity"));
    }

    [Fact]
    public async Task SearchAsync_does_not_match_when_the_query_shares_no_words_with_any_memory()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var workspaceId = await SeedWorkspaceAsync(db);
        db.Tenant.SetWorkspace(workspaceId, [], 0);
        var memoryService = scope.ServiceProvider.GetRequiredService<MemoryService>();

        await memoryService.WriteAsync(workspaceId, new MemoryWriteRequest(
            Content: "The quarterly budget review happens every January.",
            Scope: "workspace", Kind: "semantic", AgentId: null, UserId: null, SessionId: null), CancellationToken.None);

        var hits = await memoryService.SearchAsync(
            workspaceId, agentId: null, userId: null, scopes: ["workspace"],
            query: "pgvector cosine embeddings database",
            topK: 5, minScore: 0.0, CancellationToken.None);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task WriteAsync_deduplicates_identical_content_instead_of_creating_a_second_row()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var workspaceId = await SeedWorkspaceAsync(db);
        db.Tenant.SetWorkspace(workspaceId, [], 0);
        var memoryService = scope.ServiceProvider.GetRequiredService<MemoryService>();

        const string content = "The research agent should always cite its sources when summarizing a web page.";
        var first = await memoryService.WriteAsync(workspaceId, new MemoryWriteRequest(
            Content: content, Scope: "workspace", Kind: "semantic", AgentId: null, UserId: null, SessionId: null,
            Importance: 0.4), CancellationToken.None);
        var second = await memoryService.WriteAsync(workspaceId, new MemoryWriteRequest(
            Content: content, Scope: "workspace", Kind: "semantic", AgentId: null, UserId: null, SessionId: null,
            Importance: 0.9), CancellationToken.None);

        // Same logical memory, not a new row.
        Assert.Equal(first.Id, second.Id);

        var rowCount = await db.Memories.CountAsync(m => m.WorkspaceId == workspaceId && m.ContentHash == first.ContentHash);
        Assert.Equal(1, rowCount);

        // The duplicate write still has a visible effect: importance is bumped to the higher of the two.
        var stored = await db.Memories.FirstAsync(m => m.Id == first.Id);
        Assert.Equal(0.9, stored.Importance);
    }
}

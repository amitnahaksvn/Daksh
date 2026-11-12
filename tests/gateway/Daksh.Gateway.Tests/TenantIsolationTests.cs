using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Exercises the two layers of tenant isolation documented in <see cref="PlatformDbContext"/>: the EF query
/// filter (every <see cref="IWorkspaceScoped"/> entity is filtered to <c>TenantContext.WorkspaceId</c> even
/// when the LINQ query itself carries no explicit workspace predicate) and the save-time guard (rejects a
/// write into another workspace even if a check upstream was missed). Runs against the same real PostgreSQL +
/// pgvector container as the other Gateway tests (<see cref="GatewayFactory"/>), talking to
/// <see cref="PlatformDbContext"/> directly rather than through HTTP, since tenant isolation is a DbContext-
/// level guarantee, not an API-level one.
/// </summary>
[Collection(nameof(GatewayCollection))]
public sealed class TenantIsolationTests(GatewayFactory factory)
{
    private IServiceScope NewScope() => factory.Services.CreateScope();

    private static async Task<(Guid WorkspaceId, Guid AgentId, Guid SessionId)> SeedWorkspaceAsync(PlatformDbContext db, string label)
    {
        var owner = new User
        {
            Email = $"{label}-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"{label}-{Guid.NewGuid():N}@example.com".ToUpperInvariant(),
            DisplayName = $"{label} owner",
            PasswordHash = "not-a-real-hash",
        };
        db.Users.Add(owner);

        var workspace = new Workspace { Name = $"{label} workspace", Slug = $"{label}-{Guid.NewGuid():N}", OwnerUserId = owner.Id };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var agent = new Agent { WorkspaceId = workspace.Id, Name = $"{label} agent", Slug = $"{label}-agent-{Guid.NewGuid():N}" };
        db.Agents.Add(agent);

        var session = new Session { WorkspaceId = workspace.Id, AgentId = agent.Id, Title = $"{label} session" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        return (workspace.Id, agent.Id, session.Id);
    }

    [Fact]
    public async Task Query_filter_scopes_rows_to_current_workspace_even_without_an_explicit_predicate()
    {
        Guid workspaceA, agentA, sessionA, workspaceB, agentB, sessionB;
        using (var seedScope = NewScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenant.SetSystem(); // Seeding writes into two different workspaces from one scope; only the
                                    // system actor is allowed to do that (ordinary callers are pinned to one).
            (workspaceA, agentA, sessionA) = await SeedWorkspaceAsync(db, "tenant-filter-a");
            (workspaceB, agentB, sessionB) = await SeedWorkspaceAsync(db, "tenant-filter-b");
        }

        // A caller scoped to workspace A queries with no WorkspaceId predicate at all - proving the query
        // filter itself, not application code, is what keeps workspace B's rows out.
        using var scopeA = NewScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<PlatformDbContext>();
        dbA.Tenant.SetWorkspace(workspaceA, [], 0);

        var visibleAgentIds = await dbA.Agents.Select(a => a.Id).ToListAsync();
        Assert.Contains(agentA, visibleAgentIds);
        Assert.DoesNotContain(agentB, visibleAgentIds);

        var visibleSessionIds = await dbA.Sessions.Select(s => s.Id).ToListAsync();
        Assert.Contains(sessionA, visibleSessionIds);
        Assert.DoesNotContain(sessionB, visibleSessionIds);

        // Directly fetching workspace B's agent by id (still no explicit workspace predicate) must come back
        // null, not just absent from a list query - the filter applies to every query shape.
        var fetchedOtherAgent = await dbA.Agents.FirstOrDefaultAsync(a => a.Id == agentB);
        Assert.Null(fetchedOtherAgent);

        // Sanity check: the same row IS visible to a caller actually scoped to workspace B, so this isn't
        // just a case of the row being broken or soft-deleted.
        using var scopeB = NewScope();
        var dbB = scopeB.ServiceProvider.GetRequiredService<PlatformDbContext>();
        dbB.Tenant.SetWorkspace(workspaceB, [], 0);
        var fetchedFromB = await dbB.Agents.FirstOrDefaultAsync(a => a.Id == agentB);
        Assert.NotNull(fetchedFromB);
    }

    [Fact]
    public async Task Save_guard_rejects_a_write_into_another_workspace_even_when_the_query_filter_was_bypassed()
    {
        Guid workspaceA, workspaceB;
        using (var seedScope = NewScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenant.SetSystem();
            (workspaceA, _, _) = await SeedWorkspaceAsync(db, "tenant-guard-a");
            (workspaceB, _, _) = await SeedWorkspaceAsync(db, "tenant-guard-b");
        }

        // A caller legitimately scoped to workspace A tries to directly construct and save a brand-new Agent
        // tagged as belonging to workspace B. No query filter is in play here at all (this is a new, Added
        // entity, not something read back through a filtered query) - this is exactly the "a check upstream
        // was missed" case the save-time guard exists to catch.
        using var scope = NewScope();
        var db2 = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db2.Tenant.SetWorkspace(workspaceA, [], 0);

        var rogueAgent = new Agent { WorkspaceId = workspaceB, Name = "Should never be saved", Slug = $"rogue-{Guid.NewGuid():N}" };
        db2.Agents.Add(rogueAgent);

        await Assert.ThrowsAsync<TenantViolationException>(() => db2.SaveChangesAsync());

        // And confirm it genuinely never made it to the database (the guard runs before the SQL round-trip,
        // but double-check via a system-scoped read in a fresh scope to be sure nothing was persisted).
        using var verifyScope = NewScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        verifyDb.Tenant.SetSystem();
        var persisted = await verifyDb.Agents.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == rogueAgent.Id);
        Assert.Null(persisted);
    }

    [Fact]
    public async Task Save_guard_rejects_moving_an_existing_row_to_another_workspace()
    {
        Guid workspaceA, agentA, workspaceB;
        using (var seedScope = NewScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Tenant.SetSystem();
            (workspaceA, agentA, _) = await SeedWorkspaceAsync(db, "tenant-move-a");
            (workspaceB, _, _) = await SeedWorkspaceAsync(db, "tenant-move-b");
        }

        using var scope = NewScope();
        var db2 = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db2.Tenant.SetWorkspace(workspaceA, [], 0);

        var agent = await db2.Agents.FirstAsync(a => a.Id == agentA);
        agent.WorkspaceId = workspaceB;

        await Assert.ThrowsAsync<TenantViolationException>(() => db2.SaveChangesAsync());
    }
}

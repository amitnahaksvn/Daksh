using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Memory = Daksh.Platform.Memory;

namespace Daksh.Platform.Memory;

public sealed record MemoryWriteRequest(string Content, string Scope, string Kind, Guid? AgentId, Guid? UserId, Guid? SessionId,
    string? Key = null, double Importance = 0.5, string? SourceType = null, string? SourceId = null, string? ModelProviderRef = null);

public sealed record MemoryHit(Domain.Memory Memory, double Score);

public sealed class MemoryServiceOptions
{
    /// <summary>"provider/model", for example "local/hash-embedding". Empty disables embeddings (keyword search only).</summary>
    public string? EmbeddingModelRef { get; set; }
    public int DedupeWindowDays { get; set; } = 30;
    public double DedupeSimilarityThreshold { get; set; } = 0.97;
}

/// <summary>
/// Stores, searches and consolidates memories. Search is hybrid: cosine similarity over pgvector when an embedding
/// model is configured, full-text search always, fused with reciprocal-rank fusion. Without an embedding model it
/// falls back to full-text search alone (MEM-006), so the platform works without any configured model.
/// </summary>
public sealed class MemoryService(PlatformDbContext db, ModelCallRunner models, Microsoft.Extensions.Options.IOptions<MemoryServiceOptions> options,
    ILogger<MemoryService> logger)
{
    public async Task<Domain.Memory> WriteAsync(Guid workspaceId, MemoryWriteRequest request, CancellationToken ct)
    {
        var hash = MemoryRanking.ContentHash(request.Content);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.DedupeWindowDays);
        var duplicate = await db.Memories.IgnoreQueryFilters()
            .Where(m => m.WorkspaceId == workspaceId && m.ContentHash == hash && m.AgentId == request.AgentId && m.UserId == request.UserId
                        && m.DeletedAt == null && m.CreatedAt >= cutoff)
            .FirstOrDefaultAsync(ct);
        if (duplicate is not null)
        {
            duplicate.Importance = Math.Max(duplicate.Importance, request.Importance);
            duplicate.LastAccessedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return duplicate;
        }

        var memory = new Domain.Memory
        {
            WorkspaceId = workspaceId, Content = request.Content, ContentHash = hash, Scope = request.Scope, Kind = request.Kind,
            AgentId = request.AgentId, UserId = request.UserId, SessionId = request.SessionId, Key = request.Key, Importance = request.Importance,
        };
        if (request.SourceType is not null)
            memory.Sources.Add(new MemorySource { WorkspaceId = workspaceId, MemoryId = memory.Id, SourceType = request.SourceType, SourceId = request.SourceId });
        db.Memories.Add(memory);
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(options.Value.EmbeddingModelRef))
        {
            try
            {
                var (vectors, _) = await models.EmbedAsync(workspaceId, options.Value.EmbeddingModelRef, [request.Content], ct);
                await WriteEmbeddingAsync(workspaceId, memory.Id, options.Value.EmbeddingModelRef, vectors[0], ct);
            }
            catch (Exception ex) when (ex is ProviderException or CapabilityException)
            {
                logger.LogWarning(ex, "Could not embed memory {MemoryId}; it remains searchable by keyword only.", memory.Id);
            }
        }
        return memory;
    }

    private async Task WriteEmbeddingAsync(Guid workspaceId, Guid memoryId, string model, float[] vector, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO platform."MemoryEmbeddings" ("Id","WorkspaceId","MemoryId","Model","Dimensions","CreatedAt","UpdatedAt","Embedding")
            VALUES ({Guid.CreateVersion7()}, {workspaceId}, {memoryId}, {model}, {vector.Length}, now(), now(), {MemoryRanking.VectorLiteral(vector)}::vector)
            ON CONFLICT ("MemoryId","Model") DO UPDATE SET "Embedding" = EXCLUDED."Embedding", "Dimensions" = EXCLUDED."Dimensions", "UpdatedAt" = now()
            """, ct);
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(Guid workspaceId, Guid? agentId, Guid? userId, IReadOnlyList<string> scopes,
        string query, int topK, double minScore, CancellationToken ct)
    {
        var scoped = db.Memories.Where(m => m.WorkspaceId == workspaceId &&
            ((m.Scope == "agent" && m.AgentId == agentId) || (m.Scope == "user" && m.UserId == userId) ||
             (m.Scope == "workspace") || (m.Scope == "shared")) && scopes.Contains(m.Scope));

        var keywordIds = await scoped
            .Where(m => EF.Functions.ToTsVector("english", m.Content).Matches(EF.Functions.PlainToTsQuery("english", query)))
            .OrderByDescending(m => EF.Functions.ToTsVector("english", m.Content).Rank(EF.Functions.PlainToTsQuery("english", query)))
            .Take(50).Select(m => m.Id).ToListAsync(ct);

        List<Memory.RankedId> vectorHits = [];
        if (!string.IsNullOrWhiteSpace(options.Value.EmbeddingModelRef))
        {
            try
            {
                var (vectors, _) = await models.EmbedAsync(workspaceId, options.Value.EmbeddingModelRef, [query], ct);
                var literal = MemoryRanking.VectorLiteral(vectors[0]);
                var candidateIds = await scoped.Select(m => m.Id).ToListAsync(ct);
                if (candidateIds.Count > 0)
                {
                    var rows = await db.Database.SqlQuery<VectorRow>($"""
                        SELECT e."MemoryId" AS "MemoryId", 1 - (e."Embedding" <=> {literal}::vector) AS "Score"
                        FROM platform."MemoryEmbeddings" e
                        WHERE e."Model" = {options.Value.EmbeddingModelRef} AND e."MemoryId" = ANY({candidateIds})
                        ORDER BY e."Embedding" <=> {literal}::vector LIMIT 50
                        """).ToListAsync(ct);
                    vectorHits = rows.Select(r => new Memory.RankedId(r.MemoryId, r.Score)).ToList();
                }
            }
            catch (Exception ex) when (ex is ProviderException or CapabilityException)
            {
                logger.LogWarning(ex, "Embedding search failed; falling back to keyword search only.");
            }
        }

        var keywordRanked = keywordIds.Select((id, i) => new Memory.RankedId(id, 1.0 - i * 0.01)).ToList();
        var fused = vectorHits.Count > 0 ? Memory.MemoryRanking.Fuse(vectorHits, keywordRanked) : keywordRanked;
        var topIds = fused.Where(f => f.Score >= minScore || vectorHits.Count == 0).Take(topK).ToList();
        if (topIds.Count == 0) return [];

        var memories = await db.Memories.Where(m => topIds.Select(t => t.Id).Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var now = DateTimeOffset.UtcNow;
        var results = new List<MemoryHit>();
        foreach (var t in topIds)
            if (memories.TryGetValue(t.Id, out var m))
            {
                m.LastAccessedAt = now; m.AccessCount++;
                results.Add(new MemoryHit(m, t.Score));
            }
        await db.SaveChangesAsync(ct);
        return results;
    }

    public async Task<int> PurgeExpiredAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await db.Memories.Where(m => m.WorkspaceId == workspaceId && m.ExpiresAt != null && m.ExpiresAt < now).ToListAsync(ct);
        foreach (var m in expired) m.DeletedAt = now;
        await db.SaveChangesAsync(ct);
        return expired.Count;
    }

    private sealed class VectorRow { public Guid MemoryId { get; set; } public double Score { get; set; } }
}

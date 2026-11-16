using Daksh.Platform.Domain;
using Daksh.Platform.Providers;

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
/// Pending MongoDB rebuild: previously stored, searched and consolidated memories against PlatformDbContext
/// (Postgres full-text search + pgvector cosine similarity). Every member throws until the Mongo-backed
/// implementation is written.
/// </summary>
public sealed class MemoryService
{
    private static NotImplementedException Pending() => new("MemoryService is unavailable: pending MongoDB rebuild.");

    public Task<Domain.Memory> WriteAsync(Guid workspaceId, MemoryWriteRequest request, CancellationToken ct) => throw Pending();

    public Task<IReadOnlyList<MemoryHit>> SearchAsync(Guid workspaceId, Guid? agentId, Guid? userId, IReadOnlyList<string> scopes,
        string query, int topK, double minScore, CancellationToken ct) => throw Pending();

    public Task<int> PurgeExpiredAsync(Guid workspaceId, CancellationToken ct) => throw Pending();
}

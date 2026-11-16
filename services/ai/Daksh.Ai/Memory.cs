using Microsoft.Extensions.AI;

namespace Daksh.Ai;

public sealed record MemoryHit(string Text, double Score);

/// <summary>User-controlled long-term memory. Only used when the user enabled memory; deletable at any time.</summary>
public interface IMemoryStore
{
    Task AddAsync(string userId, string text, CancellationToken ct = default);
    Task<IReadOnlyList<MemoryHit>> SearchAsync(string userId, string query, int topK, CancellationToken ct = default);
    Task DeleteAllAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// Reference implementation: embeddings + cosine similarity in process. Production target is MongoDB
/// behind the same interface (see docs/AI_ARCHITECTURE.md).
/// </summary>
public sealed class InMemoryVectorMemoryStore(IEmbeddingGenerator<string, Embedding<float>> embedder) : IMemoryStore
{
    private readonly Dictionary<string, List<(string Text, float[] Vector)>> _items = new();

    public async Task AddAsync(string userId, string text, CancellationToken ct = default)
    {
        var v = (await embedder.GenerateAsync([text], cancellationToken: ct))[0].Vector.ToArray();
        lock (_items) (_items.TryGetValue(userId, out var l) ? l : _items[userId] = []).Add((text, v));
    }

    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(string userId, string query, int topK, CancellationToken ct = default)
    {
        var q = (await embedder.GenerateAsync([query], cancellationToken: ct))[0].Vector.ToArray();
        lock (_items)
        {
            if (!_items.TryGetValue(userId, out var l)) return [];
            return l.Select(i => new MemoryHit(i.Text, Cosine(q, i.Vector)))
                    .Where(h => h.Score > 0.2).OrderByDescending(h => h.Score).Take(topK).ToList();
        }
    }

    public Task DeleteAllAsync(string userId, CancellationToken ct = default)
    {
        lock (_items) _items.Remove(userId);
        return Task.CompletedTask;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

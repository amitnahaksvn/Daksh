using System.Text;
using System.Text.RegularExpressions;
using Daksh.Platform.Security;

namespace Daksh.Platform.Memory;

public sealed record RankedId(Guid Id, double Score);

public static partial class MemoryRanking
{
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]")] private static partial Regex Punctuation();

    /// <summary>Hash of the text with case, punctuation and spacing removed; equal hashes are treated as duplicates.</summary>
    public static string ContentHash(string content)
    {
        var normalized = Spaces().Replace(Punctuation().Replace(content.ToLowerInvariant().Normalize(NormalizationForm.FormKC), " "), " ").Trim();
        return Tokens.Hash(normalized);
    }

    /// <summary>
    /// Reciprocal-rank fusion of a vector ranking and a keyword ranking. Items found by both rise to the top; the
    /// result score is scaled to 0..1 where 1 means "first in every list".
    /// </summary>
    public static IReadOnlyList<RankedId> Fuse(IReadOnlyList<RankedId> vector, IReadOnlyList<RankedId> keyword, int k = 60, double vectorWeight = 1, double keywordWeight = 1)
    {
        var scores = new Dictionary<Guid, double>();
        for (var i = 0; i < vector.Count; i++) scores[vector[i].Id] = scores.GetValueOrDefault(vector[i].Id) + vectorWeight / (k + i + 1);
        for (var i = 0; i < keyword.Count; i++) scores[keyword[i].Id] = scores.GetValueOrDefault(keyword[i].Id) + keywordWeight / (k + i + 1);
        var max = (vectorWeight + keywordWeight) / (k + 1);
        return scores.Select(s => new RankedId(s.Key, s.Value / max)).OrderByDescending(s => s.Score).ToList();
    }

    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>pgvector text literal, for example "[0.1,0.2]".</summary>
    public static string VectorLiteral(ReadOnlySpan<float> v)
    {
        var sb = new StringBuilder(v.Length * 9 + 2).Append('[');
        for (var i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Splits a document into overlapping chunks on paragraph boundaries.</summary>
    public static IReadOnlyList<string> Chunk(string text, int maxChars = 1200, int overlap = 150)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var para in text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = para;
            while (p.Length > maxChars)
            {
                if (current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); }
                chunks.Add(p[..maxChars]);
                p = p[(maxChars - overlap)..];
            }
            if (current.Length + p.Length + 2 > maxChars && current.Length > 0)
            {
                var done = current.ToString();
                chunks.Add(done);
                current.Clear();
                if (overlap > 0 && done.Length > overlap) current.Append(done[^overlap..]).Append("\n\n");
            }
            current.Append(p).Append("\n\n");
        }
        if (current.ToString().Trim() is { Length: > 0 } rest) chunks.Add(rest);
        return chunks;
    }
}

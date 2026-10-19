namespace Daksh.Ai;

public sealed record MlModelInfo(string Name, string Version, string Runtime, bool IsHeuristicPlaceholder);

/// <summary>
/// Classical ML / on-device-style inference (classification, ranking, anomaly detection), separate from LLM calls.
/// Production adapters: ONNX Runtime or ML.NET, with models versioned and evaluated offline.
/// </summary>
public interface IMlModel<in TInput, TOutput>
{
    MlModelInfo Info { get; }
    Task<TOutput> PredictAsync(TInput input, CancellationToken ct = default);
}

public sealed record EmailFeatures(string Subject, string Snippet, bool FromKnownContact);
public sealed record PriorityPrediction(string Label, double Confidence);

/// <summary>Keyword heuristic standing in for a trained model. Declares itself a placeholder so it is never mistaken for ML.</summary>
public sealed class HeuristicEmailPriorityModel : IMlModel<EmailFeatures, PriorityPrediction>
{
    public MlModelInfo Info { get; } = new("email-priority", "0.0-heuristic", "none", IsHeuristicPlaceholder: true);

    private static readonly string[] Urgent = ["urgent", "asap", "deadline", "overdue", "action required"];

    public Task<PriorityPrediction> PredictAsync(EmailFeatures i, CancellationToken ct = default)
    {
        var text = $"{i.Subject} {i.Snippet}".ToLowerInvariant();
        var hits = Urgent.Count(text.Contains);
        var label = hits > 0 || i.FromKnownContact && text.Contains('?') ? "high" : "normal";
        return Task.FromResult(new PriorityPrediction(label, label == "high" ? 0.6 : 0.5));
    }
}

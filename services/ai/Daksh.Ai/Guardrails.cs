using System.Text.RegularExpressions;

namespace Daksh.Ai;

public enum GuardrailStage { Input, Output }

public sealed record GuardrailOutcome(string Text, IReadOnlyList<string> Flags);

/// <summary>Deterministic safety check applied before content reaches a model and before output reaches the user.</summary>
public interface ITextGuardrail
{
    string Name { get; }
    GuardrailOutcome Apply(string text, GuardrailStage stage, ModelLocality target);
}

/// <summary>Redacts obvious PII from content bound for cloud models.</summary>
public sealed partial class PiiRedactionGuardrail : ITextGuardrail
{
    public string Name => "pii-redaction";

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")] private static partial Regex Email();
    [GeneratedRegex(@"\b(?:\d[ -]?){13,16}\b")] private static partial Regex Card();

    public GuardrailOutcome Apply(string text, GuardrailStage stage, ModelLocality target)
    {
        if (stage != GuardrailStage.Input || target == ModelLocality.Local) return new(text, []);
        var flags = new List<string>();
        var result = Email().Replace(text, _ => { flags.Add("pii:email"); return "[email]"; });
        result = Card().Replace(result, _ => { flags.Add("pii:card"); return "[card]"; });
        return new(result, flags);
    }
}

/// <summary>Flags (does not silently rewrite) common prompt-injection phrasing so callers can restrict tool use.</summary>
public sealed partial class PromptInjectionGuardrail : ITextGuardrail
{
    public string Name => "prompt-injection";

    [GeneratedRegex(@"ignore (all |any )?(previous|prior|above) instructions|reveal (your )?(system )?prompt", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    public GuardrailOutcome Apply(string text, GuardrailStage stage, ModelLocality target) =>
        new(text, stage == GuardrailStage.Input && Pattern().IsMatch(text) ? ["injection:suspected"] : []);
}

using Cronos;

namespace Daksh.Platform.Automation;

public sealed record ScheduleSpec(string Kind, string? Cron, string TimeZone, int? EverySeconds, DateTimeOffset? RunAt);

public static class ScheduleCalculator
{
    public const int MinEverySeconds = 5;

    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"Unknown time zone '{id}'. Use an IANA name such as 'Asia/Kolkata'.");
        }
    }

    public static CronExpression ParseCron(string cron)
    {
        var fields = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        try { return CronExpression.Parse(cron, fields == 6 ? CronFormat.IncludeSeconds : CronFormat.Standard); }
        catch (CronFormatException ex) { throw new ArgumentException($"Invalid cron expression '{cron}': {ex.Message}"); }
    }

    /// <summary>Returns the problems with a schedule, or an empty list when it is valid.</summary>
    public static IReadOnlyList<string> Validate(ScheduleSpec s, DateTimeOffset now)
    {
        var errors = new List<string>();
        try { Zone(s.TimeZone); } catch (ArgumentException ex) { errors.Add(ex.Message); }
        switch (s.Kind)
        {
            case "at":
                if (s.RunAt is null) errors.Add("A one-time schedule needs a run time.");
                break;
            case "every":
                if (s.EverySeconds is null or < MinEverySeconds) errors.Add($"The interval must be at least {MinEverySeconds} seconds.");
                break;
            case "cron":
                if (string.IsNullOrWhiteSpace(s.Cron)) errors.Add("A cron schedule needs an expression.");
                else try { ParseCron(s.Cron); } catch (ArgumentException ex) { errors.Add(ex.Message); }
                break;
            default:
                errors.Add($"Unknown schedule kind '{s.Kind}'. Use at, every or cron.");
                break;
        }
        return errors;
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>. For "every", occurrences stay on the grid that
    /// starts at <paramref name="anchor"/>, so a late run does not shift later ones. Null when there is no further run.
    /// </summary>
    public static DateTimeOffset? Next(ScheduleSpec s, DateTimeOffset after, DateTimeOffset anchor)
    {
        switch (s.Kind)
        {
            case "at":
                return s.RunAt > after ? s.RunAt : null;
            case "every":
                var every = TimeSpan.FromSeconds(Math.Max(MinEverySeconds, s.EverySeconds ?? MinEverySeconds));
                if (after < anchor) return anchor;
                var steps = (long)Math.Floor((after - anchor) / every) + 1;
                return anchor + every * steps;
            case "cron":
                return ParseCron(s.Cron!).GetNextOccurrence(after, Zone(s.TimeZone));
            default:
                return null;
        }
    }

    /// <summary>Delay before attempt number <paramref name="attempt"/> (1-based) is retried: base, 2x, 4x... capped at one hour.</summary>
    public static TimeSpan RetryDelay(int baseSeconds, int attempt) =>
        TimeSpan.FromSeconds(Math.Min(3600, Math.Max(1, baseSeconds) * Math.Pow(2, Math.Max(0, attempt - 1))));
}

/// <summary>
/// Conditions for event and webhook triggers. JSON form: {"all":[...]} | {"any":[...]} | {"not":{...}} |
/// {"path":"a.b","op":"eq|ne|gt|lt|gte|lte|contains|exists|matches","value":...}
/// </summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(System.Text.Json.JsonElement? condition, System.Text.Json.JsonElement payload)
    {
        if (condition is not { ValueKind: System.Text.Json.JsonValueKind.Object } c) return true;
        if (c.TryGetProperty("all", out var all)) return all.EnumerateArray().All(x => Evaluate(x, payload));
        if (c.TryGetProperty("any", out var any)) return any.EnumerateArray().Any(x => Evaluate(x, payload));
        if (c.TryGetProperty("not", out var not)) return !Evaluate(not, payload);
        if (!c.TryGetProperty("path", out var pathEl)) return true;

        System.Text.Json.JsonElement? current = payload;
        foreach (var segment in (pathEl.GetString() ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is { ValueKind: System.Text.Json.JsonValueKind.Object } o && o.TryGetProperty(segment, out var next)) current = next;
            else if (current is { ValueKind: System.Text.Json.JsonValueKind.Array } a && int.TryParse(segment, out var i) && i < a.GetArrayLength()) current = a[i];
            else { current = null; break; }
        }
        var op = c.TryGetProperty("op", out var opEl) ? opEl.GetString() : "eq";
        if (op == "exists") return current is not null && current.Value.ValueKind != System.Text.Json.JsonValueKind.Null;
        if (current is not { } actual || !c.TryGetProperty("value", out var expected)) return false;

        var actualText = actual.ValueKind == System.Text.Json.JsonValueKind.String ? actual.GetString()! : actual.GetRawText();
        var expectedText = expected.ValueKind == System.Text.Json.JsonValueKind.String ? expected.GetString()! : expected.GetRawText();
        var numeric = actual.ValueKind == System.Text.Json.JsonValueKind.Number && expected.ValueKind == System.Text.Json.JsonValueKind.Number;
        double a1 = numeric ? actual.GetDouble() : 0, e1 = numeric ? expected.GetDouble() : 0;
        return op switch
        {
            "eq" => numeric ? a1.Equals(e1) : actualText == expectedText,
            "ne" => numeric ? !a1.Equals(e1) : actualText != expectedText,
            "gt" => numeric && a1 > e1, "lt" => numeric && a1 < e1, "gte" => numeric && a1 >= e1, "lte" => numeric && a1 <= e1,
            "contains" => actualText.Contains(expectedText, StringComparison.OrdinalIgnoreCase),
            "matches" => System.Text.RegularExpressions.Regex.IsMatch(actualText, expectedText,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)),
            _ => false
        };
    }
}

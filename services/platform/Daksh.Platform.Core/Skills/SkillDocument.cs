using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Platform.Security;
using YamlDotNet.Serialization;

namespace Daksh.Platform.Skills;

public sealed record SkillRequirements(IReadOnlyList<string> Bins, IReadOnlyList<string> Env, IReadOnlyList<string> Os, IReadOnlyList<string> Tools);

public sealed record SkillDocument(
    string Name, string Description, string Version, string Body, string Raw, SkillRequirements Requires,
    IReadOnlyDictionary<string, object?> Frontmatter, IReadOnlyList<string> Warnings)
{
    public string FrontmatterJson => JsonSerializer.Serialize(Frontmatter);
}

public sealed class SkillFormatException(IReadOnlyList<string> errors) : Exception(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Reads the SKILL.md format: YAML front matter between "---" lines (name, description, optional version and
/// metadata.daksh.requires) followed by Markdown instructions. A skill is text for the model; nothing in it is executed.
/// </summary>
public static partial class SkillParser
{
    public const int MaxBytes = 64 * 1024;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}$")] private static partial Regex NamePattern();
    [GeneratedRegex(@"^\d+\.\d+\.\d+([-+][0-9A-Za-z.-]+)?$")] private static partial Regex SemVer();

    public static SkillDocument Parse(string raw)
    {
        var errors = new List<string>();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxBytes) errors.Add($"A skill may be at most {MaxBytes / 1024} KB.");
        var text = raw.Replace("\r\n", "\n").TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
            throw new SkillFormatException(["SKILL.md must start with YAML front matter between '---' lines."]);
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) throw new SkillFormatException(["The front matter is not closed with '---'."]);
        var yaml = text[4..end];
        var bodyStart = text.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : text[(bodyStart + 1)..].Trim();

        Dictionary<string, object?> fm;
        try
        {
            var parsed = new DeserializerBuilder().Build().Deserialize<object?>(yaml);
            fm = Normalize(parsed) as Dictionary<string, object?> ?? throw new SkillFormatException(["The front matter must be a YAML mapping."]);
        }
        catch (YamlDotNet.Core.YamlException ex) { throw new SkillFormatException([$"The front matter is not valid YAML: {ex.Message}"]); }

        var name = fm.GetValueOrDefault("name") as string ?? "";
        var description = fm.GetValueOrDefault("description") as string ?? "";
        var version = fm.GetValueOrDefault("version")?.ToString() ?? "1.0.0";
        if (!NamePattern().IsMatch(name)) errors.Add("'name' is required: lower-case letters, digits and hyphens, at most 64 characters.");
        if (string.IsNullOrWhiteSpace(description)) errors.Add("'description' is required.");
        else if (description.Length > 1024) errors.Add("'description' may be at most 1024 characters.");
        if (!SemVer().IsMatch(version)) errors.Add($"'version' must look like 1.2.3 (got '{version}').");
        if (body.Length == 0) errors.Add("The skill has no instructions after the front matter.");
        if (errors.Count > 0) throw new SkillFormatException(errors);

        var requires = Dig(fm, "metadata", "daksh", "requires") as Dictionary<string, object?> ?? Dig(fm, "requires") as Dictionary<string, object?> ?? [];
        var warnings = new List<string>();
        if (Untrusted.LooksLikeInjection(body))
            warnings.Add("The instructions contain text that tries to override rules. Review before enabling.");
        if (Regex.IsMatch(body, @"(?i)\bcurl\b[^\n|]*\|\s*(ba)?sh|\bbase64\s+(-d|--decode)\b"))
            warnings.Add("The instructions tell the agent to download and run code. Review before enabling.");

        return new SkillDocument(name, description.Trim(), version, body, raw,
            new SkillRequirements(Strings(requires, "bins"), Strings(requires, "env"), Strings(requires, "os"),
                Strings(fm, "allowed-tools").Concat(Strings(requires, "tools")).Distinct().ToList()),
            fm, warnings);
    }

    /// <summary>Requirement names that are not met on this host.</summary>
    public static IReadOnlyList<string> Missing(SkillRequirements r, Func<string, bool> binExists, Func<string, bool> envExists,
        string os, IReadOnlyCollection<string> availableTools)
    {
        var missing = new List<string>();
        missing.AddRange(r.Bins.Where(b => !binExists(b)).Select(b => $"program '{b}'"));
        missing.AddRange(r.Env.Where(e => !envExists(e)).Select(e => $"environment variable '{e}'"));
        if (r.Os.Count > 0 && !r.Os.Contains(os, StringComparer.OrdinalIgnoreCase)) missing.Add($"operating system ({string.Join("/", r.Os)})");
        missing.AddRange(r.Tools.Where(t => !availableTools.Contains(t)).Select(t => $"tool '{t}'"));
        return missing;
    }

    private static object? Normalize(object? o) => o switch
    {
        Dictionary<object, object> d => d.ToDictionary(k => k.Key.ToString() ?? "", v => Normalize(v.Value)),
        List<object> l => l.Select(Normalize).ToList(),
        _ => o
    };

    private static object? Dig(Dictionary<string, object?> d, params string[] path)
    {
        object? current = d;
        foreach (var p in path)
        {
            if (current is Dictionary<string, object?> m && m.TryGetValue(p, out var next)) current = next;
            else return null;
        }
        return current;
    }

    private static IReadOnlyList<string> Strings(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) switch
    {
        List<object?> l => l.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList(),
        string s => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
        _ => []
    };
}

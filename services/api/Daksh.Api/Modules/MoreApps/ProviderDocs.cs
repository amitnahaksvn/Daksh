using System.Text;

namespace Daksh.Api.Modules;

/// <summary>
/// Writes the "keys, secrets and setup" section of Amit.md from the app definitions, so the instructions can never disagree with the code
/// (a test fails when they do; run the tests with UPDATE_DOCS=1 to rewrite the section).
/// </summary>
public static class ProviderDocs
{
    public const string Begin = "<!-- BEGIN GENERATED APPS -->";
    public const string End = "<!-- END GENERATED APPS -->";

    private static string Env(string section, string leaf) => $"{section.Replace(':', '_')}__{leaf}";

    public static string Render(IEnumerable<ProviderApp> apps)
    {
        var sb = new StringBuilder();
        foreach (var app in apps.Where(a => a.Setup.Length > 0).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"### {app.Name}").AppendLine();
            sb.AppendLine($"App key `{app.Key}`. {Describe(app)}").AppendLine();
            sb.AppendLine("**Server settings:** " + ServerSettings(app)).AppendLine();
            var n = 1;
            foreach (var step in app.Setup) sb.AppendLine($"{n++}. {step}");
            if (app.Auth == AuthKind.OAuth)
                sb.AppendLine($"{n}. Register the redirect URI `<API address>/api/integrations/{app.Key}/callback` with the provider (many providers require HTTPS).");
            if (!string.IsNullOrWhiteSpace(app.Notes)) sb.AppendLine().AppendLine($"Notes: {app.Notes}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string Describe(ProviderApp a) => (a.Auth, a.HasWrite) switch
    {
        (AuthKind.OAuth, true) => "Sign-in: OAuth. Read-only until full access is turned on.",
        (AuthKind.OAuth, false) => "Sign-in: OAuth. Read-only.",
        (AuthKind.Open, _) => "Free public service: no sign-in and no key. Read-only.",
        (AuthKind.Credentials, true) => "Each person enters their own details. Read-only until full access is turned on.",
        (AuthKind.Credentials, false) => "Each person enters their own details. Read-only.",
        (_, true) => "One server key; nothing to sign in to. Read-only until full access is turned on.",
        _ => "One server key; nothing to sign in to. Read-only.",
    };

    private static string ServerSettings(ProviderApp a)
    {
        switch (a.Auth)
        {
            case AuthKind.OAuth:
                var o = a.OAuth!;
                var ids = $"`{Env(o.Section, "OAuth__ClientId")}`" + (o.SecretOptional ? "" : $", `{Env(o.Section, "OAuth__ClientSecret")}`");
                var reuse = o.Fallbacks.Length > 0 ? $" (or reuse `{Env(o.Fallbacks[0], "OAuth__ClientId")}` / `ClientSecret`)" : "";
                var region = o.AuthUrl.Contains("{region}", StringComparison.Ordinal) ? $", `{Env(o.Section, "Region")}` (default `{o.DefaultRegion}`)" : "";
                return $"{ids}{reuse}{region}; optional `{Env(o.Section, "OAuth__RedirectUri")}` and `{Env(o.Section, "OAuth__FrontendUrl")}`.";
            case AuthKind.ServerKey:
                return string.Join(", ", a.ServerKeys.Select(k => $"`{k.Replace(":", "__")}`")) + ".";
            case AuthKind.Open:
                return "none. Nothing to configure.";
            default:
                return "none. Each person enters: " + string.Join(", ", a.Credentials!.Fields.Select(f => f.Label)) + ".";
        }
    }
}

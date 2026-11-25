using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class GoogleWorkspaceTests
{
    [Fact]
    public void Connecting_Drive_Docs_and_Sheets_asks_for_read_only_permissions_alongside_Gmail()
    {
        var scope = GmailScopes.ForFeatures(GoogleWorkspaceModule.FeaturesFor(["drive", "docs", "sheets", "slides"], write: false));
        Assert.Contains("gmail.readonly", scope);
        Assert.Contains("auth/drive.readonly", scope);
        Assert.Contains("auth/documents.readonly", scope);
        Assert.Contains("auth/spreadsheets.readonly", scope);
        Assert.Contains("auth/presentations.readonly", scope);
        Assert.DoesNotContain("auth/documents ", scope + " ");
        Assert.DoesNotContain("drive.file", scope);
    }

    [Fact]
    public void Full_access_adds_the_create_and_edit_permissions()
    {
        var scope = GmailScopes.ForFeatures(GoogleWorkspaceModule.FeaturesFor(["docs"], write: true));
        Assert.Contains("auth/drive.file", scope);
        Assert.Contains("auth/documents", scope);
        Assert.Contains("auth/spreadsheets", scope);
    }

    [Fact]
    public void Unknown_services_are_dropped()
    {
        Assert.Equal("drive", GoogleWorkspaceModule.FeaturesFor(["drive", "bogus"], false));
    }

    [Fact]
    public void Granted_checks_follow_the_stored_scopes_and_write_implies_read()
    {
        Assert.False(GoogleWorkspaceModule.IsGranted(null, "drive"));
        Assert.False(GoogleWorkspaceModule.IsGranted("https://www.googleapis.com/auth/gmail.readonly", "docs"));
        Assert.True(GoogleWorkspaceModule.IsGranted("https://www.googleapis.com/auth/documents.readonly", "docs"));
        Assert.True(GoogleWorkspaceModule.IsGranted("https://www.googleapis.com/auth/spreadsheets", "sheets")); // edit rights include reading
        Assert.False(GoogleWorkspaceModule.WriteGranted("https://www.googleapis.com/auth/documents.readonly"));
        Assert.True(GoogleWorkspaceModule.WriteGranted("https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/documents https://www.googleapis.com/auth/spreadsheets"));
    }

    [Fact]
    public void Full_Gmail_access_does_not_count_as_Drive_access()
    {
        Assert.False(GoogleWorkspaceModule.IsGranted("https://mail.google.com/", "drive"));
        Assert.True(GmailScopes.Satisfies(["https://mail.google.com/"], GmailScopes.ReadOnly));
    }

    [Fact]
    public void Doc_text_reads_paragraphs_and_table_cells_in_order()
    {
        using var doc = JsonDocument.Parse("""
            [{"paragraph":{"elements":[{"textRun":{"content":"Hello "}},{"textRun":{"content":"world\n"}}]}},
             {"table":{"tableRows":[{"tableCells":[{"content":[{"paragraph":{"elements":[{"textRun":{"content":"A1"}}]}}]},{"content":[{"paragraph":{"elements":[{"textRun":{"content":"B1"}}]}}]}]}]}}]
            """);
        var text = GoogleWorkspaceModule.DocText(doc.RootElement);
        Assert.StartsWith("Hello world", text);
        Assert.True(text.IndexOf("A1", StringComparison.Ordinal) < text.IndexOf("B1", StringComparison.Ordinal));
    }
}

public class SlackTests
{
    private const string Secret = "8f742231b10e8888abcd99yyyzzz85a5";

    private static string Sign(string secret, string timestamp, string body) =>
        "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));

    [Fact]
    public void A_correctly_signed_recent_request_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        Assert.True(SlackIntegrationModule.VerifySignature(Secret, ts, Sign(Secret, ts, "{\"a\":1}"), "{\"a\":1}", now));
    }

    [Fact]
    public void A_wrong_signature_a_changed_body_or_an_old_timestamp_is_refused()
    {
        var now = DateTimeOffset.UtcNow;
        var ts = now.ToUnixTimeSeconds().ToString();
        var sig = Sign(Secret, ts, "body");
        Assert.False(SlackIntegrationModule.VerifySignature(Secret, ts, Sign("another-secret", ts, "body"), "body", now));
        Assert.False(SlackIntegrationModule.VerifySignature(Secret, ts, sig, "tampered", now));
        var old = now.AddMinutes(-10).ToUnixTimeSeconds().ToString();
        Assert.False(SlackIntegrationModule.VerifySignature(Secret, old, Sign(Secret, old, "body"), "body", now)); // replay
        Assert.False(SlackIntegrationModule.VerifySignature(Secret, null, sig, "body", now));
        Assert.False(SlackIntegrationModule.VerifySignature("", ts, sig, "body", now));
    }

    [Fact]
    public void Reading_is_the_default_and_full_access_adds_posting()
    {
        var read = SlackScopes.UserScope(false);
        Assert.Contains("channels:history", read);
        Assert.Contains("search:read", read);
        Assert.DoesNotContain("chat:write", read);
        Assert.False(SlackScopes.CanWrite(read));
        var full = SlackScopes.UserScope(true);
        Assert.Contains("chat:write", full);
        Assert.True(SlackScopes.CanWrite(full));
    }

    [Fact]
    public void The_authorization_url_asks_for_user_scopes_and_carries_the_state()
    {
        var url = SlackIntegrationModule.BuildAuthorizationUrl(new SlackIntegrationModule.Settings("123.456", "secret", "https://api.example.com/api/integrations/slack/callback", "https://app.example.com/apps/slack"), "state-1", false);
        Assert.StartsWith("https://slack.com/oauth/v2/authorize?", url);
        Assert.Contains("client_id=123.456", url);
        Assert.Contains("user_scope=", url);
        Assert.Contains("state=state-1", url);
        Assert.DoesNotContain("secret", url);
    }

    [Fact]
    public void Slack_markup_becomes_plain_text()
    {
        var text = SlackText.Clean("Hi <@U123>, see <#C1|general> and <https://example.com|the docs> or <https://x.test> &amp; <!here>", id => id == "U123" ? "Priya" : null);
        Assert.Equal("Hi @Priya, see #general and the docs (https://example.com) or https://x.test & @here", text);
    }
}

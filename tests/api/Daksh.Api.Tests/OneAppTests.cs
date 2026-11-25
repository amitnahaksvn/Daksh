using System.Text.Json;
using Daksh.Api.Modules;

namespace Daksh.Api.Tests;

public class OneAppTests
{
    [Fact]
    public void GitHub_reads_by_default_and_private_repositories_or_full_access_add_permissions()
    {
        var read = GitHubModule.ScopesFor(write: false, priv: false);
        Assert.Contains("notifications", read);
        Assert.DoesNotContain("repo", read.Split(' '));
        Assert.DoesNotContain("public_repo", read);
        Assert.Contains("public_repo", GitHubModule.ScopesFor(write: true, priv: false));
        var priv = GitHubModule.ScopesFor(write: true, priv: true).Split(' ');
        Assert.Contains("repo", priv);
        Assert.DoesNotContain("public_repo", priv); // repo already covers it
    }

    [Fact]
    public void The_GitHub_sign_in_url_carries_the_state_and_scopes()
    {
        var url = GitHubModule.BuildAuthorizationUrl(new OAuthConfig("cid", "secret", "https://api.example.com/api/integrations/github/callback", "https://app.example.com/apps/github"), "st8", false, false);
        Assert.StartsWith("https://github.com/login/oauth/authorize?", url);
        Assert.Contains("client_id=cid", url);
        Assert.Contains("state=st8", url);
        Assert.DoesNotContain("secret", url);
    }

    [Fact]
    public void Spotify_is_read_only_by_default_and_uses_PKCE()
    {
        Assert.DoesNotContain("user-modify-playback-state", SpotifyModule.ScopesFor(false));
        Assert.Contains("user-modify-playback-state", SpotifyModule.ScopesFor(true));
        var url = SpotifyModule.BuildAuthorizationUrl(new OAuthConfig("cid", null, "http://127.0.0.1:5080/api/integrations/spotify/callback", "http://localhost:3000/apps/spotify"), "st8", "chal", false);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=chal", url);
        Assert.DoesNotContain("client_secret", url);
    }

    [Fact]
    public void Notion_sign_in_asks_for_a_user_owned_integration()
    {
        var url = NotionModule.BuildAuthorizationUrl(new OAuthConfig("cid", "secret", "https://api.example.com/api/integrations/notion/callback", "https://app.example.com/apps/notion"), "st8");
        Assert.StartsWith("https://api.notion.com/v1/oauth/authorize?", url);
        Assert.Contains("owner=user", url);
        Assert.Contains("state=st8", url);
    }

    [Fact]
    public void Sign_in_urls_must_be_https_or_loopback()
    {
        Assert.True(AppConnections.IsAllowedUri("https://example.com/cb"));
        Assert.True(AppConnections.IsAllowedUri("http://localhost:5080/cb"));
        Assert.True(AppConnections.IsAllowedUri("http://127.0.0.1:5080/cb"));
        Assert.False(AppConnections.IsAllowedUri("http://example.com/cb"));
        Assert.False(AppConnections.IsAllowedUri("javascript:alert(1)"));
    }

    [Fact]
    public void The_code_challenge_matches_the_RFC_7636_example()
    {
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", AppConnections.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public void Settings_are_checked_against_each_apps_specs()
    {
        var count = GitHubModule.Info.Settings.First(s => s.Key == "itemCount");
        Assert.Null(AppConnections.Validate(count, Json("5")));
        Assert.NotNull(AppConnections.Validate(count, Json("2")));
        Assert.NotNull(AppConnections.Validate(count, Json("\"5\"")));
        var source = GitHubModule.Info.Settings.First(s => s.Key == "dashboardSource");
        Assert.Null(AppConnections.Validate(source, Json("\"assigned\"")));
        Assert.NotNull(AppConnections.Validate(source, Json("\"everything\"")));
        var flag = GitHubModule.Info.Settings.First(s => s.Key == "hideDrafts");
        Assert.Null(AppConnections.Validate(flag, Json("true")));
        Assert.NotNull(AppConnections.Validate(flag, Json("\"yes\"")));
        var watched = DiscordModule.Info.Settings.First(s => s.Key == "watchedChannels");
        Assert.Null(AppConnections.Validate(watched, Json("\"123456789012345678\"")));
        Assert.NotNull(AppConnections.Validate(watched, Json("\"" + new string('1', 200) + "\"")));
    }

    [Fact]
    public void Every_apps_defaults_pass_their_own_validation()
    {
        foreach (var info in new[] { GitHubModule.Info, NotionModule.Info, DiscordModule.Info, SpotifyModule.Info })
            foreach (var spec in info.Settings)
            {
                var json = spec.Kind switch { "int" => spec.Default, "bool" => spec.Default, _ => JsonSerializer.Serialize(spec.Default) };
                Assert.Null(AppConnections.Validate(spec, Json(json)));
            }
    }

    [Fact]
    public void Discord_and_Notion_full_access_is_a_Daksha_switch_while_GitHub_and_Spotify_need_a_new_sign_in()
    {
        Assert.True(DiscordModule.Info.SoftWrite);
        Assert.True(NotionModule.Info.SoftWrite);
        Assert.False(GitHubModule.Info.SoftWrite);
        Assert.False(SpotifyModule.Info.SoftWrite);
    }
}

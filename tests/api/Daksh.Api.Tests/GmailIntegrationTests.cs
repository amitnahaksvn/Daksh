using Daksh.Api.Modules;
using Microsoft.AspNetCore.WebUtilities;

namespace Daksh.Api.Tests;

public class GmailIntegrationTests
{
    [Fact]
    public void Authorization_url_requests_read_and_send_scopes_with_state_and_pkce()
    {
        var url = GmailIntegrationModule.BuildAuthorizationUrl(
            "client-id",
            "http://localhost:5080/api/integrations/gmail/callback",
            "csrf-state",
            "pkce-challenge");
        var query = QueryHelpers.ParseQuery(new Uri(url).Query);

        Assert.Equal("client-id", query["client_id"]);
        Assert.Equal("csrf-state", query["state"]);
        Assert.Equal("pkce-challenge", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("offline", query["access_type"]);
        Assert.Contains("https://www.googleapis.com/auth/gmail.readonly", query["scope"].ToString());
        Assert.Contains("https://www.googleapis.com/auth/gmail.send", query["scope"].ToString());
        Assert.DoesNotContain("password", query.Keys);
    }
}

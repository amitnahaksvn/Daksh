using System.Net;
using System.Net.Http.Json;
using Daksh.TestSupport;

namespace Daksh.Api.Tests;

// The assistant endpoints are pending the MongoDB rebuild (see AssistantModule) and currently return 501
// for any well-formed request; input validation still runs first.
public class AssistantTests : IClassFixture<DakshApiFactory>
{
    private readonly DakshApiFactory _f;
    public AssistantTests(DakshApiFactory f) => _f = f;

    [Fact]
    public async Task Message_returns_not_implemented_pending_mongodb_rebuild()
    {
        var client = _f.ClientFor("alice");
        var res = await client.PostAsJsonAsync("/api/v1/assistant/messages", new { message = "hello daksh" });
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_message_is_rejected_with_problem_details(string message)
    {
        var res = await _f.ClientFor("alice").PostAsJsonAsync("/api/v1/assistant/messages", new { message });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("Validation failed", body);
    }
}

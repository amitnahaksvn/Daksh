using System.Net;
using System.Net.Http.Json;
using Daksh.Domain;
using Daksh.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Api.Tests;

public class AssistantTests : IClassFixture<DakshApiFactory>
{
    private readonly DakshApiFactory _f;
    public AssistantTests(DakshApiFactory f) => _f = f;

    private sealed record Reply(Guid ConversationId, string Reply_, string Provider, bool IsSimulated);

    [Fact]
    public async Task Message_returns_simulated_reply_and_records_audit_event()
    {
        var client = _f.ClientFor("alice");
        var res = await client.PostAsJsonAsync("/api/v1/assistant/messages", new { message = "hello daksh" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("mock", json.GetProperty("provider").GetString());
        Assert.True(json.GetProperty("isSimulated").GetBoolean());
        Assert.Contains("hello daksh", json.GetProperty("reply").GetString());

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DakshDbContext>();
        var audit = await db.AuditEvents.SingleAsync(a => a.UserId == "alice" && a.Action == "assistant.message");
        Assert.Equal(json.GetProperty("conversationId").GetString(), audit.EntityId);
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

    [Fact]
    public async Task Conversations_are_isolated_per_user()
    {
        var a = _f.ClientFor("user-a");
        var created = await (await a.PostAsJsonAsync("/api/v1/assistant/messages", new { message = "private" }))
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = created.GetProperty("conversationId").GetString();

        var other = await _f.ClientFor("user-b").GetAsync($"/api/v1/assistant/conversations/{id}/messages");
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        var own = await a.GetAsync($"/api/v1/assistant/conversations/{id}/messages");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }
}

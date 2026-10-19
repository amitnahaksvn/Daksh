using System.Net;
using System.Net.Http.Json;
using Daksh.TestSupport;

namespace Daksh.Api.Tests;

public class PlatformTests : IClassFixture<DakshApiFactory>
{
    private readonly DakshApiFactory _f;
    public PlatformTests(DakshApiFactory f) => _f = f;

    [Fact]
    public async Task Liveness_is_healthy()
    {
        var res = await _f.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Task_lifecycle_and_validation()
    {
        var c = _f.ClientFor("task-user");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/v1/tasks", new { title = "" })).StatusCode);

        var created = await c.PostAsJsonAsync("/api/v1/tasks", new { title = "Write report" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetString();

        var done = await c.PostAsync($"/api/v1/tasks/{id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.ClientFor("other").PostAsync($"/api/v1/tasks/{id}/complete", null)).StatusCode);
    }

    [Fact]
    public async Task Past_reminder_is_rejected()
    {
        var res = await _f.ClientFor("r").PostAsJsonAsync("/api/v1/reminders",
            new { text = "x", remindAt = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Privacy_defaults_to_everything_off_and_updates_are_audited()
    {
        var c = _f.ClientFor("priv");
        var def = await c.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/privacy");
        Assert.False(def.GetProperty("memoryEnabled").GetBoolean());
        Assert.False(def.GetProperty("allowProviderDataSharing").GetBoolean());

        var put = await c.PutAsJsonAsync("/api/v1/privacy", new { memoryEnabled = true, allowProviderDataSharing = false, retentionDays = 30 });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var audit = await c.GetStringAsync("/api/v1/audit-events");
        Assert.Contains("privacy.update", audit);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync("/api/v1/privacy", new { memoryEnabled = true, allowProviderDataSharing = false, retentionDays = 0 })).StatusCode);
    }

    [Fact]
    public async Task Integrations_are_reported_as_mock_never_verified()
    {
        var json = await _f.ClientFor("i").GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/integrations");
        Assert.True(json.GetArrayLength() >= 8);
        foreach (var i in json.EnumerateArray())
            Assert.Equal("Mock", i.GetProperty("state").GetString());
    }
}

namespace Daksh.Gateway.Tests;

/// <summary>
/// Automated equivalent of tests/manual/e2e_restart_recovery.py (steps 15-16 of the required scenario):
/// a job becomes due while nothing is polling for it, then JobRunnerWorker's catch-up poll runs it once
/// something does poll again.
///
/// The manual script needs a real external Gateway process kill/restart because it has no other way to
/// make "the Gateway was down" happen. Here, the Gateway host (and JobRunnerWorker's BackgroundService)
/// never actually stops - but that is not what the catch-up behaviour depends on. JobRunnerWorker's poll
/// query (`("Status" = 'Pending' AND "RunAfter" &lt;= now)`) claims a due job regardless of how long it has
/// been overdue; it does not distinguish "ran one poll cycle late" from "ran ten minutes late because the
/// whole process was down". So the real bug this scenario step guards - a job silently never running again
/// after being missed - is exercised just as faithfully by scheduling a job and then not touching it while
/// real wall-clock time passes across one or more of JobRunnerWorker's own poll cycles, which is the simpler
/// approach the project's own notes call for over building new infrastructure to literally pause/resume a
/// hosted service. GatewayFactory sets Scheduler:PollIntervalSeconds=1 only to keep the test fast; the
/// catch-up query itself is untouched.
/// </summary>
[Collection(nameof(GatewayCollection))]
public sealed class RestartRecoveryTests(GatewayFactory factory)
{
    [Fact]
    public async Task Catch_up_poll_runs_a_job_that_became_due_while_unattended()
    {
        var http = factory.CreateClient();
        var api = new ApiClient(http);
        var runId = DateTimeOffset.UtcNow.Ticks;

        var (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/auth/register", body: new
        {
            email = $"restart{runId}@example.com", password = "Sup3rSecret!",
            displayName = "Restart Tester", workspaceName = "Restart Co",
        });
        b = ApiClient.Expect("register/login", s, b);
        var token = b.GetStringProp("accessToken");
        var ws = b.GetGuidProp("workspaceId").ToString();

        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/providers", token, ws, new { name = "local", kind = "scripted" });
        b = ApiClient.Expect("configure provider", s, b);
        var providerId = b.GetGuidProp("id");

        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/providers/{providerId}/models", token, ws, new
        {
            modelId = "research", contextWindow = 16000, maxOutputTokens = 2000,
            tools = true, structuredOutput = true, embeddings = false,
            inputPricePerMTok = 0, outputPricePerMTok = 0,
        });
        ApiClient.Expect("add research model", s, b);

        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/agents", token, ws, new
        {
            name = "Restart Agent", description = "Used to test scheduler restart recovery",
            defaultModel = "local/research", toolProfile = "coding",
            systemInstructions = "You are a careful assistant.",
        });
        b = ApiClient.Expect("create agent", s, b);
        var agentId = b.GetGuidProp("id");

        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/sessions", token, ws, new { agentId, title = "Restart recovery session" });
        b = ApiClient.Expect("create session", s, b);
        var sessionId = b.GetGuidProp("id");

        // Schedule the job 3 seconds out. Nothing in this test touches JobRunnerWorker's poll loop between
        // here and the assertion below - it either catches the job on a normal cycle (RunAfter just passed)
        // or, if scheduling/CI jitter delays this test, on a later cycle once it's well overdue; either way
        // proves the same catch-up behaviour the manual script's external restart was exercising.
        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages", token, ws,
            new { text = "Schedule a reminder to check on this in 3 seconds." });
        b = ApiClient.Expect("ask to schedule a task", s, b);
        var executionId = b.GetGuidProp("executionId");

        bool scheduled = false;
        for (var i = 0; i < 60; i++)
        {
            var (es, eb) = await api.CallAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId}/executions/{executionId}", token, ws);
            if (es == 200)
            {
                var status = eb.GetStringProp("status");
                if (status is "Succeeded" or "Failed" or "Cancelled")
                {
                    Assert.Equal("Succeeded", status);
                    scheduled = true;
                    break;
                }
            }
            await Task.Delay(500);
        }
        Assert.True(scheduled, "timed out waiting for the scheduling execution");

        (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/automations", token, ws);
        var automations = ApiClient.Expect("list automations", s, b);
        Assert.True(automations.GetArrayLength() >= 1, $"expected an automation to have been created, got {automations}");
        var firstAutomation = automations.EnumerateArray().First();
        var automationId = firstAutomation.GetGuidProp("id");
        Assert.True(firstAutomation.GetProperty("lastRunAt").ValueKind == System.Text.Json.JsonValueKind.Null,
            $"expected the automation not to have run yet, got {firstAutomation}");

        // Deliberately do nothing for longer than the 3-second schedule, so the job becomes overdue while
        // unattended - the equivalent of the manual script's Gateway-down window - before checking on it.
        await Task.Delay(TimeSpan.FromSeconds(4));

        bool ran = false;
        for (var i = 0; i < 40; i++)
        {
            (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/automations", token, ws);
            var list = ApiClient.Expect("list automations", s, b, 200);
            var automation = list.EnumerateArray().FirstOrDefault(a => a.GetGuidProp("id") == automationId);
            if (automation.ValueKind != System.Text.Json.JsonValueKind.Undefined &&
                automation.GetProperty("lastRunAt").ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                Assert.Equal(0, automation.GetProperty("consecutiveFailures").GetInt32());
                ran = true;
                break;
            }
            await Task.Delay(1000);
        }
        Assert.True(ran, "timed out waiting for the automation to run after it became overdue");
    }
}

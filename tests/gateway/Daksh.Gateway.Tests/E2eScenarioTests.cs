using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Automated equivalent of tests/manual/e2e_scenario.py: drives the full required 16-step scenario (steps
/// 1-14 here; 15-16 are the restart-recovery steps covered by <see cref="RestartRecoveryTests"/>) against
/// an in-process Gateway and a real Testcontainers-hosted PostgreSQL + pgvector database. Every HTTP call
/// and assertion mirrors the Python script's, so this is a direct translation, not a weaker rewrite.
/// </summary>
[Collection(nameof(GatewayCollection))]
public sealed class E2eScenarioTests(GatewayFactory factory)
{
    [Fact]
    public async Task Full_scenario_register_through_usage_summary()
    {
        var http = factory.CreateClient();
        var api = new ApiClient(http);
        var runId = DateTimeOffset.UtcNow.Ticks;

        // 1. A user logs into the platform (register = first login).
        var (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/auth/register", body: new
        {
            email = $"researcher{runId}@example.com", password = "Sup3rSecret!",
            displayName = "Researcher", workspaceName = "Research Co",
        });
        b = ApiClient.Expect("1. register/login", s, b);
        var token = b.GetStringProp("accessToken");
        var ws = b.GetGuidProp("workspaceId").ToString();

        // 2-3. Configure a scripted provider with a research model and an embedding model, then create the agent.
        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/providers", token, ws, new { name = "local", kind = "scripted" });
        b = ApiClient.Expect("3a. configure provider", s, b);
        var providerId = b.GetGuidProp("id");

        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/providers/{providerId}/models", token, ws, new
        {
            modelId = "research", contextWindow = 16000, maxOutputTokens = 2000,
            tools = true, structuredOutput = true, embeddings = false,
            inputPricePerMTok = 0, outputPricePerMTok = 0,
        });
        ApiClient.Expect("3b. add research model", s, b);

        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/providers/{providerId}/models", token, ws, new
        {
            modelId = "hash-embedding", contextWindow = 0, maxOutputTokens = 0,
            embeddings = true, inputPricePerMTok = 0, outputPricePerMTok = 0,
        });
        ApiClient.Expect("3c. add embedding model", s, b);

        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/agents", token, ws, new
        {
            name = "AI Research Agent", description = "Researches topics and remembers findings",
            defaultModel = "local/research", toolProfile = "coding",
            systemInstructions = "You are a careful research assistant.",
        });
        b = ApiClient.Expect("2. create research agent", s, b);
        var agentId = b.GetGuidProp("id");

        // 4-8. The user asks the agent to research a topic; it searches, fetches, summarizes, and saves memory.
        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/sessions", token, ws, new { agentId, title = "Research session" });
        b = ApiClient.Expect("4a. create session", s, b);
        var sessionId = b.GetGuidProp("id");

        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages", token, ws,
            new { text = "Please research the topic of pgvector and summarize how it works." });
        b = ApiClient.Expect("4b. send research request", s, b);
        var executionId = b.GetGuidProp("executionId");

        var result1 = await WaitForExecutionAsync(api, token, ws, sessionId, executionId, "5-8. agent searches web, fetches, summarizes, saves memory");

        (s, b) = await api.CallAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId}/messages", token, ws);
        var messages = ApiClient.Expect("8b. read transcript", s, b);
        var toolNames = ToolNamesOf(messages);
        Assert.Contains("web_search", toolNames);
        Assert.Contains("web_fetch", toolNames);
        Assert.Contains("memory_write", toolNames);

        // 9-10. A follow-up question; the agent retrieves the stored memory via memory_search, not a new search.
        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages", token, ws,
            new { text = "What did you find out about pgvector earlier?" });
        b = ApiClient.Expect("9. follow-up question", s, b);
        var executionId2 = b.GetGuidProp("executionId");
        await WaitForExecutionAsync(api, token, ws, sessionId, executionId2, "10. agent recalls memory");

        (s, b) = await api.CallAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId}/messages?after=0", token, ws);
        var messages2 = ApiClient.Expect("10b. read transcript", s, b);
        Assert.Contains("memory_search", ToolNamesOf(messages2));

        // 11-12. Ask to schedule a future task; the scheduler persists an automation.
        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/messages", token, ws,
            new { text = "Schedule a reminder to check on this research in 5 seconds." });
        b = ApiClient.Expect("11. ask to schedule a task", s, b);
        var executionId3 = b.GetGuidProp("executionId");
        await WaitForExecutionAsync(api, token, ws, sessionId, executionId3, "12. scheduler persists the job");

        (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/automations", token, ws);
        var automations = ApiClient.Expect("12b. list automations", s, b);
        Assert.True(automations.GetArrayLength() >= 1, "expected at least one automation to have been created");

        // 13. Deliver a message through the mock channel adapter and confirm the reply comes back out.
        (s, b) = await api.CallAsync(HttpMethod.Post, "/api/v1/testing/mock-channel/accounts", token, ws,
            new { name = $"e2e-mock-{runId}", defaultAgentId = agentId });
        b = ApiClient.Expect("13a. create mock channel account", s, b);
        var mockAccountId = b.GetGuidProp("accountId");

        (s, b) = await api.CallAsync(HttpMethod.Post, $"/api/v1/testing/mock-channel/accounts/{mockAccountId}/inbound", token, ws, new
        {
            chatId = "chat-1", senderId = "user-1", senderName = "Alice", text = "Hello from the mock channel",
        });
        b = ApiClient.Expect("13b. inject inbound channel message", s, b);
        Assert.Equal("routed", b.GetStringProp("status"));
        var channelSessionId = b.GetGuidProp("sessionId");
        var channelExecutionId = b.GetGuidProp("executionId");
        await WaitForExecutionAsync(api, token, ws, channelSessionId, channelExecutionId, "13c. channel execution completes");

        // ChannelOutboxWorker delivers asynchronously (polls every 500ms); give it a moment to catch up.
        JsonElement outbound = default;
        for (var i = 0; i < 20; i++)
        {
            (s, outbound) = await api.CallAsync(HttpMethod.Get, $"/api/v1/testing/mock-channel/accounts/{mockAccountId}/outbound", token, ws);
            outbound = ApiClient.Expect("13d. read outbound channel messages", s, outbound);
            if (outbound.GetArrayLength() > 0) break;
            await Task.Delay(500);
        }
        Assert.True(outbound.GetArrayLength() >= 1, $"expected at least one outbound message delivered through the channel, got {outbound}");

        // 14. Usage/cost is queryable.
        (s, b) = await api.CallAsync(HttpMethod.Get, "/api/v1/usage/summary", token, ws);
        var usage = ApiClient.Expect("14. usage summary queryable", s, b);
        Assert.True(usage.GetProperty("calls").GetInt32() >= 1, $"expected at least one usage record, got {usage}");
    }

    private static List<string> ToolNamesOf(JsonElement messages) =>
        messages.EnumerateArray()
            .Select(m => m.GetStringOrNull("toolName"))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

    private static async Task<JsonElement> WaitForExecutionAsync(ApiClient api, string token, string ws, Guid sessionId, Guid executionId, string label, int timeoutSeconds = 30)
    {
        for (var i = 0; i < timeoutSeconds * 2; i++)
        {
            var (s, b) = await api.CallAsync(HttpMethod.Get, $"/api/v1/sessions/{sessionId}/executions/{executionId}", token, ws);
            if (s == 200)
            {
                var status = b.GetStringProp("status");
                if (status is "Succeeded" or "Failed" or "Cancelled" or "WaitingApproval")
                {
                    if (status != "Succeeded")
                        throw new Xunit.Sdk.XunitException($"[FAIL] {label}: status={status}\n{b}");
                    return b;
                }
            }
            await Task.Delay(500);
        }
        throw new Xunit.Sdk.XunitException($"{label}: timed out waiting for execution {executionId}");
    }
}

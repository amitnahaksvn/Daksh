using System.Text.Json;
using Daksh.Platform.Providers;

namespace Daksh.Platform.Tests.Providers;

public sealed class ResearchBehaviorTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("{}").RootElement;

    private static ToolSpec Tool(string name) => new(name, name, EmptySchema);

    private static ChatRequest Request(string userText, params string[] availableTools) =>
        new("research", [new ChatMessage(ChatRole.User, userText)], availableTools.Select(Tool).ToList());

    [Fact]
    public void Respond_RecallPhrasingThatAlsoMatchesResearchWords_CallsMemorySearch_NotWebSearch()
    {
        // Regression test for the bug fixed this session: "find out about" is part of the research-intent regex,
        // so a natural follow-up like "What did you find out about X earlier?" used to re-trigger a brand new
        // web_search instead of a memory_search recall, because ResearchIntent was checked before RecallIntent.
        var behavior = new ResearchBehavior();
        var request = Request("What did you find out about quantum computing earlier?", "web_search", "web_fetch", "memory_write", "memory_search");

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("memory_search", response.ToolCalls[0].Name);
    }

    [Fact]
    public void Respond_RecallPhrasing_WithoutMemorySearchTool_FallsThroughToResearch()
    {
        // If memory_search genuinely isn't offered, recall intent cannot be served, so it's fine (and expected)
        // for the research path to take over instead -- this confirms the ordering fix didn't break that fallback.
        var behavior = new ResearchBehavior();
        var request = Request("What did you find out about quantum computing earlier?", "web_search", "web_fetch");

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("web_search", response.ToolCalls[0].Name);
    }

    [Fact]
    public void Respond_PlainRecallPhrasing_CallsMemorySearch()
    {
        var behavior = new ResearchBehavior();
        var request = Request("Do you remember what we discussed about the project budget?", "memory_search");

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("memory_search", response.ToolCalls[0].Name);
    }

    [Fact]
    public void Respond_PlainResearchIntent_CallsWebSearch()
    {
        var behavior = new ResearchBehavior();
        var request = Request("Please research the history of the Roman Empire", "web_search");

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("web_search", response.ToolCalls[0].Name);
        var args = JsonDocument.Parse(response.ToolCalls[0].Arguments).RootElement;
        Assert.Contains("Roman Empire", args.GetProperty("query").GetString());
    }

    [Fact]
    public void Respond_ScheduleIntent_CallsScheduleCreate()
    {
        var behavior = new ResearchBehavior();
        var request = Request("Remind me every 10 minutes to stretch", "schedule_create");

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("schedule_create", response.ToolCalls[0].Name);
        var args = JsonDocument.Parse(response.ToolCalls[0].Arguments).RootElement;
        Assert.Equal("every", args.GetProperty("kind").GetString());
        Assert.Equal(600, args.GetProperty("every_seconds").GetInt32());
    }

    [Fact]
    public void Respond_ScheduleIntent_OneTimeDelay_CallsScheduleCreateWithAtKind()
    {
        var behavior = new ResearchBehavior();
        var request = Request("Schedule a reminder in 30 minutes to check the deploy", "schedule_create");

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        var args = JsonDocument.Parse(response.ToolCalls[0].Arguments).RootElement;
        Assert.Equal("at", args.GetProperty("kind").GetString());
        Assert.Equal(1800, args.GetProperty("in_seconds").GetInt32());
    }

    [Fact]
    public void Respond_NoIntentMatchesAndNoToolsAvailable_EchoesPlainText()
    {
        var behavior = new ResearchBehavior();
        var request = Request("Just saying hello, nothing special here.");

        var response = behavior.Respond(request);

        Assert.Empty(response.ToolCalls);
        Assert.Contains("hello", response.Text);
    }

    [Fact]
    public void Respond_AfterWebSearchToolResult_CallsWebFetchOnFirstUrl()
    {
        var behavior = new ResearchBehavior();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "research quantum computing"),
            new(ChatRole.Assistant, null, [new ToolCall("call_1", "web_search", "{\"query\":\"quantum computing\"}")]),
            new(ChatRole.Tool, "Found: https://example.com/quantum some trailing text.", ToolCallId: "call_1", ToolName: "web_search"),
        };
        var request = new ChatRequest("research", messages, [Tool("web_search"), Tool("web_fetch")]);

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("web_fetch", response.ToolCalls[0].Name);
        var args = JsonDocument.Parse(response.ToolCalls[0].Arguments).RootElement;
        Assert.Equal("https://example.com/quantum", args.GetProperty("url").GetString());
    }

    [Fact]
    public void Respond_AfterWebFetchToolResult_CallsMemoryWrite()
    {
        var behavior = new ResearchBehavior();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "research quantum computing"),
            new(ChatRole.Assistant, null, [new ToolCall("call_2", "web_fetch", "{\"url\":\"https://example.com/quantum\"}")]),
            new(ChatRole.Tool, "Quantum computing uses qubits.", ToolCallId: "call_2", ToolName: "web_fetch"),
        };
        var request = new ChatRequest("research", messages, [Tool("web_fetch"), Tool("memory_write")]);

        var response = behavior.Respond(request);

        Assert.Single(response.ToolCalls);
        Assert.Equal("memory_write", response.ToolCalls[0].Name);
    }
}

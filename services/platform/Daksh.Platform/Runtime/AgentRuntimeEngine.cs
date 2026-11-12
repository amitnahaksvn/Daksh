using System.Text.Json;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Memory;
using Daksh.Platform.Providers;
using Daksh.Platform.Security;
using Daksh.Platform.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Runtime;

public sealed record RunOutcome(ExecutionStatus Status, string? FinalText, string? Error);

/// <summary>
/// Drives one execution from its current step to completion, interruption (approval needed) or failure. Every
/// message, tool call and token count is written to the database before the next step starts, so a crash loses at
/// most the in-flight model call: the worker that claims the lease afterwards resumes from the last saved step.
/// </summary>
public sealed class AgentRuntimeEngine(
    PlatformDbContext db, ModelCallRunner models, MemoryService memory, ToolRegistry toolRegistry,
    IExecutionEventSink events, Infrastructure.IClock clock, IServiceProvider services, ILogger<AgentRuntimeEngine> logger)
{
    public async Task<RunOutcome> RunAsync(Guid executionId, CancellationToken ct)
    {
        var execution = await db.Executions.FindAsync([executionId], ct) ?? throw new InvalidOperationException("Execution not found.");
        var session = await db.Sessions.FindAsync([execution.SessionId], ct) ?? throw new InvalidOperationException("Session not found.");
        var agent = await db.Agents.Include(a => a.Configuration).Include(a => a.Instructions.Where(i => i.Enabled))
            .FirstOrDefaultAsync(a => a.Id == execution.AgentId, ct) ?? throw new InvalidOperationException("Agent not found.");
        var config = agent.Configuration ?? throw new InvalidOperationException("Agent has no configuration.");

        execution.Status = ExecutionStatus.Running;
        execution.StartedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "run.started", new { }, ct);

        var loopDetector = new LoopDetector();
        var toolCalls = await db.ToolExecutions.Where(t => t.ExecutionId == execution.Id && t.Status == ToolExecutionStatus.Succeeded).ToListAsync(ct);
        loopDetector.Restore(toolCalls.Select(t => (t.ToolName, t.Arguments)));

        try
        {
            while (execution.Step < config.Limits.MaxSteps)
            {
                if (execution.CancelRequested) return await FinishAsync(execution, session, ExecutionStatus.Cancelled, null, "Cancelled by user.", ct);

                // A tool call left WaitingApproval by a previous pass: resume it if it has been decided.
                var pending = await db.ToolExecutions.Where(t => t.ExecutionId == execution.Id && t.Status == ToolExecutionStatus.WaitingApproval).ToListAsync(ct);
                if (pending.Count > 0)
                {
                    var resumed = await ResumePendingApprovalsAsync(execution, session, agent, config, pending, ct);
                    if (!resumed) return await FinishAsync(execution, session, ExecutionStatus.WaitingApproval, null, null, ct);
                    continue;
                }

                var (messages, promptTokens) = await BuildPromptAsync(session, agent, config, execution, ct);
                if (ContextBudget.ShouldCompact(promptTokens, await ContextWindowAsync(execution, config, ct), config.Session.CompactionThreshold))
                    await CompactAsync(session, config, execution, ct);

                var toolSpecs = await BuildToolSpecsAsync(agent, config, ct);
                var modelRefs = string.IsNullOrEmpty(execution.ModelOverride)
                    ? [config.DefaultModel ?? throw new InvalidOperationException("Agent has no default model."), .. config.FallbackModels]
                    : new List<string> { execution.ModelOverride };

                var request = new ChatRequest(modelRefs[0], messages, toolSpecs.Count > 0 ? toolSpecs : null, config.Temperature, config.Limits.MaxOutputTokens);
                ChatResponse? response = null;
                string usedModel = modelRefs[0];
                var textSoFar = "";

                await foreach (var (evt, modelRef) in models.StreamWithFallbackAsync(execution.WorkspaceId, modelRefs, request, ct))
                {
                    switch (evt)
                    {
                        case TextDelta d:
                            textSoFar += d.Text;
                            await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "text.delta", new { text = d.Text }, ct);
                            break;
                        case ToolCallStarted t:
                            await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "tool.started", new { name = t.Name }, ct);
                            break;
                        case ChatCompleted c:
                            response = c.Response;
                            usedModel = modelRef;
                            break;
                    }
                }
                if (response is null) throw new InvalidOperationException("Model stream ended without a completion.");

                await RecordUsageAsync(execution, session, agent, usedModel, response.Usage, ct);
                execution.ModelUsed = usedModel;
                execution.Step++;

                var assistantMessage = await AppendMessageAsync(session, execution, "assistant", response.Text, usedModel,
                    response.ToolCalls.Count > 0 ? JsonSerializer.Serialize(response.ToolCalls, JsonDefaults.Options) : null, null, null, false, ct);

                if (response.ToolCalls.Count == 0)
                    return await FinishAsync(execution, session, ExecutionStatus.Succeeded, response.Text, null, ct);

                if (response.ToolCalls.Count > config.Limits.MaxToolCallsPerStep)
                    return await FinishAsync(execution, session, ExecutionStatus.Failed, null,
                        $"The model requested {response.ToolCalls.Count} tool calls in one step; the limit is {config.Limits.MaxToolCallsPerStep}.", ct);

                var stop = await ExecuteToolCallsAsync(execution, session, agent, config, response.ToolCalls, loopDetector, ct);
                if (stop is not null) return await FinishAsync(execution, session, stop.Status, stop.FinalText, stop.Error, ct);
            }
            return await FinishAsync(execution, session, ExecutionStatus.Failed, null, $"The run exceeded its step limit ({config.Limits.MaxSteps}).", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            execution.Status = ExecutionStatus.Queued; // released, not consumed: the worker will retry it
            execution.LeaseUntil = null;
            await db.SaveChangesAsync(ct);
            throw;
        }
        catch (NoEligibleModelException ex) { return await FinishAsync(execution, session, ExecutionStatus.Failed, null, ex.Message, ct); }
        catch (CapabilityException ex) { return await FinishAsync(execution, session, ExecutionStatus.Failed, null, ex.Message, ct); }
        catch (ProviderException ex) { return await FinishAsync(execution, session, ExecutionStatus.Failed, null, $"Model provider error: {ex.Message}", ct); }
    }

    private async Task<RunOutcome?> ExecuteToolCallsAsync(Execution execution, Session session, Agent agent, AgentConfiguration config,
        IReadOnlyList<ToolCall> calls, LoopDetector loopDetector, CancellationToken ct)
    {
        var rules = await db.ToolPermissions.Where(p => p.AgentId == null || p.AgentId == agent.Id).Select(p =>
            new ToolRule(p.Pattern, p.Effect, p.AgentId != null, p.UserId != null)).ToListAsync(ct);
        var decisions = ToolPolicy.Evaluate(toolRegistry.All.Select(t => t.Descriptor), config.ToolPolicy, rules).ToDictionary(d => d.Name);

        foreach (var call in calls)
        {
            var tool = toolRegistry.Find(call.Name);
            JsonElement arguments;
            try { arguments = JsonDefaults.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments); }
            catch (JsonException) { arguments = JsonDefaults.Parse("{}"); }

            var toolExec = new ToolExecution
            {
                WorkspaceId = execution.WorkspaceId, ExecutionId = execution.Id, SessionId = session.Id, AgentId = agent.Id,
                ToolCallId = call.Id, ToolName = call.Name, Arguments = call.Arguments ?? "{}",
            };

            if (tool is null)
            {
                toolExec.Status = ToolExecutionStatus.Failed; toolExec.Error = $"Unknown tool '{call.Name}'.";
                db.ToolExecutions.Add(toolExec);
                await AppendMessageAsync(session, execution, "tool", toolExec.Error, null, null, call.Id, call.Name, false, ct);
                continue;
            }

            var loopReason = loopDetector.Record(call.Name, call.Arguments ?? "{}");
            if (loopReason is not null)
                return new RunOutcome(ExecutionStatus.Failed, null, $"Stopped to avoid a loop: {loopReason}");

            if (!decisions.TryGetValue(call.Name, out var decision) || !decision.Allowed)
            {
                toolExec.Status = ToolExecutionStatus.Denied; toolExec.Error = decisions.TryGetValue(call.Name, out var d) ? d.Reason : "not permitted";
                db.ToolExecutions.Add(toolExec);
                await AppendMessageAsync(session, execution, "tool", $"Tool '{call.Name}' was denied: {toolExec.Error}", null, null, call.Id, call.Name, false, ct);
                continue;
            }

            var risk = tool.AssessRisk(arguments);
            toolExec.Risk = risk.ToString().ToLowerInvariant();
            var needsApproval = ToolPolicy.NeedsApproval(call.Name, risk, config.Approval.RiskThreshold, config.Approval.AlwaysRequire,
                config.Approval.AutoApprove, decision.RequiresApproval, tool.Descriptor);

            if (needsApproval)
            {
                toolExec.Status = ToolExecutionStatus.WaitingApproval;
                db.ToolExecutions.Add(toolExec);
                var approval = new Approval
                {
                    WorkspaceId = execution.WorkspaceId, ExecutionId = execution.Id, ToolExecutionId = toolExec.Id, SessionId = session.Id,
                    Kind = "tool", Title = $"Run tool '{call.Name}'", Risk = toolExec.Risk, RequestedByAgentId = agent.Id,
                    Detail = JsonSerializer.Serialize(new { tool = call.Name, arguments = JsonDocument.Parse(toolExec.Arguments).RootElement }),
                    ExpiresAt = clock.UtcNow.AddMinutes(config.Approval.ExpiryMinutes),
                };
                db.Approvals.Add(approval);
                await db.SaveChangesAsync(ct);
                toolExec.ApprovalId = approval.Id;
                await db.SaveChangesAsync(ct);
                await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "approval.requested", new { approvalId = approval.Id, tool = call.Name }, ct);
                execution.Status = ExecutionStatus.WaitingApproval;
                await db.SaveChangesAsync(ct);
                return new RunOutcome(ExecutionStatus.WaitingApproval, null, null);
            }

            await RunToolAsync(execution, session, agent, config, tool, toolExec, arguments, ct);
        }
        return null;
    }

    private async Task<bool> ResumePendingApprovalsAsync(Execution execution, Session session, Agent agent, AgentConfiguration config,
        IReadOnlyList<ToolExecution> pending, CancellationToken ct)
    {
        var resumedAny = false;
        foreach (var toolExec in pending)
        {
            var approval = await db.Approvals.FindAsync([toolExec.ApprovalId], ct);
            if (approval is null || approval.Status == ApprovalStatus.Pending)
            {
                if (approval is not null && approval.ExpiresAt < clock.UtcNow && approval.Status == ApprovalStatus.Pending)
                {
                    approval.Status = ApprovalStatus.Expired;
                    toolExec.Status = ToolExecutionStatus.Denied; toolExec.Error = "Approval expired.";
                    await AppendMessageAsync(session, execution, "tool", "Approval expired; the tool call was not run.", null, null, toolExec.ToolCallId, toolExec.ToolName, false, ct);
                    resumedAny = true;
                }
                continue;
            }
            if (approval.Status == ApprovalStatus.Rejected)
            {
                toolExec.Status = ToolExecutionStatus.Denied; toolExec.Error = approval.DecisionNote ?? "Rejected by approver.";
                await AppendMessageAsync(session, execution, "tool", $"Tool call denied: {toolExec.Error}", null, null, toolExec.ToolCallId, toolExec.ToolName, false, ct);
                resumedAny = true;
                continue;
            }
            var tool = toolRegistry.Find(toolExec.ToolName);
            if (tool is null) { toolExec.Status = ToolExecutionStatus.Failed; toolExec.Error = "Tool no longer exists."; resumedAny = true; continue; }
            var arguments = JsonDefaults.Parse(toolExec.Arguments);
            await RunToolAsync(execution, session, agent, config, tool, toolExec, arguments, ct);
            resumedAny = true;
        }
        if (resumedAny) execution.Status = ExecutionStatus.Running;
        await db.SaveChangesAsync(ct);
        return resumedAny;
    }

    private async Task RunToolAsync(Execution execution, Session session, Agent agent, AgentConfiguration config, ITool tool,
        ToolExecution toolExec, JsonElement arguments, CancellationToken ct)
    {
        var schemaErrors = SchemaValidator.Validate(tool.Descriptor.InputSchema, arguments);
        if (schemaErrors.Count > 0)
        {
            toolExec.Status = ToolExecutionStatus.Failed; toolExec.Error = "Invalid arguments: " + string.Join("; ", schemaErrors);
            if (db.Entry(toolExec).State == EntityState.Detached) db.ToolExecutions.Add(toolExec);
            await AppendMessageAsync(session, execution, "tool", toolExec.Error, null, null, toolExec.ToolCallId, toolExec.ToolName, false, ct);
            return;
        }

        toolExec.Status = ToolExecutionStatus.Running;
        toolExec.StartedAt = clock.UtcNow;
        toolExec.Attempts++;
        if (db.Entry(toolExec).State == EntityState.Detached) db.ToolExecutions.Add(toolExec);
        await db.SaveChangesAsync(ct);

        var context = new ToolContext
        {
            WorkspaceId = execution.WorkspaceId, AgentId = agent.Id, UserId = execution.UserId, SessionId = session.Id,
            ExecutionId = execution.Id, RootExecutionId = execution.RootExecutionId, ToolCallId = toolExec.ToolCallId,
            Depth = execution.Depth, WorkspaceDirectory = AgentWorkspacePath(execution.WorkspaceId, agent.Id),
            WorkspaceAccess = config.WorkspaceAccess, SandboxMode = config.SandboxMode, Services = services,
            Progress = async text => await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "tool.progress", new { tool = toolExec.ToolName, text }, ct),
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, tool.Descriptor.TimeoutSeconds)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        ToolResult result;
        try { result = await tool.ExecuteAsync(context, arguments, linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = ToolResult.Fail($"The tool timed out after {tool.Descriptor.TimeoutSeconds} seconds.");
        }
        catch (SecurityViolationException ex) { result = ToolResult.Fail($"Refused: {ex.Message}"); }
        catch (ToolArgumentException ex) { result = ToolResult.Fail(ex.Message); }
        catch (Exception ex) { logger.LogError(ex, "Tool {Tool} threw an unhandled exception", tool.Descriptor.Name); result = ToolResult.Fail("The tool failed unexpectedly."); }

        toolExec.FinishedAt = clock.UtcNow;
        toolExec.DurationMs = (long)(toolExec.FinishedAt!.Value - toolExec.StartedAt!.Value).TotalMilliseconds;
        toolExec.Status = result.Ok ? ToolExecutionStatus.Succeeded : ToolExecutionStatus.Failed;

        var redactor = services.GetRequiredService<Redactor>();
        var content = redactor.Redact(result.Content);
        const int maxLen = 16_000;
        if (content.Length > maxLen) content = content[..maxLen] + "\n[output truncated]";
        toolExec.Result = content;
        if (!result.Ok) toolExec.Error = content;

        var wrapped = tool.Descriptor.ReturnsUntrustedContent && result.Ok ? content : content;
        await AppendMessageAsync(session, execution, "tool", wrapped, null, null, toolExec.ToolCallId, toolExec.ToolName, tool.Descriptor.ReturnsUntrustedContent, ct);
        await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "tool.finished", new { tool = toolExec.ToolName, ok = result.Ok }, ct);
    }

    private async Task<(List<ChatMessage> Messages, int Tokens)> BuildPromptAsync(Session session, Agent agent, AgentConfiguration config, Execution execution, CancellationToken ct)
    {
        var systemParts = new List<string> { Untrusted.SystemRule };
        foreach (var instr in agent.Instructions.OrderBy(i => i.Order)) systemParts.Add(instr.Content);
        if (!string.IsNullOrEmpty(agent.Personality)) systemParts.Add("Personality: " + agent.Personality);

        if (config.Memory.Enabled)
        {
            var lastUserText = await db.Messages.Where(m => m.SessionId == session.Id && m.Role == "user").OrderByDescending(m => m.Seq)
                .Select(m => m.Content).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(lastUserText))
            {
                var hits = await memory.SearchAsync(session.WorkspaceId, agent.Id, execution.UserId, config.Memory.ReadScopes,
                    lastUserText, config.Memory.RecallTopK, config.Memory.RecallMinScore, ct);
                if (hits.Count > 0)
                    systemParts.Add(Untrusted.Wrap("memory", string.Join("\n", hits.Select(h => "- " + h.Memory.Content))));
            }
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, string.Join("\n\n", systemParts)) };
        if (!string.IsNullOrEmpty(session.Summary)) messages.Add(new(ChatRole.System, "Summary of earlier conversation:\n" + session.Summary));

        var history = await db.Messages.Where(m => m.SessionId == session.Id && m.Epoch == session.Epoch && m.Seq > session.SummaryUpToSeq)
            .OrderBy(m => m.Seq).ToListAsync(ct);
        foreach (var m in history)
        {
            var role = m.Role switch { "user" => ChatRole.User, "assistant" => ChatRole.Assistant, "tool" => ChatRole.Tool, _ => ChatRole.System };
            var content = m.Untrusted ? m.Content : m.Content; // already wrapped at write time when untrusted
            IReadOnlyList<ToolCall>? calls = m.ToolCalls is { Length: > 0 } tc ? JsonSerializer.Deserialize<List<ToolCall>>(tc, JsonDefaults.Options) : null;
            messages.Add(new ChatMessage(role, content, calls, m.ToolCallId, m.ToolName));
        }
        return (messages, TokenEstimator.Estimate(messages));
    }

    private async Task<int> ContextWindowAsync(Execution execution, AgentConfiguration config, CancellationToken ct)
    {
        var modelRef = execution.ModelOverride ?? config.DefaultModel;
        if (modelRef is null) return 32_000;
        var (providerName, modelId) = ModelCallRunner.Split(modelRef);
        var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Provider!.Name == providerName && m.ModelId == modelId, ct);
        return model?.ContextWindow ?? 32_000;
    }

    private async Task CompactAsync(Session session, AgentConfiguration config, Execution execution, CancellationToken ct)
    {
        var toSummarize = await db.Messages.Where(m => m.SessionId == session.Id && m.Epoch == session.Epoch && m.Seq > session.SummaryUpToSeq)
            .OrderBy(m => m.Seq).ToListAsync(ct);
        if (toSummarize.Count <= config.Session.KeepRecentMessages) return;
        var toDrop = toSummarize.Take(toSummarize.Count - config.Session.KeepRecentMessages).ToList();
        if (toDrop.Count == 0) return;

        var transcript = string.Join("\n", toDrop.Select(m => $"[{m.Role}] {Truncate(m.Content, 500)}"));
        var modelRef = execution.ModelOverride ?? config.DefaultModel!;
        string summaryText;
        try
        {
            var request = new ChatRequest(modelRef, [
                new ChatMessage(ChatRole.System, "Summarize this conversation excerpt in a few sentences, preserving facts, decisions and open questions. Output only the summary."),
                new ChatMessage(ChatRole.User, transcript)]);
            ChatResponse? resp = null;
            await foreach (var (evt, _) in models.StreamWithFallbackAsync(session.WorkspaceId, [modelRef], request, ct))
                if (evt is ChatCompleted c) resp = c.Response;
            summaryText = resp?.Text ?? transcript[..Math.Min(2000, transcript.Length)];
        }
        catch (Exception ex) when (ex is ProviderException or NoEligibleModelException)
        {
            logger.LogWarning(ex, "Compaction model call failed; keeping a truncated transcript instead of a summary.");
            summaryText = Truncate(transcript, 2000);
        }

        session.Summary = string.IsNullOrEmpty(session.Summary) ? summaryText : session.Summary + "\n" + summaryText;
        session.SummaryUpToSeq = toDrop[^1].Seq;
        foreach (var m in toDrop) m.Compacted = true;
        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private async Task<List<ToolSpec>> BuildToolSpecsAsync(Agent agent, AgentConfiguration config, CancellationToken ct)
    {
        var rules = await db.ToolPermissions.Where(p => p.AgentId == null || p.AgentId == agent.Id).Select(p =>
            new ToolRule(p.Pattern, p.Effect, p.AgentId != null, p.UserId != null)).ToListAsync(ct);
        var tools = toolRegistry.All.Where(t => t.Unavailable(services) is null).ToList();
        var decisions = ToolPolicy.Evaluate(tools.Select(t => t.Descriptor), config.ToolPolicy, rules).ToDictionary(d => d.Name);
        return tools.Where(t => decisions.TryGetValue(t.Descriptor.Name, out var d) && d.Allowed)
            .Select(t => new ToolSpec(t.Descriptor.Name, t.Descriptor.Description, t.Descriptor.InputSchema)).ToList();
    }

    private async Task<Message> AppendMessageAsync(Session session, Execution execution, string role, string content, string? model,
        string? toolCallsJson, string? toolCallId, string? toolName, bool untrusted, CancellationToken ct)
    {
        var message = new Message
        {
            WorkspaceId = session.WorkspaceId, SessionId = session.Id, Seq = session.NextSeq++, Role = role, Content = content,
            Model = model, ToolCalls = toolCallsJson, ToolCallId = toolCallId, ToolName = toolName, ExecutionId = execution.Id,
            Epoch = session.Epoch, Untrusted = untrusted, AuthorUserId = role == "user" ? execution.UserId : null,
        };
        db.Messages.Add(message);
        session.LastMessageAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return message;
    }

    private async Task RecordUsageAsync(Execution execution, Session session, Agent agent, string modelRef, Usage usage, CancellationToken ct)
    {
        execution.InputTokens += usage.InputTokens;
        execution.OutputTokens += usage.OutputTokens;
        var (providerName, modelId) = ModelCallRunner.Split(modelRef);
        var model = await db.Models.Include(m => m.Provider).FirstOrDefaultAsync(m => m.Provider!.Name == providerName && m.ModelId == modelId, ct);
        var cost = model is not null ? ModelCallRunner.Cost(model, usage.InputTokens, usage.OutputTokens) : 0;
        execution.Cost += cost;
        var record = new UsageRecord
        {
            WorkspaceId = execution.WorkspaceId, UserId = execution.UserId, AgentId = agent.Id, SessionId = session.Id,
            ExecutionId = execution.Id, RootExecutionId = execution.RootExecutionId, Provider = providerName, Model = modelId,
            Purpose = "chat", InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, Estimated = usage.Estimated,
        };
        db.UsageRecords.Add(record);
        await db.SaveChangesAsync(ct);
        if (cost > 0) db.CostRecords.Add(new CostRecord
        {
            WorkspaceId = execution.WorkspaceId, UsageRecordId = record.Id, Amount = cost,
            InputPricePerMTok = model!.InputPricePerMTok, OutputPricePerMTok = model.OutputPricePerMTok,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<RunOutcome> FinishAsync(Execution execution, Session session, ExecutionStatus status, string? finalText, string? error, CancellationToken ct)
    {
        execution.Status = status;
        execution.FinalText = finalText;
        execution.Error = error;
        if (status is ExecutionStatus.Succeeded or ExecutionStatus.Failed or ExecutionStatus.Cancelled)
        {
            execution.FinishedAt = clock.UtcNow;
            execution.LeaseUntil = null;
        }
        if (status == ExecutionStatus.Succeeded && !string.IsNullOrWhiteSpace(finalText) && session.ChannelAccountId is { } channelAccountId)
            db.ChannelMessages.Add(new ChannelMessage
            {
                WorkspaceId = execution.WorkspaceId, ChannelAccountId = channelAccountId, Direction = "out",
                ExternalChatId = session.PeerId ?? "", Text = finalText, Status = "pending", SessionId = session.Id, ExecutionId = execution.Id,
            });
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(execution.WorkspaceId, session.Id, execution.Id, "run." + status.ToString().ToLowerInvariant(),
            new { text = finalText, error }, ct);
        return new RunOutcome(status, finalText, error);
    }

    public static string AgentWorkspacePath(Guid workspaceId, Guid agentId) =>
        Path.Combine(AppContext.BaseDirectory, "data", "workspaces", workspaceId.ToString("N"), agentId.ToString("N"));
}

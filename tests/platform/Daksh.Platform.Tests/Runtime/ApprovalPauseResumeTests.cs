using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Runtime;
using Daksh.Platform.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Platform.Tests.Runtime;

/// <summary>
/// Exercises the real <see cref="AgentRuntimeEngine"/> (not just <see cref="ToolPolicy"/> in isolation, which
/// <c>Tools/ToolPolicyTests.cs</c> already covers) against a real PostgreSQL database: a tool call whose risk
/// trips the agent's approval policy must actually pause the whole execution
/// (<see cref="ExecutionStatus.WaitingApproval"/>, no further model/tool steps run), and once the resulting
/// <see cref="Approval"/> row is decided, the next <c>RunAsync</c> call must resume correctly - either running
/// the tool and continuing to a normal finish, or treating it as denied and continuing without ever running it.
/// </summary>
[Collection(nameof(ApprovalPauseResumeCollection))]
public sealed class ApprovalPauseResumeTests(ApprovalPauseResumeFixture fixture)
{
    private sealed record Seeded(Guid WorkspaceId, Guid SessionId, Guid ExecutionId);

    private async Task<Seeded> SeedAsync(PlatformDbContext db)
    {
        var workspaceId = Guid.CreateVersion7();
        db.Tenant.SetWorkspaceAsSystem(workspaceId);

        var owner = new User { Email = $"owner-{workspaceId:N}@example.com", NormalizedEmail = $"OWNER-{workspaceId:N}@EXAMPLE.COM", DisplayName = "Approval Test Owner" };
        db.Users.Add(owner);
        var workspace = new Workspace { Id = workspaceId, Name = "Approval test workspace", Slug = "approval-" + workspaceId.ToString("N"), OwnerUserId = owner.Id };
        db.Workspaces.Add(workspace);

        var provider = new ModelProvider { WorkspaceId = workspaceId, Name = "scripted", Kind = "scripted", Enabled = true };
        db.ModelProviders.Add(provider);
        var model = new Model
        {
            WorkspaceId = workspaceId, ProviderId = provider.Id, ModelId = ApprovalPauseResumeBehavior.ModelId,
            DisplayName = "Approval pause/resume test model", Enabled = true,
            Capabilities = new ModelCapabilities { Chat = true, Tools = true },
        };
        db.Models.Add(model);

        var agent = new Agent { WorkspaceId = workspaceId, Name = "Approval Test Agent", Slug = "approval-test-agent" };
        db.Agents.Add(agent);
        var configuration = new AgentConfiguration
        {
            WorkspaceId = workspaceId, AgentId = agent.Id, DefaultModel = $"scripted/{ApprovalPauseResumeBehavior.ModelId}",
            ToolPolicy = new ToolPolicySpec { Allow = [ApprovalTestTool.Name] },
            // Default risk threshold ("high") plus the test tool's High risk is enough to require approval -
            // kept explicit here so the test documents exactly which rule it relies on.
            Approval = new AgentApprovalPolicy { RiskThreshold = "high", ExpiryMinutes = 60 },
            Memory = new AgentMemorySettings { Enabled = false }, // keep the test focused on approval, not recall
        };
        agent.Configuration = configuration;
        db.AgentConfigurations.Add(configuration);

        var session = new Session { WorkspaceId = workspaceId, AgentId = agent.Id, NextSeq = 2 };
        db.Sessions.Add(session);
        db.Messages.Add(new Message
        {
            WorkspaceId = workspaceId, SessionId = session.Id, Seq = 1, Role = "user", Content = "Please run the approval test tool.",
        });

        var executionId = Guid.CreateVersion7();
        db.Executions.Add(new Execution
        {
            Id = executionId, WorkspaceId = workspaceId, SessionId = session.Id, AgentId = agent.Id,
            RootExecutionId = executionId, Status = ExecutionStatus.Queued, Trigger = "chat", InputSeq = 1,
        });

        await db.SaveChangesAsync();
        return new Seeded(workspaceId, session.Id, executionId);
    }

    private IServiceScope NewScope() => fixture.Services.CreateScope();

    [Fact]
    public async Task ToolCallRequiringApproval_PausesTheWholeExecution_NoFurtherStepsRun()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var seeded = await SeedAsync(db);
        var engine = scope.ServiceProvider.GetRequiredService<AgentRuntimeEngine>();

        var countBefore = fixture.TestTool.ExecutionCount;
        var outcome = await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStatus.WaitingApproval, outcome.Status);
        Assert.Null(outcome.FinalText);

        var execution = await db.Executions.IgnoreQueryFilters().SingleAsync(e => e.Id == seeded.ExecutionId);
        Assert.Equal(ExecutionStatus.WaitingApproval, execution.Status);
        // Not a terminal status: the lease/finished bookkeeping for terminal runs must not have fired.
        Assert.Null(execution.FinishedAt);

        var toolExec = await db.ToolExecutions.IgnoreQueryFilters().SingleAsync(t => t.ExecutionId == seeded.ExecutionId);
        Assert.Equal(ToolExecutionStatus.WaitingApproval, toolExec.Status);
        Assert.Equal(ApprovalTestTool.Name, toolExec.ToolName);

        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.ToolExecutionId == toolExec.Id);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Equal("high", approval.Risk);

        // The tool must genuinely not have run yet. (ApprovalTestTool is a singleton shared by the whole test
        // collection, so compare against the count taken right before this run, not against zero.)
        Assert.Equal(countBefore, fixture.TestTool.ExecutionCount);
    }

    [Fact]
    public async Task RunAsync_WhileApprovalStillPending_StaysPausedAndRunsNothingElse()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var seeded = await SeedAsync(db);
        var engine = scope.ServiceProvider.GetRequiredService<AgentRuntimeEngine>();

        await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);
        var messageCountAfterFirstRun = await db.Messages.IgnoreQueryFilters().CountAsync(m => m.SessionId == seeded.SessionId);
        var countAfterFirstRun = fixture.TestTool.ExecutionCount;

        // Call RunAsync again before anyone decides the approval - this is exactly what RuntimeWorker's poll
        // loop would do if it picked the lease-expired row back up while a human still hasn't acted.
        var secondOutcome = await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStatus.WaitingApproval, secondOutcome.Status);
        var messageCountAfterSecondRun = await db.Messages.IgnoreQueryFilters().CountAsync(m => m.SessionId == seeded.SessionId);
        Assert.Equal(messageCountAfterFirstRun, messageCountAfterSecondRun); // no new model/tool step happened
        Assert.Equal(countAfterFirstRun, fixture.TestTool.ExecutionCount);

        var toolExec = await db.ToolExecutions.IgnoreQueryFilters().SingleAsync(t => t.ExecutionId == seeded.ExecutionId);
        Assert.Equal(ToolExecutionStatus.WaitingApproval, toolExec.Status);
    }

    [Fact]
    public async Task ApprovedDecision_ResumesAndRunsTheToolThenFinishesSucceeded()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var seeded = await SeedAsync(db);
        var engine = scope.ServiceProvider.GetRequiredService<AgentRuntimeEngine>();

        var countBefore = fixture.TestTool.ExecutionCount;
        await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.ExecutionId == seeded.ExecutionId);
        approval.Status = ApprovalStatus.Approved;
        approval.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var resumed = await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, resumed.Status);
        Assert.Equal("Finished after approval decision.", resumed.FinalText);
        Assert.Equal(countBefore + 1, fixture.TestTool.ExecutionCount); // the tool actually ran, exactly once

        var toolExec = await db.ToolExecutions.IgnoreQueryFilters().SingleAsync(t => t.ExecutionId == seeded.ExecutionId);
        Assert.Equal(ToolExecutionStatus.Succeeded, toolExec.Status);
        Assert.Equal("approval_test_tool ran", toolExec.Result);

        var execution = await db.Executions.IgnoreQueryFilters().SingleAsync(e => e.Id == seeded.ExecutionId);
        Assert.Equal(ExecutionStatus.Succeeded, execution.Status);
        Assert.NotNull(execution.FinishedAt);
    }

    [Fact]
    public async Task RejectedDecision_ResumesWithoutRunningTheToolAndStillFinishes()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var seeded = await SeedAsync(db);
        var engine = scope.ServiceProvider.GetRequiredService<AgentRuntimeEngine>();

        var countBefore = fixture.TestTool.ExecutionCount;
        await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.ExecutionId == seeded.ExecutionId);
        approval.Status = ApprovalStatus.Rejected;
        approval.DecisionNote = "Not approved for this test.";
        approval.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var resumed = await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, resumed.Status);
        Assert.Equal("Finished after approval decision.", resumed.FinalText);
        // The denied tool call must never actually run.
        Assert.Equal(countBefore, fixture.TestTool.ExecutionCount);

        var toolExec = await db.ToolExecutions.IgnoreQueryFilters().SingleAsync(t => t.ExecutionId == seeded.ExecutionId);
        Assert.Equal(ToolExecutionStatus.Denied, toolExec.Status);
        Assert.Equal("Not approved for this test.", toolExec.Error);
    }

    [Fact]
    public async Task ExpiredPendingApproval_IsTreatedAsDenied()
    {
        using var scope = NewScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var seeded = await SeedAsync(db);
        var engine = scope.ServiceProvider.GetRequiredService<AgentRuntimeEngine>();

        var countBefore = fixture.TestTool.ExecutionCount;
        await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);
        var approval = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.ExecutionId == seeded.ExecutionId);
        approval.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); // force it into the past, still Pending
        await db.SaveChangesAsync();

        var resumed = await engine.RunAsync(seeded.ExecutionId, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, resumed.Status);
        Assert.Equal(countBefore, fixture.TestTool.ExecutionCount);

        var approvalAfter = await db.Approvals.IgnoreQueryFilters().SingleAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Expired, approvalAfter.Status);
        var toolExec = await db.ToolExecutions.IgnoreQueryFilters().SingleAsync(t => t.ExecutionId == seeded.ExecutionId);
        Assert.Equal(ToolExecutionStatus.Denied, toolExec.Status);
        Assert.Equal("Approval expired.", toolExec.Error);
    }
}

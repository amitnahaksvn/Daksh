using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Daksh.Gateway.Tests;

/// <summary>
/// Regression test for the claim-race bug fixed this session: <c>Execution.Version</c> was mapped with
/// <c>Ignore()</c>, so it was never a real EF concurrency token - Postgres never checked it, and
/// <c>RuntimeWorker</c>/<c>JobRunnerWorker</c>'s optimistic "only proceed if we win the race to set
/// Status=Running" comment was dead code: two workers could both load the same <c>Queued</c> execution, both
/// successfully flip it to <c>Running</c>, and both run the agent engine concurrently
/// (<c>IX_Messages_SessionId_Seq</c> duplicate-key races were the visible symptom).
///
/// The fix changed the mapping to <c>IsRowVersion()</c> (Postgres <c>xmin</c>, the same pattern already used
/// for <c>Agent</c>/<c>Approval</c>/<c>Automation</c>). This test loads the same <see cref="Execution"/> row
/// into two separate <see cref="PlatformDbContext"/> instances - the same "two concurrent workers, separate DI
/// scopes" shape <c>RuntimeWorker.RunOneAsync</c> actually uses - has both set <c>Status = Running</c> and
/// save, and asserts the first save wins while the second throws <see cref="DbUpdateConcurrencyException"/>.
/// If <c>Execution.Version</c>'s mapping is ever reverted to <c>Ignore()</c>, both saves would succeed and
/// this test would fail loudly.
/// </summary>
[Collection(nameof(GatewayCollection))]
public sealed class ExecutionConcurrencyTests(GatewayFactory factory)
{
    private async Task<Guid> SeedQueuedExecutionAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenant.SetSystem();

        var owner = new User
        {
            Email = $"claim-{Guid.NewGuid():N}@example.com",
            NormalizedEmail = $"claim-{Guid.NewGuid():N}@example.com".ToUpperInvariant(),
            DisplayName = "Claim race test owner",
            PasswordHash = "not-a-real-hash",
        };
        db.Users.Add(owner);
        var workspace = new Workspace { Name = "Claim race workspace", Slug = $"claim-{Guid.NewGuid():N}", OwnerUserId = owner.Id };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        var agent = new Agent { WorkspaceId = workspace.Id, Name = "Claim race agent", Slug = $"claim-agent-{Guid.NewGuid():N}" };
        db.Agents.Add(agent);
        var session = new Session { WorkspaceId = workspace.Id, AgentId = agent.Id, Title = "Claim race session" };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();

        var execution = new Execution
        {
            WorkspaceId = workspace.Id,
            SessionId = session.Id,
            AgentId = agent.Id,
            Status = ExecutionStatus.Queued,
        };
        execution.RootExecutionId = execution.Id;
        db.Executions.Add(execution);
        await db.SaveChangesAsync();

        return execution.Id;
    }

    [Fact]
    public async Task Only_one_of_two_concurrent_claims_on_the_same_execution_succeeds()
    {
        var executionId = await SeedQueuedExecutionAsync();

        // Two separate DI scopes, each with its own PlatformDbContext and its own load of the same row -
        // exactly how RuntimeWorker.RunOneAsync and JobRunnerWorker.RunActionAsync each independently claim
        // an execution from their own scope.
        using var scope1 = factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db1.Tenant.SetSystem();
        var execution1 = await db1.Executions.IgnoreQueryFilters().FirstAsync(e => e.Id == executionId);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db2.Tenant.SetSystem();
        var execution2 = await db2.Executions.IgnoreQueryFilters().FirstAsync(e => e.Id == executionId);

        // Both "workers" decide to claim it.
        execution1.Status = ExecutionStatus.Running;
        execution1.WorkerId = "worker-1";
        execution2.Status = ExecutionStatus.Running;
        execution2.WorkerId = "worker-2";

        // The first save wins the race outright.
        await db1.SaveChangesAsync();

        // The second save is still working off the row version it originally read, which is now stale -
        // Postgres's xmin check on the UPDATE affects zero rows, and EF surfaces that as a concurrency
        // exception rather than silently succeeding.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());

        // Confirm the end state: exactly one worker's claim stuck.
        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        verifyDb.Tenant.SetSystem();
        var final = await verifyDb.Executions.IgnoreQueryFilters().FirstAsync(e => e.Id == executionId);
        Assert.Equal(ExecutionStatus.Running, final.Status);
        Assert.Equal("worker-1", final.WorkerId);
    }
}

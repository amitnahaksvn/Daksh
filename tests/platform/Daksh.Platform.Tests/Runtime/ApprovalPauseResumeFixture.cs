using System.Text.Json;
using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Providers;
using Daksh.Platform.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Daksh.Platform.Tests.Runtime;

/// <summary>
/// A scripted-provider behaviour built only for <see cref="ApprovalPauseResumeTests"/>: it emits exactly one
/// call to <see cref="ApprovalTestTool"/> the first time it's asked (no "tool" message in history yet), and a
/// plain text answer once a tool-role message is present - which is true whether the tool call was approved and
/// actually ran, or was rejected and never ran. This mirrors what a real model would do: ask for the dangerous
/// action, then react to whatever came back from it (a result, or a denial).
/// </summary>
public sealed class ApprovalPauseResumeBehavior : IScriptedBehavior
{
    public const string ModelId = "approval-pause-resume";
    public string Name => ModelId;

    public ChatResponse Respond(ChatRequest request)
    {
        var alreadyResumed = request.Messages.Any(m => m.Role == ChatRole.Tool);
        return alreadyResumed
            ? ScriptedProvider.Text(request, "Finished after approval decision.")
            : ScriptedProvider.Call(request, ApprovalTestTool.Name, new { note = "please approve me" });
    }
}

/// <summary>A deliberately High-risk fake tool, so <see cref="Daksh.Platform.Tools.ToolPolicy.NeedsApproval"/>
/// trips under the agent's default "high" risk threshold without needing any real dangerous tool.</summary>
public sealed class ApprovalTestTool : ITool
{
    public const string Name = "approval_test_tool";
    public int ExecutionCount;

    public ToolDescriptor Descriptor { get; } = new(Name, "A fake tool used only to test the approval pause/resume flow.", "test",
        JsonDefaults.Schema(new { type = "object" }), ToolRisk.High);

    public Task<ToolResult> ExecuteAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        Interlocked.Increment(ref ExecutionCount);
        return Task.FromResult(ToolResult.Success("approval_test_tool ran"));
    }
}

/// <summary>
/// Hosts the real platform DI graph (<see cref="ServiceCollectionExtensions.AddDakshPlatform"/>) against a real
/// PostgreSQL 17 + pgvector container - the same image <c>Daksh.Gateway.Tests/GatewayFactory.cs</c> uses, for the
/// same reason: the migration runs raw "CREATE EXTENSION vector" SQL that a plain postgres image can't satisfy.
/// Shared across every test in <see cref="ApprovalPauseResumeTests"/> via <see cref="ApprovalPauseResumeCollection"/>
/// so the container and its one migration are paid for once.
/// </summary>
public sealed class ApprovalPauseResumeFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("daksh_platform_approval_test")
        .WithUsername("daksh")
        .WithPassword("change-me")
        .Build();

    public ServiceProvider Services { get; private set; } = null!;
    public ApprovalTestTool TestTool { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Platform"] = _postgres.GetConnectionString(),
            // SecretProtector (used by ModelCallRunner, even though the scripted provider needs no real
            // credential) requires at least one 32-byte base64 key to construct.
            ["Security:Encryption:ActiveKeyId"] = "test",
            ["Security:Encryption:Keys:test"] = "MZMXSW9AL9vXCuZM8sp/4b64pkpfQzpt+KTDVEwO8ZE=",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDakshPlatform(config);
        // Extra registrations for this test only: an additional scripted behaviour and an additional tool, on
        // top of everything AddDakshPlatform already wires up - production registration is untouched.
        services.AddSingleton<IScriptedBehavior, ApprovalPauseResumeBehavior>();
        TestTool = new ApprovalTestTool();
        services.AddSingleton<ITool>(TestTool);

        Services = services.BuildServiceProvider();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(nameof(ApprovalPauseResumeCollection))]
public sealed class ApprovalPauseResumeCollection : ICollectionFixture<ApprovalPauseResumeFixture>;

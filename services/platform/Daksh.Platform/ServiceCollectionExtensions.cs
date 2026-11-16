using Daksh.Domain;
using Daksh.Platform.Automation;
using Daksh.Platform.Channels;
using Daksh.Platform.Data;
using Daksh.Platform.Files;
using Daksh.Platform.Identity;
using Daksh.Platform.Infrastructure;
using Daksh.Platform.Memory;
using Daksh.Platform.Providers;
using Daksh.Platform.Runtime;
using Daksh.Platform.Sandbox;
using Daksh.Platform.Security;
using Daksh.Platform.Tools;
using Daksh.Platform.Tools.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Daksh.Platform;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers every platform service: data access, security, providers, tools, runtime, memory, automation.
    /// Channel adapters and the HTTP/SignalR surface are added separately by the Gateway.</summary>
    public static IServiceCollection AddDakshPlatform(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<TenantContext>();
        services.AddSingleton(_ => MongoConnection.CreateDatabase(
            config["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Mongo:ConnectionString (Mongo__ConnectionString) is required."),
            config["Mongo:Database"] ?? throw new InvalidOperationException("Mongo:Database (Mongo__Database) is required.")));

        services.Configure<SecretProtectorOptions>(config.GetSection("Security:Encryption"));
        services.Configure<StorageOptions>(config.GetSection("Storage"));
        services.Configure<SandboxOptions>(config.GetSection("Sandbox"));
        services.Configure<RuntimeOptions>(config.GetSection("Runtime"));
        services.Configure<SchedulerOptions>(config.GetSection("Scheduler"));
        services.Configure<MemoryServiceOptions>(config.GetSection("Memory"));
        services.Configure<Tools.Core.WebSearchOptions>(config.GetSection("Tools:WebSearch"));
        services.Configure<JwtOptions>(config.GetSection("Auth:Jwt"));
        services.Configure<AuthPolicyOptions>(config.GetSection("Auth:Policy"));
        services.Configure<SsrfOptions>(config.GetSection("Security:Ssrf"));
        services.Configure<CommandPolicyOptions>(config.GetSection("Security:Command"));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<Redactor>();
        services.AddSingleton<ISecretProtector>(sp => new SecretProtector(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecretProtectorOptions>>().Value));
        services.AddSingleton(sp => new SsrfGuard(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SsrfOptions>>().Value));
        services.AddSingleton(sp => new CommandPolicy(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CommandPolicyOptions>>().Value));

        services.AddHttpClient(OpenAiProvider.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(5))
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SsrfGuard>().CreateHandler());
        services.AddHttpClient("web-tools", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(sp => sp.GetRequiredService<SsrfGuard>().CreateHandler());

        services.AddSingleton<IModelProvider, OpenAiProvider>();
        services.AddSingleton<IModelProvider, AnthropicProvider>();
        services.AddSingleton<IModelProvider, OllamaProvider>();
        services.AddSingleton<IModelProvider, ScriptedProvider>();
        services.AddSingleton<IScriptedBehavior, EchoBehavior>();
        services.AddSingleton<IScriptedBehavior, ResearchBehavior>();
        services.AddSingleton<ProviderRegistry>();
        services.AddScoped<ModelCallRunner>();

        services.AddScoped<IdentityService>();
        services.AddSingleton<PasswordService>();
        services.AddScoped<TokenService>();
        services.AddScoped<WorkspaceService>();

        services.AddScoped<MemoryService>();
        services.AddScoped<FileStorageService>();

        services.AddSingleton<ISandboxRunner, ProcessSandboxRunner>();
        services.AddSingleton<ISandboxRunner, ContainerSandboxRunner>();
        services.AddSingleton<SandboxRunnerSelector>();

        services.AddScoped<ExecutionQueueService>();
        services.AddScoped<AgentRuntimeEngine>();
        services.AddSingleton<IExecutionEventSink, NullExecutionEventSink>();
        services.AddHostedService<RuntimeWorker>();

        services.AddScoped<SchedulerService>();
        services.AddHostedService<JobRunnerWorker>();

        services.AddHttpClient<Daksh.Platform.Integrations.DeveloperPlatforms.GitHubIntegration>(c =>
        {
            c.BaseAddress = new Uri("https://api.github.com/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddHttpClient<Daksh.Platform.Integrations.DeveloperPlatforms.GitLabIntegration>(c =>
        {
            c.BaseAddress = new Uri("https://gitlab.com/api/v4/");
            c.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<Channels.Core.MockChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<Channels.Core.MockChannelAdapter>());
        services.AddSingleton<Channels.Core.TelegramChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<Channels.Core.TelegramChannelAdapter>());
        services.AddSingleton<Channels.Core.SlackChannelAdapter>();
        services.AddSingleton<IChannelAdapter>(sp => sp.GetRequiredService<Channels.Core.SlackChannelAdapter>());
        services.AddScoped<ChannelService>();
        services.AddHostedService<ChannelOutboxWorker>();

        AddCoreTools(services);
        services.AddSingleton<ToolRegistry>();

        return services;
    }

    private static void AddCoreTools(IServiceCollection services)
    {
        services.AddSingleton<ITool, ReadFileTool>();
        services.AddSingleton<ITool, WriteFileTool>();
        services.AddSingleton<ITool, EditFileTool>();
        services.AddSingleton<ITool, ListFilesTool>();
        services.AddSingleton<ITool, SearchFilesTool>();
        services.AddSingleton<ITool, ExecTool>();
        services.AddSingleton<ITool, CodeExecuteTool>();
        services.AddSingleton<IWebSearchBackend, SearXngSearchBackend>();
        services.AddSingleton<IWebSearchBackend, BraveSearchBackend>();
        services.AddSingleton<IWebSearchBackend, TavilySearchBackend>();
        services.AddSingleton<IWebSearchBackend, ScriptedSearchBackend>();
        services.AddSingleton<ITool, WebSearchTool>();
        services.AddSingleton<ITool, WebFetchTool>();
        services.AddSingleton<ITool, HttpRequestTool>();
        services.AddSingleton<ITool, MemorySearchTool>();
        services.AddSingleton<ITool, MemoryWriteTool>();
        services.AddSingleton<ITool, SessionsListTool>();
        services.AddSingleton<ITool, SessionHistoryTool>();
        services.AddSingleton<ITool, ScheduleCreateTool>();
        services.AddSingleton<ITool, ScheduleListTool>();
        services.AddSingleton<ITool, ScheduleCancelTool>();
        services.AddSingleton<ITool, MessageSendTool>();
        services.AddSingleton<ITool, ImageAnalyzeTool>();
    }
}

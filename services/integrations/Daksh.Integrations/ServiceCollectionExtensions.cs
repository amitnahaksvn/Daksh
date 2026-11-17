using Daksh.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Daksh.Integrations;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers mock adapters with TryAdd so a real provider registered earlier (or replaced later)
    /// wins without touching consuming code. Also registers a real Gemini chat model (Cloud locality) when
    /// Ai:Gemini:ApiKey is configured — PolicyModelRouter then prefers it over the mock for any user who has
    /// allowed provider data sharing.
    /// </summary>
    public static IServiceCollection AddDakshIntegrations(this IServiceCollection s, IConfiguration config)
    {
        s.TryAddSingleton<MockChatClient>();
        s.AddSingleton(sp =>
            new ChatModelRegistration("mock", sp.GetRequiredService<MockChatClient>(), ModelLocality.Local, IsSimulated: true));

        var geminiApiKey = config["Ai:Gemini:ApiKey"];
        if (!string.IsNullOrWhiteSpace(geminiApiKey))
        {
            // "-lite" models carry a much higher free-tier daily quota than the flagship model (we hit
            // gemini-3.8-flash's 20-requests/day cap almost immediately); prefer this unless overridden.
            var geminiModel = config["Ai:Gemini:Model"] ?? "gemini-3.5-flash-lite";
            s.AddSingleton(_ => new GeminiChatClient(new HttpClient(), geminiApiKey, geminiModel));
            s.AddSingleton(sp =>
                new ChatModelRegistration("gemini", sp.GetRequiredService<GeminiChatClient>(), ModelLocality.Cloud, IsSimulated: false, CostRank: 1));
        }

        s.TryAddSingleton<MockEmbeddingGenerator>();
        s.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => sp.GetRequiredService<MockEmbeddingGenerator>());
        s.AddDakshAi();
        s.TryAddSingleton<ISpeechRecognizer, MockSpeechRecognizer>();
        s.TryAddSingleton<ISpeechSynthesizer, MockSpeechSynthesizer>();
        s.TryAddSingleton<IEmailProvider, MockEmailProvider>();
        s.TryAddSingleton<ICalendarProvider, MockCalendarProvider>();
        s.TryAddSingleton<IMessagingProvider, MockMessagingProvider>();
        s.TryAddSingleton<MockPushNotificationSender>();
        s.TryAddSingleton<IPushNotificationSender>(sp => sp.GetRequiredService<MockPushNotificationSender>());
        s.TryAddSingleton<IWorkflowRunner, MockWorkflowRunner>();
        return s;
    }

    public static IReadOnlyList<IntegrationInfo> DescribeIntegrations(this IServiceProvider sp) => new IIntegration[]
    {
        sp.GetRequiredService<MockChatClient>(), sp.GetRequiredService<MockEmbeddingGenerator>(), sp.GetRequiredService<ISpeechRecognizer>(),
        sp.GetRequiredService<ISpeechSynthesizer>(), sp.GetRequiredService<IEmailProvider>(),
        sp.GetRequiredService<ICalendarProvider>(), sp.GetRequiredService<IMessagingProvider>(),
        sp.GetRequiredService<IPushNotificationSender>(), sp.GetRequiredService<IWorkflowRunner>()
    }.Select(i => i.Info).ToList();
}

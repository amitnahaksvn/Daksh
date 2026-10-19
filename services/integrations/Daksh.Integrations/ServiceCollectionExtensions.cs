using Daksh.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Daksh.Integrations;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers mock adapters with TryAdd so a real provider registered earlier (or replaced later)
    /// wins without touching consuming code.
    /// </summary>
    public static IServiceCollection AddDakshIntegrations(this IServiceCollection s)
    {
        s.TryAddSingleton<MockChatClient>();
        s.AddSingleton(sp =>
            new ChatModelRegistration("mock", sp.GetRequiredService<MockChatClient>(), ModelLocality.Local, IsSimulated: true));
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

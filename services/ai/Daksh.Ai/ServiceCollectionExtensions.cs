using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Daksh.Ai;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the AI core. Register <see cref="ChatModelRegistration"/> instances (and an embedding generator)
    /// before or after; AddDakshIntegrations supplies simulated defaults.
    /// </summary>
    public static IServiceCollection AddDakshAi(this IServiceCollection s)
    {
        s.TryAddSingleton<IPromptRegistry, PromptRegistry>();
        s.TryAddSingleton<IModelRouter, PolicyModelRouter>();
        s.TryAddSingleton<IMemoryStore, InMemoryVectorMemoryStore>();
        s.TryAddSingleton<AgentToolRegistry>();
        s.TryAddSingleton<IMlModel<EmailFeatures, PriorityPrediction>, HeuristicEmailPriorityModel>();
        s.TryAddEnumerable(ServiceDescriptor.Singleton<ITextGuardrail, PiiRedactionGuardrail>());
        s.TryAddEnumerable(ServiceDescriptor.Singleton<ITextGuardrail, PromptInjectionGuardrail>());
        s.TryAddSingleton<IAssistantOrchestrator, AssistantOrchestrator>();
        return s;
    }
}

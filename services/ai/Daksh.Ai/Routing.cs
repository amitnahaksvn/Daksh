using Microsoft.Extensions.AI;

namespace Daksh.Ai;

/// <summary>A registered chat model. Real providers (Anthropic, OpenAI, Azure OpenAI, Ollama...) plug in as <see cref="IChatClient"/>.</summary>
public sealed record ChatModelRegistration(string Name, IChatClient Client, ModelLocality Locality, bool IsSimulated, int CostRank = 0);

public interface IModelRouter
{
    /// <summary>Picks the cheapest eligible model. Cloud models are ineligible unless the user allowed provider data sharing.</summary>
    ChatModelRegistration Select(AiTask task, UserAiContext user);
}

public sealed class NoEligibleModelException(string message) : Exception(message);

public sealed class PolicyModelRouter(IEnumerable<ChatModelRegistration> models) : IModelRouter
{
    private readonly IReadOnlyList<ChatModelRegistration> _models = models.ToList();

    public ChatModelRegistration Select(AiTask task, UserAiContext user)
    {
        var eligible = _models.Where(m => m.Locality == ModelLocality.Local || user.AllowProviderDataSharing)
                              .OrderBy(m => m.IsSimulated ? 1 : 0) // prefer real models over mocks
                              .ThenBy(m => m.CostRank)
                              .FirstOrDefault();
        return eligible ?? throw new NoEligibleModelException("No AI model is permitted by the user's privacy settings.");
    }

    public IReadOnlyList<ChatModelRegistration> Models => _models;
}

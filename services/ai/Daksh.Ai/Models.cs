namespace Daksh.Ai;

public enum AiTask { Chat, Summarize, Classify, Extract }

/// <summary>Where a model runs. Local models never send user content off the device/host boundary we control.</summary>
public enum ModelLocality { Local, Cloud }

/// <summary>Per-user choices that constrain every AI call. Sourced from the user's privacy settings.</summary>
public sealed record UserAiContext(string UserId, bool MemoryEnabled, bool AllowProviderDataSharing);

/// <param name="Context">Data pulled from the user's connected apps for this question. It is given to the model as data, never as instructions.</param>
/// <param name="SystemPrompt">Replaces Daksha's own assistant persona. Used when the model must speak FOR the owner to someone else (auto-replies), where "You are Daksh, a personal AI operator" is the wrong voice.</param>
public sealed record AssistantRequest(UserAiContext User, string Message, IReadOnlyList<(string Role, string Content)>? History = null, string? Context = null, string? SystemPrompt = null);

public sealed record AssistantResult(
    string Text,
    string Model,
    bool IsSimulated,
    long InputTokens,
    long OutputTokens,
    IReadOnlyList<string> GuardrailFlags,
    IReadOnlyList<string> MemoriesUsed);

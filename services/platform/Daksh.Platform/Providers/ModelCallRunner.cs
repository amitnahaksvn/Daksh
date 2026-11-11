using Daksh.Platform.Data;
using Daksh.Platform.Domain;
using Daksh.Platform.Providers;
using Daksh.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daksh.Platform.Providers;

public sealed record ResolvedModel(ModelProvider Provider, Model Model, ProviderCredential? Credential, IModelProvider Adapter);

public sealed class NoEligibleModelException(string message) : Exception(message);

/// <summary>
/// Resolves "provider/model" references to a configured provider + model + live credential, runs the streaming
/// call with credential rotation and retry, and records usage/cost. This is the one place every caller (agent
/// runtime, memory embeddings, media tools) goes through, so usage tracking and failover are never duplicated.
/// </summary>
public sealed class ModelCallRunner(PlatformDbContext db, ProviderRegistry registry, ISecretProtector secrets, Redactor redactor, ILogger<ModelCallRunner> logger)
{
    public static (string Provider, string Model) Split(string modelRef)
    {
        var i = modelRef.IndexOf('/');
        if (i < 0) throw new ArgumentException($"Model reference '{modelRef}' must be in the form 'provider/model'.");
        return (modelRef[..i], modelRef[(i + 1)..]);
    }

    public async Task<ResolvedModel> ResolveAsync(Guid workspaceId, string modelRef, CancellationToken ct)
    {
        var (providerName, modelId) = Split(modelRef);
        var provider = await db.ModelProviders.Include(p => p.Credentials).Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Name == providerName && p.Enabled, ct)
            ?? throw new NoEligibleModelException($"Provider '{providerName}' is not configured or is disabled.");
        var model = provider.Models.FirstOrDefault(m => m.ModelId == modelId && m.Enabled)
            ?? throw new NoEligibleModelException($"Model '{modelId}' is not configured on provider '{providerName}'.");
        if (!registry.Supports(provider.Kind))
            throw new NoEligibleModelException($"No adapter is registered for provider kind '{provider.Kind}'.");
        var credential = PickCredential(provider.Credentials);
        return new ResolvedModel(provider, model, credential, registry.Get(provider.Kind));
    }

    private static ProviderCredential? PickCredential(IReadOnlyList<ProviderCredential> credentials)
    {
        var now = DateTimeOffset.UtcNow;
        return credentials.Where(c => c.CooldownUntil is null || c.CooldownUntil < now)
            .OrderBy(c => c.Priority).ThenBy(c => c.LastUsedAt ?? DateTimeOffset.MinValue).FirstOrDefault()
            ?? credentials.OrderBy(c => c.CooldownUntil).FirstOrDefault();
    }

    private ProviderEndpoint BuildEndpoint(ModelProvider provider, ProviderCredential? credential)
    {
        var key = credential is not null ? secrets.Unprotect(credential.SecretEnc) : null;
        if (key is not null) redactor.Register(key);
        return new ProviderEndpoint(provider.Kind, provider.BaseUrl, key, provider.Headers, provider.Options);
    }

    /// <summary>Streams a chat call against the primary model, then fallbacks, rotating credentials on auth/rate-limit failures.</summary>
    public async IAsyncEnumerable<(ChatStreamEvent Event, string ModelRef)> StreamWithFallbackAsync(
        Guid workspaceId, IReadOnlyList<string> modelRefs, ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var modelRef in modelRefs)
        {
            ResolvedModel resolved;
            try { resolved = await ResolveAsync(workspaceId, modelRef, ct); }
            catch (NoEligibleModelException ex) { last = ex; logger.LogWarning("Skipping model {Model}: {Reason}", modelRef, ex.Message); continue; }

            try { CapabilityCheck.Ensure(resolved.Model.Capabilities, request, modelRef); }
            catch (CapabilityException ex) { last = ex; continue; }

            var triedCredentials = new HashSet<Guid>();
            var credential = resolved.Credential;
            for (var attempt = 0; attempt < Math.Max(1, resolved.Provider.Credentials.Count); attempt++)
            {
                if (credential is not null) triedCredentials.Add(credential.Id);
                var endpoint = BuildEndpoint(resolved.Provider, credential);
                var retried = false;
                await foreach (var evt in StreamOneAsync(resolved.Provider, credential, resolved.Adapter, endpoint,
                    request with { Model = resolved.Model.ModelId }, ct))
                {
                    if (evt is RetrySignal) { retried = true; break; }
                    yield return (evt, modelRef);
                    if (evt is ChatCompleted) yield break;
                }
                if (!retried) break;
                credential = PickCredential(resolved.Provider.Credentials.Where(c => !triedCredentials.Contains(c.Id)).ToList());
                if (credential is null) break;
            }
        }
        throw last ?? new NoEligibleModelException("No configured model could serve this request.");
    }

    private abstract record StreamOutcome;
    private sealed record RetrySignal : ChatStreamEvent;

    private async IAsyncEnumerable<ChatStreamEvent> StreamOneAsync(ModelProvider provider, ProviderCredential? credential, IModelProvider adapter,
        ProviderEndpoint endpoint, ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        IAsyncEnumerator<ChatStreamEvent>? enumerator = null;
        try
        {
            enumerator = adapter.StreamAsync(endpoint, request, ct).GetAsyncEnumerator(ct);
        }
        catch (ProviderException) { }

        while (true)
        {
            ChatStreamEvent? current = null;
            ProviderException? failure = null;
            try
            {
                if (enumerator is null || !await enumerator.MoveNextAsync()) break;
                current = enumerator.Current;
            }
            catch (ProviderException ex) { failure = ex; }

            if (failure is not null)
            {
                await RecordFailureAsync(provider, credential, failure, ct);
                if (failure.ShouldFailover) { yield return new RetrySignal(); yield break; }
                throw failure;
            }
            if (current is ChatCompleted completed)
            {
                if (credential is not null) { credential.LastUsedAt = DateTimeOffset.UtcNow; credential.ErrorCount = 0; await db.SaveChangesAsync(ct); }
                yield return completed;
                yield break;
            }
            yield return current!;
        }
    }

    private async Task RecordFailureAsync(ModelProvider provider, ProviderCredential? credential, ProviderException ex, CancellationToken ct)
    {
        logger.LogWarning(ex, "Provider {Provider} call failed ({Kind})", provider.Name, ex.Kind);
        provider.HealthStatus = ex.Kind is ProviderErrorKind.Auth or ProviderErrorKind.Billing ? "down" : "degraded";
        provider.HealthCheckedAt = DateTimeOffset.UtcNow;
        provider.HealthError = ex.Message;
        if (credential is not null)
        {
            credential.ErrorCount++;
            var cooldown = ex.Kind switch
            {
                ProviderErrorKind.Billing => TimeSpan.FromMinutes(10),
                ProviderErrorKind.Auth => TimeSpan.FromMinutes(5),
                ProviderErrorKind.RateLimit => ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, Math.Min(credential.ErrorCount, 4)))),
                _ => TimeSpan.FromSeconds(30),
            };
            credential.CooldownUntil = DateTimeOffset.UtcNow.Add(cooldown);
            credential.DisabledReason = ex.Message;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<(IReadOnlyList<float[]> Vectors, Usage Usage)> EmbedAsync(Guid workspaceId, string modelRef, IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var resolved = await ResolveAsync(workspaceId, modelRef, ct);
        if (!resolved.Model.Capabilities.Embeddings) throw new CapabilityException($"Model '{modelRef}' does not support embeddings.");
        var endpoint = BuildEndpoint(resolved.Provider, resolved.Credential);
        return await resolved.Adapter.EmbedAsync(endpoint, resolved.Model.ModelId, inputs, ct);
    }

    public static decimal Cost(Model model, long inputTokens, long outputTokens) =>
        Pricing.Cost(inputTokens, outputTokens, model.InputPricePerMTok, model.OutputPricePerMTok);
}

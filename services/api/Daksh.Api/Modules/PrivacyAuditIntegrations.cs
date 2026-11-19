using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Daksh.Integrations;
using FluentValidation;

namespace Daksh.Api.Modules;

public sealed record PrivacySettingsDto(bool MemoryEnabled, bool AllowProviderDataSharing, int RetentionDays);
public sealed record AuditEventDto(Guid Id, string Action, string? EntityType, string? EntityId, string? Detail, DateTimeOffset OccurredAt);
public sealed record IntegrationDto(string Key, string Category, string Provider, string State, string Note);

[BsonIgnoreExtraElements]
public sealed class PrivacySettingsDoc
{
    [BsonId] public required string UserId { get; set; }
    public bool MemoryEnabled { get; set; }
    public bool AllowProviderDataSharing { get; set; }
    public int RetentionDays { get; set; } = 365;
}

[BsonIgnoreExtraElements]
public sealed class AuditEventDoc
{
    [BsonId] public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string UserId { get; set; }
    public required string Action { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Detail { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public static class PrivacyStore
{
    public static async Task<PrivacySettingsDto> GetAsync(IMongoDatabase db, string userId, CancellationToken ct)
    {
        var doc = await db.GetCollection<PrivacySettingsDoc>("privacySettings").Find(d => d.UserId == userId).FirstOrDefaultAsync(ct);
        return doc is null ? new PrivacySettingsDto(false, false, 365) : new PrivacySettingsDto(doc.MemoryEnabled, doc.AllowProviderDataSharing, doc.RetentionDays);
    }

    public static Task SaveAsync(IMongoDatabase db, string userId, PrivacySettingsDto s, CancellationToken ct) =>
        db.GetCollection<PrivacySettingsDoc>("privacySettings").ReplaceOneAsync(d => d.UserId == userId,
            new PrivacySettingsDoc { UserId = userId, MemoryEnabled = s.MemoryEnabled, AllowProviderDataSharing = s.AllowProviderDataSharing, RetentionDays = s.RetentionDays },
            new ReplaceOptions { IsUpsert = true }, ct);
}

/// <summary>The account's activity log: what Daksha did on the owner's behalf, without the contents of anything private.</summary>
public static class AuditStore
{
    public static Task WriteAsync(IMongoDatabase db, string userId, string action, string? entityType, string? entityId, string? detail, CancellationToken ct) =>
        db.GetCollection<AuditEventDoc>("auditEvents").InsertOneAsync(new AuditEventDoc { UserId = userId, Action = action, EntityType = entityType, EntityId = entityId, Detail = detail }, cancellationToken: ct);
}

public sealed class PrivacySettingsValidator : AbstractValidator<PrivacySettingsDto>
{
    public PrivacySettingsValidator() => RuleFor(x => x.RetentionDays).InclusiveBetween(1, 3650);
}

public static class PlatformModules
{
    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        var p = app.MapGroup("/api/v1/privacy").RequireAuthorization().WithTags("Privacy");

        // Everything is off until the owner turns it on: sharing data with an AI provider is always an explicit choice.
        p.MapGet("/", async (IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) => Results.Ok(await PrivacyStore.GetAsync(db, who.UserId, ct)));

        p.MapPut("/", async (PrivacySettingsDto req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            await PrivacyStore.SaveAsync(db, who.UserId, req, ct);
            await AuditStore.WriteAsync(db, who.UserId, "privacy.update", "privacy", null,
                $"Memory {(req.MemoryEnabled ? "on" : "off")}, AI provider data sharing {(req.AllowProviderDataSharing ? "on" : "off")}, keep {req.RetentionDays} days", ct);
            return Results.Ok(req);
        }).Validate<PrivacySettingsDto>();

        app.MapGet("/api/v1/audit-events", async (IMongoDatabase db, ClaimsPrincipalAccessor who, int? take, CancellationToken ct) =>
            {
                var rows = await db.GetCollection<AuditEventDoc>("auditEvents").Find(e => e.UserId == who.UserId).SortByDescending(e => e.OccurredAt).Limit(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct);
                return Results.Ok(rows.Select(e => new AuditEventDto(Guid.Parse(e.Id), e.Action, e.EntityType, e.EntityId, e.Detail, new DateTimeOffset(DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc)))));
            })
            .RequireAuthorization().WithTags("Audit");

        app.MapGet("/api/v1/integrations", (IServiceProvider sp) =>
                Results.Ok(sp.DescribeIntegrations()
                    .Select(i => new IntegrationDto(i.Key, i.Category, i.Provider, i.State.ToString(), i.Note))))
            .RequireAuthorization().WithTags("Integrations");
    }
}

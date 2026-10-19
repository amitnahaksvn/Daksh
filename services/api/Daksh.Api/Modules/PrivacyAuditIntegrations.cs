using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using Daksh.Domain;
using Daksh.Integrations;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace Daksh.Api.Modules;

public sealed record PrivacySettingsDto(bool MemoryEnabled, bool AllowProviderDataSharing, int RetentionDays);
public sealed record AuditEventDto(Guid Id, string Action, string? EntityType, string? EntityId, string? Detail, DateTimeOffset OccurredAt);
public sealed record IntegrationDto(string Key, string Category, string Provider, string State, string Note);

public sealed class PrivacySettingsValidator : AbstractValidator<PrivacySettingsDto>
{
    public PrivacySettingsValidator() => RuleFor(x => x.RetentionDays).InclusiveBetween(1, 3650);
}

public static class PlatformModules
{
    public static void MapPlatform(this IEndpointRouteBuilder app)
    {
        // Privacy and memory are opt-in: defaults are everything off.
        var p = app.MapGroup("/api/v1/privacy").RequireAuthorization().WithTags("Privacy");

        p.MapGet("/", async (HttpContext http, DakshDbContext db, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            var s = await db.PrivacySettings.FindAsync([u], ct);
            return Results.Ok(s is null ? new PrivacySettingsDto(false, false, 90)
                : new PrivacySettingsDto(s.MemoryEnabled, s.AllowProviderDataSharing, s.RetentionDays));
        });

        p.MapPut("/", async (PrivacySettingsDto req, HttpContext http, DakshDbContext db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            var s = await db.PrivacySettings.FindAsync([u], ct);
            if (s is null) db.PrivacySettings.Add(s = new PrivacySettings { UserId = u });
            s.MemoryEnabled = req.MemoryEnabled;
            s.AllowProviderDataSharing = req.AllowProviderDataSharing;
            s.RetentionDays = req.RetentionDays;
            s.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Record(u, "privacy.update", nameof(PrivacySettings), u,
                $"memory={req.MemoryEnabled};share={req.AllowProviderDataSharing};retention={req.RetentionDays}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(req);
        }).Validate<PrivacySettingsDto>();

        app.MapGet("/api/v1/audit-events", async (HttpContext http, DakshDbContext db, int? take, CancellationToken ct) =>
        {
            var u = http.User.UserId();
            var n = Math.Clamp(take ?? 50, 1, 200);
            return Results.Ok(await db.AuditEvents.Where(a => a.UserId == u)
                .OrderByDescending(a => a.OccurredAt).Take(n)
                .Select(a => new AuditEventDto(a.Id, a.Action, a.EntityType, a.EntityId, a.Detail, a.OccurredAt))
                .ToListAsync(ct));
        }).RequireAuthorization().WithTags("Audit");

        app.MapGet("/api/v1/integrations", (IServiceProvider sp) =>
                Results.Ok(sp.DescribeIntegrations()
                    .Select(i => new IntegrationDto(i.Key, i.Category, i.Provider, i.State.ToString(), i.Note))))
            .RequireAuthorization().WithTags("Integrations");
    }
}

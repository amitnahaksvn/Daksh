using Daksh.Api.Infrastructure;
using Daksh.Integrations;
using FluentValidation;

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
        // Privacy and audit-events: pending MongoDB rebuild (previously backed by DakshDbContext).
        var p = app.MapGroup("/api/v1/privacy").RequireAuthorization().WithTags("Privacy");

        p.MapGet("/", (HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Privacy settings are unavailable: pending MongoDB rebuild."));

        p.MapPut("/", (PrivacySettingsDto req, HttpContext http) =>
            Results.Problem(statusCode: 501, title: "Privacy settings are unavailable: pending MongoDB rebuild.")).Validate<PrivacySettingsDto>();

        app.MapGet("/api/v1/audit-events", (HttpContext http, int? take) =>
            Results.Problem(statusCode: 501, title: "Audit events are unavailable: pending MongoDB rebuild."))
            .RequireAuthorization().WithTags("Audit");

        app.MapGet("/api/v1/integrations", (IServiceProvider sp) =>
                Results.Ok(sp.DescribeIntegrations()
                    .Select(i => new IntegrationDto(i.Key, i.Category, i.Provider, i.State.ToString(), i.Note))))
            .RequireAuthorization().WithTags("Integrations");
    }
}

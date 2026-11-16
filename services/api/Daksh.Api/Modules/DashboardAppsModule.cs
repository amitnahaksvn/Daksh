using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using FluentValidation;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record DashboardAppDto(string Id, string ConnectionId, string IntegrationKey, string Name, string Icon, string Color, string Status, string? AccountIdentifier, DateTime? LastActiveAt, int DisplayOrder);
public sealed record AddDashboardAppRequest(string? ConnectionId);
public sealed record PatchDashboardAppRequest(bool? IsVisible, int? DisplayOrder);

public sealed class AddDashboardAppRequestValidator : AbstractValidator<AddDashboardAppRequest>
{
    public AddDashboardAppRequestValidator() => RuleFor(x => x.ConnectionId).NotEmpty();
}

/// <summary>
/// What the signed-in user chose to pin to their dashboard. Connecting an integration never adds it here —
/// the user must explicitly "Add to Dashboard"; removing a row here never touches the underlying connection.
/// </summary>
public static class DashboardAppsModule
{
    public static void MapDashboardApps(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/dashboard/apps").WithTags("Dashboard").RequireAuthorization(AuthSetup.UserScheme);

        g.MapGet("/", ListAsync);
        g.MapPost("/", AddAsync).Validate<AddDashboardAppRequest>();
        g.MapDelete("/{id}", RemoveAsync);
        g.MapPatch("/{id}", PatchAsync);
    }

    private static async Task<IResult> ListAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var apps = await db.GetCollection<UserDashboardApp>("userDashboardApps")
            .Find(a => a.UserId == who.UserId && a.IsVisible)
            .SortBy(a => a.DisplayOrder).ToListAsync(ct);
        if (apps.Count == 0) return Results.Ok(Array.Empty<DashboardAppDto>());

        var connectionIds = apps.Select(a => a.ConnectionId).ToList();
        var connections = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => connectionIds.Contains(c.Id)).ToListAsync(ct);
        var connectionsById = connections.ToDictionary(c => c.Id);

        var keys = connections.Select(c => c.IntegrationKey).Distinct().ToList();
        var catalog = await db.GetCollection<IntegrationCatalogEntry>("integrations")
            .Find(c => keys.Contains(c.IntegrationKey)).ToListAsync(ct);
        var catalogByKey = catalog.ToDictionary(c => c.IntegrationKey);

        var dtos = apps
            .Where(a => connectionsById.ContainsKey(a.ConnectionId))
            .Select(a =>
            {
                var connection = connectionsById[a.ConnectionId];
                var entry = catalogByKey.GetValueOrDefault(connection.IntegrationKey);
                return new DashboardAppDto(
                    a.Id, connection.Id, connection.IntegrationKey,
                    entry?.Name ?? connection.IntegrationKey, entry?.Icon ?? "", entry?.Color ?? "#888888",
                    connection.Status.ToString(), connection.AccountIdentifier, connection.LastActiveAt, a.DisplayOrder);
            });
        return Results.Ok(dtos);
    }

    private static async Task<IResult> AddAsync(AddDashboardAppRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connection = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.Id == req.ConnectionId && c.UserId == who.UserId).FirstOrDefaultAsync(ct);
        if (connection is null) return Results.Problem(statusCode: 404, title: "Connection not found.");

        var dashboardApps = db.GetCollection<UserDashboardApp>("userDashboardApps");
        var existing = await dashboardApps.Find(a => a.UserId == who.UserId && a.ConnectionId == req.ConnectionId).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            // Re-adding after a prior removal: just make it visible again at the end of the order, rather
            // than creating a second row for the same connection.
            var nextOrder = await NextDisplayOrderAsync(dashboardApps, who.UserId, ct);
            await dashboardApps.UpdateOneAsync(a => a.Id == existing.Id,
                Builders<UserDashboardApp>.Update.Set(a => a.IsVisible, true).Set(a => a.DisplayOrder, nextOrder).Set(a => a.UpdatedAt, DateTime.UtcNow), cancellationToken: ct);
            return Results.Ok(new { id = existing.Id });
        }

        var order = await NextDisplayOrderAsync(dashboardApps, who.UserId, ct);
        var app = new UserDashboardApp
        {
            UserId = who.UserId,
            ConnectionId = req.ConnectionId!,
            IsVisible = true,
            DisplayOrder = order,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await dashboardApps.InsertOneAsync(app, cancellationToken: ct);
        return Results.Ok(new { id = app.Id });
    }

    private static async Task<IResult> RemoveAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var result = await db.GetCollection<UserDashboardApp>("userDashboardApps")
            .DeleteOneAsync(a => a.Id == id && a.UserId == who.UserId, ct);
        return result.DeletedCount == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> PatchAsync(string id, PatchDashboardAppRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var dashboardApps = db.GetCollection<UserDashboardApp>("userDashboardApps");
        var existing = await dashboardApps.Find(a => a.Id == id && a.UserId == who.UserId).FirstOrDefaultAsync(ct);
        if (existing is null) return Results.NotFound();

        var update = Builders<UserDashboardApp>.Update.Set(a => a.UpdatedAt, DateTime.UtcNow);
        if (req.IsVisible is { } visible) update = update.Set(a => a.IsVisible, visible);
        if (req.DisplayOrder is { } order) update = update.Set(a => a.DisplayOrder, order);
        await dashboardApps.UpdateOneAsync(a => a.Id == id, update, cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<int> NextDisplayOrderAsync(IMongoCollection<UserDashboardApp> dashboardApps, string userId, CancellationToken ct)
    {
        var max = await dashboardApps.Find(a => a.UserId == userId).SortByDescending(a => a.DisplayOrder).Limit(1).FirstOrDefaultAsync(ct);
        return (max?.DisplayOrder ?? -1) + 1;
    }
}

using Daksh.Api.Auth;
using Daksh.Api.Infrastructure;
using FluentValidation;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record DashboardAppDto(string Id, string ConnectionId, string IntegrationKey, string Name, string Icon, string Color, string Status, string? AccountIdentifier, DateTime? LastActiveAt, int DisplayOrder, int UnreadCount, string? LastMessagePreview, DateTime? LastMessageAt, bool LastMessageIsGroup, int Cols = 0, int Height = 0, int Col = 0);
public sealed record AvailableAppDto(string ConnectionId, string IntegrationKey, string Name, string Icon, string Color, string? AccountIdentifier, string? DashboardId);
public sealed record ActivityItemDto(string IntegrationKey, string ConnectionId, string Icon, string Color, string ChatId, string? ChatName, bool IsGroup, bool FromMe, string Text, DateTime Timestamp);
public sealed record AddDashboardAppRequest(string? ConnectionId);
public sealed record PatchDashboardAppRequest(bool? IsVisible, int? DisplayOrder, int? Cols = null, int? Height = null, int? Col = null);
public sealed record DashboardLayoutItem(string? Id, int DisplayOrder, int Cols, int Height = 0, int Col = 0);

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
        g.MapGet("/available", AvailableAsync);
        g.MapPost("/", AddAsync).Validate<AddDashboardAppRequest>();
        g.MapDelete("/{id}", RemoveAsync);
        g.MapPatch("/{id}", PatchAsync);
        g.MapPut("/layout", SaveLayoutAsync);
        g.MapDelete("/layout", ResetLayoutAsync);

        app.MapGet("/api/dashboard/activity", GetActivityAsync).WithTags("Dashboard").RequireAuthorization(AuthSetup.UserScheme);
    }

    /// <summary>The dashboard's big activity feed: every recent message (individual or group, sent or
    /// received) across every WhatsApp connection the user has — not just ones pinned to the dashboard.</summary>
    private static async Task<IResult> GetActivityAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var connections = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == who.UserId && c.IntegrationKey == "whatsapp").ToListAsync(ct);
        if (connections.Count == 0) return Results.Ok(Array.Empty<ActivityItemDto>());

        var catalog = await db.GetCollection<IntegrationCatalogEntry>("integrations").Find(c => c.IntegrationKey == "whatsapp").FirstOrDefaultAsync(ct);
        var connectionIds = connections.Select(c => c.Id).ToList();

        var messages = await db.GetCollection<WhatsAppMessage>("whatsappMessages")
            .Find(m => connectionIds.Contains(m.ConnectionId))
            .SortByDescending(m => m.Timestamp).Limit(50).ToListAsync(ct);

        var items = messages.Select(m => new ActivityItemDto(
            "whatsapp", m.ConnectionId, catalog?.Icon ?? "SiWhatsapp", catalog?.Color ?? "#25D366",
            m.ChatId, m.ChatName, WhatsAppChat.IsGroup(m.ChatId), m.FromMe, m.Text, m.Timestamp));
        return Results.Ok(items);
    }

    /// <summary>Every connected app and account the user could show as a tile. DashboardId is set when it is already on the dashboard (WhatsApp and Telegram live in the Chats sidebar, so they are not offered).</summary>
    private static async Task<IResult> AvailableAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var onDashboard = (await db.GetCollection<UserDashboardApp>("userDashboardApps")
            .Find(a => a.UserId == who.UserId && a.IsVisible).ToListAsync(ct)).GroupBy(a => a.ConnectionId).ToDictionary(g => g.Key, g => g.First().Id);
        var connections = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == who.UserId && c.Status == ConnectionStatus.Connected && c.IntegrationKey != "whatsapp" && c.IntegrationKey != "telegram").ToListAsync(ct);
        var keys = connections.Select(c => c.IntegrationKey).Distinct().ToList();
        var catalog = (await db.GetCollection<IntegrationCatalogEntry>("integrations").Find(c => keys.Contains(c.IntegrationKey)).ToListAsync(ct)).ToDictionary(c => c.IntegrationKey);
        return Results.Ok(connections.Select(c =>
        {
            var entry = catalog.GetValueOrDefault(c.IntegrationKey);
            return new AvailableAppDto(c.Id, c.IntegrationKey, entry?.Name ?? c.IntegrationKey, entry?.Icon ?? "", entry?.Color ?? "#888888", c.AccountIdentifier, onDashboard.GetValueOrDefault(c.Id));
        }));
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

        // WhatsApp is the only integration with a message feed today; fetch each connection's unread count
        // and latest message in one grouped aggregation rather than a query per card.
        var whatsAppConnectionIds = connections.Where(c => c.IntegrationKey == "whatsapp").Select(c => c.Id).ToList();
        var latestByConnection = new Dictionary<string, WhatsAppMessage>();
        var unreadByConnection = new Dictionary<string, int>();
        if (whatsAppConnectionIds.Count > 0)
        {
            var messages = db.GetCollection<WhatsAppMessage>("whatsappMessages");
            foreach (var connectionId in whatsAppConnectionIds)
            {
                var latest = await messages.Find(m => m.ConnectionId == connectionId).SortByDescending(m => m.Timestamp).Limit(1).FirstOrDefaultAsync(ct);
                if (latest is not null) latestByConnection[connectionId] = latest;

                var lastViewedAt = connectionsById[connectionId].LastViewedAt ?? DateTime.MinValue;
                unreadByConnection[connectionId] = (int)await messages.CountDocumentsAsync(
                    m => m.ConnectionId == connectionId && !m.FromMe && m.Timestamp > lastViewedAt, cancellationToken: ct);
            }
        }

        var dtos = apps
            .Where(a => connectionsById.ContainsKey(a.ConnectionId))
            .Select(a =>
            {
                var connection = connectionsById[a.ConnectionId];
                var entry = catalogByKey.GetValueOrDefault(connection.IntegrationKey);
                var latest = latestByConnection.GetValueOrDefault(connection.Id);
                return new DashboardAppDto(
                    a.Id, connection.Id, connection.IntegrationKey,
                    entry?.Name ?? connection.IntegrationKey, entry?.Icon ?? "", entry?.Color ?? "#888888",
                    connection.Status.ToString(), connection.AccountIdentifier, connection.LastActiveAt, a.DisplayOrder,
                    unreadByConnection.GetValueOrDefault(connection.Id), latest?.Text, latest?.Timestamp,
                    latest is not null && WhatsAppChat.IsGroup(latest.ChatId), EffectiveCols(a), a.Height, a.Col);
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
        if (req.Cols is { } cols)
        {
            if (!ValidCols(cols)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["cols"] = ["Width must be between 3 and 12 columns (0 for the default)."] });
            update = update.Set(a => a.Cols, cols).Set(a => a.ColSpan, 0);
        }
        if (req.Col is { } start)
        {
            if (!ValidCol(start)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["col"] = ["The starting column must be between 1 and 10 (0 to flow automatically)."] });
            update = update.Set(a => a.Col, start);
        }
        if (req.Height is { } height)
        {
            if (!ValidHeight(height)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["height"] = ["Height must be between 160 and 2000 pixels (0 to fit the content)."] });
            update = update.Set(a => a.Height, height);
        }
        await dashboardApps.UpdateOneAsync(a => a.Id == id && a.UserId == who.UserId, update, cancellationToken: ct);
        return Results.NoContent();
    }

    /// <summary>A tile may be 3–12 columns wide in the 12-column grid; 0 means "use the card's default".</summary>
    public static bool ValidCols(int cols) => cols == 0 || cols is >= 3 and <= 12;

    /// <summary>The width to send to the browser: the 12-column value, or a legacy 3-column width converted (1→4, 2→8, 3→12).</summary>
    public static int EffectiveCols(UserDashboardApp a) => a.Cols > 0 ? a.Cols : a.ColSpan is >= 1 and <= 3 ? a.ColSpan * 4 : 0;
    /// <summary>The column a tile starts in: 1–10 (a tile is at least 3 wide, so it can't start later), or 0 for "flow after the previous tile".</summary>
    public static bool ValidCol(int col) => col is >= 0 and <= 10;
    public static bool ValidHeight(int height) => height == 0 || height is >= 160 and <= 2000;

    /// <summary>Saves the whole arrangement in one request: each tile's position and width. Only the caller's own tiles can change.</summary>
    private static async Task<IResult> SaveLayoutAsync(List<DashboardLayoutItem> items, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (items is null || items.Count == 0 || items.Count > 100 || items.Any(i => string.IsNullOrWhiteSpace(i.Id)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Send between 1 and 100 tiles, each with an id."] });
        if (items.Any(i => !ValidCols(i.Cols) || !ValidCol(i.Col) || !ValidHeight(i.Height) || i.DisplayOrder is < 0 or > 10_000) || items.Select(i => i.Id).Distinct().Count() != items.Count)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["items"] = ["Each tile needs a unique id, a position of 0 or more, a width of 0 or 3–12 columns and a height of 0 or 160–2000 pixels."] });

        var apps = db.GetCollection<UserDashboardApp>("userDashboardApps");
        var now = DateTime.UtcNow;
        var writes = items.Select(i => (WriteModel<UserDashboardApp>)new UpdateOneModel<UserDashboardApp>(
            Builders<UserDashboardApp>.Filter.Eq(a => a.Id, i.Id) & Builders<UserDashboardApp>.Filter.Eq(a => a.UserId, who.UserId),
            Builders<UserDashboardApp>.Update.Set(a => a.DisplayOrder, i.DisplayOrder).Set(a => a.Cols, i.Cols).Set(a => a.ColSpan, 0).Set(a => a.Height, i.Height).Set(a => a.Col, i.Col).Set(a => a.UpdatedAt, now))).ToList();
        var result = await apps.BulkWriteAsync(writes, cancellationToken: ct);
        return Results.Ok(new { updated = result.MatchedCount });
    }

    /// <summary>Back to the default arrangement: every tile at its default width, in the order it was added.</summary>
    private static async Task<IResult> ResetLayoutAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var apps = db.GetCollection<UserDashboardApp>("userDashboardApps");
        var all = await apps.Find(a => a.UserId == who.UserId).SortBy(a => a.CreatedAt).ToListAsync(ct);
        if (all.Count == 0) return Results.NoContent();
        var writes = all.Select((a, index) => (WriteModel<UserDashboardApp>)new UpdateOneModel<UserDashboardApp>(
            Builders<UserDashboardApp>.Filter.Eq(x => x.Id, a.Id) & Builders<UserDashboardApp>.Filter.Eq(x => x.UserId, who.UserId),
            Builders<UserDashboardApp>.Update.Set(x => x.DisplayOrder, index).Set(x => x.Cols, 0).Set(x => x.ColSpan, 0).Set(x => x.Height, 0).Set(x => x.Col, 0).Set(x => x.UpdatedAt, DateTime.UtcNow))).ToList();
        await apps.BulkWriteAsync(writes, cancellationToken: ct);
        return Results.NoContent();
    }

    private static async Task<int> NextDisplayOrderAsync(IMongoCollection<UserDashboardApp> dashboardApps, string userId, CancellationToken ct)
    {
        var max = await dashboardApps.Find(a => a.UserId == userId).SortByDescending(a => a.DisplayOrder).Limit(1).FirstOrDefaultAsync(ct);
        return (max?.DisplayOrder ?? -1) + 1;
    }
}

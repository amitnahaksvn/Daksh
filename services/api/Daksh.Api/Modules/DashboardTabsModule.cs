using Daksh.Api.Auth;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>One named dashboard of a user: which blocks it shows. The Ask tab is not stored; it is always there, first.</summary>
[BsonIgnoreExtraElements]
public sealed class DashboardTab
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    [BsonElement("userId")] public required string UserId { get; set; }
    [BsonElement("name")] public required string Name { get; set; }
    [BsonElement("order")] public int Order { get; set; }
    /// <summary>The blocks shown on this dashboard, from <see cref="DashboardTabs.Widgets"/>.</summary>
    [BsonElement("widgets")] public List<string> Widgets { get; set; } = [];
    [BsonElement("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record DashboardTabDto(string Id, string Name, int Order, IReadOnlyList<string> Widgets);
public sealed record CreateDashboardTabRequest(string? Name, List<string>? Widgets);
public sealed record UpdateDashboardTabRequest(string? Name, List<string>? Widgets);

/// <summary>Rules for dashboard tabs, kept apart from the endpoints so they can be tested directly.</summary>
public static class DashboardTabs
{
    /// <summary>The blocks a dashboard can show: the Command Centre, and the movable app tiles.</summary>
    public static readonly string[] Widgets = ["command", "apps"];
    public const int MaxTabs = 12;
    public const int MaxNameLength = 40;

    /// <summary>A usable tab name: trimmed, inner whitespace collapsed, at most 40 characters; null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim(), @"\s+", " ");
        if (cleaned.Length == 0) return null;
        return cleaned.Length > MaxNameLength ? cleaned[..MaxNameLength].TrimEnd() : cleaned;
    }

    /// <summary>Only known blocks, each once, in the catalog's order.</summary>
    public static List<string> CleanWidgets(IEnumerable<string>? widgets) =>
        Widgets.Where(w => widgets?.Contains(w, StringComparer.Ordinal) == true).ToList();

    /// <summary>"Dashboard", then "Dashboard 2", "Dashboard 3", ... skipping any name already in use.</summary>
    public static string NextDefaultName(IEnumerable<string> existing)
    {
        var used = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains("Dashboard")) return "Dashboard";
        for (var n = 2; ; n++) if (!used.Contains($"Dashboard {n}")) return $"Dashboard {n}";
    }
}

public static class DashboardTabsModule
{
    public static void MapDashboardTabs(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/dashboards").WithTags("Dashboard").RequireAuthorization(AuthSetup.UserScheme);
        g.MapGet("/", ListAsync);
        g.MapPost("/", CreateAsync);
        g.MapPatch("/{id}", UpdateAsync);
        g.MapDelete("/{id}", DeleteAsync);
    }

    private static IMongoCollection<DashboardTab> Tabs(IMongoDatabase db) => db.GetCollection<DashboardTab>("dashboardTabs");
    private static DashboardTabDto ToDto(DashboardTab t) => new(t.Id, t.Name, t.Order, t.Widgets);

    /// <summary>The user's dashboards in order. A user who has none yet gets one called "Dashboard" showing everything.</summary>
    private static async Task<IResult> ListAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var tabs = await Tabs(db).Find(t => t.UserId == who.UserId).SortBy(t => t.Order).ToListAsync(ct);
        if (tabs.Count == 0)
        {
            var first = new DashboardTab { UserId = who.UserId, Name = "Dashboard", Order = 0, Widgets = [.. DashboardTabs.Widgets] };
            await Tabs(db).InsertOneAsync(first, cancellationToken: ct);
            tabs = [first];
        }
        return Results.Ok(tabs.Select(ToDto));
    }

    private static async Task<IResult> CreateAsync(CreateDashboardTabRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var tabs = await Tabs(db).Find(t => t.UserId == who.UserId).ToListAsync(ct);
        if (tabs.Count >= DashboardTabs.MaxTabs) return Results.Problem(statusCode: 409, title: $"You can have up to {DashboardTabs.MaxTabs} dashboards.");
        var name = DashboardTabs.CleanName(req.Name) ?? DashboardTabs.NextDefaultName(tabs.Select(t => t.Name));
        var tab = new DashboardTab
        {
            UserId = who.UserId, Name = name, Order = tabs.Count == 0 ? 0 : tabs.Max(t => t.Order) + 1,
            Widgets = req.Widgets is null ? [.. DashboardTabs.Widgets] : DashboardTabs.CleanWidgets(req.Widgets),
        };
        await Tabs(db).InsertOneAsync(tab, cancellationToken: ct);
        return Results.Ok(ToDto(tab));
    }

    private static async Task<IResult> UpdateAsync(string id, UpdateDashboardTabRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var update = Builders<DashboardTab>.Update.Set(t => t.UserId, who.UserId);
        if (req.Name is not null)
        {
            var name = DashboardTabs.CleanName(req.Name);
            if (name is null) return Results.Problem(statusCode: 400, title: "A dashboard needs a name.");
            update = update.Set(t => t.Name, name);
        }
        if (req.Widgets is not null) update = update.Set(t => t.Widgets, DashboardTabs.CleanWidgets(req.Widgets));
        var res = await Tabs(db).UpdateOneAsync(t => t.Id == id && t.UserId == who.UserId, update, cancellationToken: ct);
        if (res.MatchedCount == 0) return Results.NotFound();
        var tab = await Tabs(db).Find(t => t.Id == id && t.UserId == who.UserId).FirstAsync(ct);
        return Results.Ok(ToDto(tab));
    }

    private static async Task<IResult> DeleteAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (await Tabs(db).CountDocumentsAsync(t => t.UserId == who.UserId, cancellationToken: ct) <= 1)
            return Results.Problem(statusCode: 409, title: "You need at least one dashboard.");
        var res = await Tabs(db).DeleteOneAsync(t => t.Id == id && t.UserId == who.UserId, ct);
        return res.DeletedCount == 0 ? Results.NotFound() : Results.NoContent();
    }
}

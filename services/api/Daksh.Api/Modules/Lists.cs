using Daksh.Api.Auth;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

/// <summary>One thing on one of the owner's lists ("Shopping", "Amazon", "To-do"…), added from the app or by messaging themselves.</summary>
public sealed class UserListItem
{
    [BsonId, BsonRepresentation(BsonType.ObjectId)] public string Id { get; set; } = ObjectId.GenerateNewId().ToString();
    public required string UserId { get; set; }
    public required string List { get; set; }
    public required string Text { get; set; }
    public bool Done { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record ListItemDto(string Id, string List, string Text, bool Done, DateTime CreatedAt);
public sealed record AddListItemRequest(string? List, string? Text);

public static class ListsModule
{
    public const string DefaultShopping = "Shopping";
    public const string DefaultTodo = "To-do";

    private static IMongoCollection<UserListItem> Items(IMongoDatabase db) => db.GetCollection<UserListItem>("userListItems");
    public static string CleanName(string? name)
    {
        var n = Regex.Replace(name ?? "", @"\s+", " ").Trim().Trim('"', '\'', ':', '-');
        if (n.Length == 0) return DefaultTodo;
        // "buy list", "shopping list", "to buy"… are all the Shopping list; "to do", "tasks"… are all the To-do list.
        var key = Regex.Replace(n.ToLowerInvariant(), @"\b(my|the|a)\b|\blist\b", "").Trim();
        if (key is "buy" or "buying" or "to buy" or "shopping" or "shop" or "purchase" or "purchases" or "buylist" or "buy ") return DefaultShopping;
        if (key is "todo" or "to do" or "to-do" or "task" or "tasks" or "todos" or "to dos" or "") return DefaultTodo;
        if (n.Length > 40) n = n[..40].Trim();
        return char.ToUpperInvariant(n[0]) + n[1..];
    }

    /// <summary>Adds an item. Returns it and how many open items the list now has (so the confirmation can say "3 items").</summary>
    public static async Task<(UserListItem Item, int OpenCount, bool Duplicate)> AddAsync(IMongoDatabase db, string userId, string list, string text, CancellationToken ct = default)
    {
        list = CleanName(list);
        text = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        if (text.Length > 300) text = text[..300];
        var items = Items(db);
        var existing = await items.Find(i => i.UserId == userId && i.List == list && !i.Done && i.Text == text).FirstOrDefaultAsync(ct);
        if (existing is not null) return (existing, (int)await items.CountDocumentsAsync(i => i.UserId == userId && i.List == list && !i.Done, cancellationToken: ct), true);
        var item = new UserListItem { UserId = userId, List = list, Text = text };
        await items.InsertOneAsync(item, cancellationToken: ct);
        return (item, (int)await items.CountDocumentsAsync(i => i.UserId == userId && i.List == list && !i.Done, cancellationToken: ct), false);
    }

    /// <summary>Removes the newest open item whose text matches (case-insensitive). Returns it, or null.</summary>
    public static async Task<UserListItem?> RemoveByTextAsync(IMongoDatabase db, string userId, string? list, string text, CancellationToken ct = default)
    {
        var open = await Items(db).Find(i => i.UserId == userId && !i.Done).SortByDescending(i => i.CreatedAt).ToListAsync(ct);
        var hit = open.FirstOrDefault(i => (list is null || i.List.Equals(CleanName(list), StringComparison.OrdinalIgnoreCase)) && i.Text.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? open.FirstOrDefault(i => (list is null || i.List.Equals(CleanName(list), StringComparison.OrdinalIgnoreCase)) && i.Text.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit is null) return null;
        await Items(db).DeleteOneAsync(i => i.Id == hit.Id, ct);
        return hit;
    }

    public static async Task<IReadOnlyList<UserListItem>> OpenAsync(IMongoDatabase db, string userId, int limit, CancellationToken ct = default) =>
        await Items(db).Find(i => i.UserId == userId && !i.Done).SortBy(i => i.List).ThenByDescending(i => i.CreatedAt).Limit(limit).ToListAsync(ct);

    /// <summary>The open lists as text the assistant (or a chat reply) can read, grouped by list.</summary>
    public static string Summarise(IEnumerable<UserListItem> open) =>
        string.Join("\n", open.GroupBy(i => i.List).Select(g => $"{g.Key} ({g.Count()}): " + string.Join(", ", g.Select(i => i.Text))));

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/lists").WithTags("Lists").RequireAuthorization(AuthSetup.UserScheme);
        g.MapGet("", async (IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
            Results.Ok((await Items(db).Find(i => i.UserId == who.UserId).SortBy(i => i.List).ThenByDescending(i => i.CreatedAt).Limit(500).ToListAsync(ct))
                .Select(i => new ListItemDto(i.Id, i.List, i.Text, i.Done, i.CreatedAt))));
        g.MapPost("", async (AddListItemRequest req, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.Problem(statusCode: 400, title: "Write what to add.");
            var (item, _, _) = await AddAsync(db, who.UserId, req.List ?? DefaultTodo, req.Text, ct);
            return Results.Ok(new ListItemDto(item.Id, item.List, item.Text, item.Done, item.CreatedAt));
        });
        g.MapPost("/{id}/toggle", async (string id, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            var item = await Items(db).Find(i => i.Id == id && i.UserId == who.UserId).FirstOrDefaultAsync(ct);
            if (item is null) return Results.NotFound();
            await Items(db).UpdateOneAsync(i => i.Id == id, Builders<UserListItem>.Update.Set(i => i.Done, !item.Done), cancellationToken: ct);
            return Results.NoContent();
        });
        g.MapDelete("/{id}", async (string id, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        {
            await Items(db).DeleteOneAsync(i => i.Id == id && i.UserId == who.UserId, ct);
            return Results.NoContent();
        });
    }
}

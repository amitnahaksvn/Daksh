using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Daksh.Api.Auth;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

public sealed record ShoppingStoreDto(string Key, string Name, string Home, string SearchUrl, bool Connected, string? ConnectionId, bool Cart = false, string Session = "none");
public sealed record AddToCartRequest(string[]? Items);
public sealed record CartItemResult(string Query, string Status, string? Title, string? Price, string? Note);
public sealed record CartLine(string Title, string? Price);
public sealed record CartSummaryDto(int? Count, string? Total, IReadOnlyList<CartLine> Items, string Url);
public sealed record AddToCartResponse(IReadOnlyList<CartItemResult> Results, CartSummaryDto? Cart);

/// <summary>
/// The shopping apps. None of them offers an official way for another app to act on a buyer's account. For Amazon and Flipkart, Daksha therefore works through a small
/// helper that runs on the user's own computer and drives their own Chrome: the user signs in once in a real Chrome window (Daksha never sees the login), and the helper
/// then adds items to the user's cart and reads it back. It never opens checkout or touches payment: the user reviews and pays in the store's own app or site.
/// The other stores are turned on per user and open the user's shopping list as store searches.
/// </summary>
public static class ShoppingModule
{
    /// <summary>{0} is replaced by the URL-encoded item. These are the stores' public search pages.</summary>
    public static readonly IReadOnlyDictionary<string, (string Name, string Home, string Search)> Stores = new Dictionary<string, (string, string, string)>
    {
        ["amazon"] = ("Amazon", "https://www.amazon.in/", "https://www.amazon.in/s?k={0}"),
        ["flipkart"] = ("Flipkart", "https://www.flipkart.com/", "https://www.flipkart.com/search?q={0}"),
    };

    /// <summary>Stores whose cart Daksha can fill through the local shopping helper.</summary>
    public static readonly HashSet<string> CartStores = ["amazon", "flipkart"];

    public static IServiceCollection AddShopping(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient("shopping", c =>
        {
            c.BaseAddress = new Uri((config["Integrations:Shopping:BaseUrl"] ?? "http://127.0.0.1:4300").TrimEnd('/') + "/");
            c.DefaultRequestHeaders.Add("X-Internal-Key", config["Integrations:Shopping:InternalServiceKey"] ?? "");
            c.Timeout = TimeSpan.FromMinutes(4); // adding several items drives a real browser, one item after another
        });
        return services;
    }

    public static void MapShopping(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations/shopping").WithTags("Shopping").RequireAuthorization(AuthSetup.UserScheme);
        g.MapGet("", ListAsync);
        g.MapPost("/{key}/enable", EnableAsync);
        g.MapDelete("/{key}", DisableAsync);
        g.MapPost("/{key}/session/start", StartSessionAsync);
        g.MapDelete("/{key}/session", EndSessionAsync);
        g.MapPost("/{key}/cart/add", AddToCartAsync);
        g.MapGet("/{key}/cart", ReadCartAsync);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record SessionDto(string State);
    private sealed record ServiceError(string? Error, string? Code);

    /// <summary>"signing_in" / "connected" / "none", or "unavailable" when the helper is not running on this computer.</summary>
    private static async Task<string> SessionStateAsync(IHttpClientFactory clients, string userId, string key, CancellationToken ct)
    {
        try
        {
            using var res = await clients.CreateClient("shopping").GetAsync($"sessions/{Uri.EscapeDataString(userId)}/{key}", ct);
            return res.IsSuccessStatusCode ? (await res.Content.ReadFromJsonAsync<SessionDto>(Json, ct))?.State ?? "none" : "unavailable";
        }
        catch (HttpRequestException) { return "unavailable"; }
        catch (TaskCanceledException) { return "unavailable"; }
    }

    private static IResult Unavailable() => Results.Problem(statusCode: 503, title: "Daksha's shopping helper is not running on this computer.",
        detail: "Start it (services/shopping) so Daksha can use your own Chrome to fill your cart.", extensions: new Dictionary<string, object?> { ["code"] = "helper_unavailable" });

    /// <summary>Passes the helper's answer (or its error) back to the web app.</summary>
    private static async Task<IResult> RelayAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return Results.Ok(await res.Content.ReadFromJsonAsync<T>(Json, ct));
        var err = await res.Content.ReadFromJsonAsync<ServiceError>(Json, ct).ConfigureAwait(false);
        var status = (int)res.StatusCode is >= 400 and < 600 ? (int)res.StatusCode : 502;
        return Results.Problem(statusCode: status, title: err?.Error ?? "The shopping helper could not do that.", extensions: err?.Code is null ? null : new Dictionary<string, object?> { ["code"] = err.Code });
    }

    private static async Task SyncConnectionAsync(IMongoDatabase db, string userId, string key, string state, CancellationToken ct)
    {
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var open = await connections.Find(c => c.UserId == userId && c.IntegrationKey == key && c.Status == ConnectionStatus.Connected).AnyAsync(ct);
        if (state == "connected" && !open) await EnableForAsync(key, db, userId, ct);
        else if (state == "none" && open) await DisableForAsync(key, db, userId, ct);
    }

    private static async Task<IResult> StartSessionAsync(string key, IHttpClientFactory clients, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (!CartStores.Contains(key)) return Results.NotFound();
        try
        {
            using var res = await clients.CreateClient("shopping").PostAsync($"sessions/{Uri.EscapeDataString(who.UserId)}/{key}/start", null, ct);
            return await RelayAsync<SessionDto>(res, ct);
        }
        catch (HttpRequestException) { return Unavailable(); }
    }

    private static async Task<IResult> EndSessionAsync(string key, IMongoDatabase db, IHttpClientFactory clients, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (!CartStores.Contains(key)) return Results.NotFound();
        try
        {
            using var res = await clients.CreateClient("shopping").DeleteAsync($"sessions/{Uri.EscapeDataString(who.UserId)}/{key}", ct);
            if (!res.IsSuccessStatusCode && res.StatusCode != HttpStatusCode.NoContent) return await RelayAsync<object>(res, ct);
        }
        catch (HttpRequestException) { return Unavailable(); }
        await DisableForAsync(key, db, who.UserId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> AddToCartAsync(string key, AddToCartRequest req, IHttpClientFactory clients, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (!CartStores.Contains(key)) return Results.NotFound();
        var items = (req.Items ?? []).Select(i => i?.Trim() ?? "").Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (items.Length == 0) return Results.Problem(statusCode: 400, title: "Say what to add.");
        try
        {
            using var res = await clients.CreateClient("shopping").PostAsJsonAsync($"cart/{Uri.EscapeDataString(who.UserId)}/{key}/add", new { items }, ct);
            return await RelayAsync<AddToCartResponse>(res, ct);
        }
        catch (HttpRequestException) { return Unavailable(); }
        catch (TaskCanceledException) { return Results.Problem(statusCode: 504, title: "The store took too long. Check your cart on the store, then try again."); }
    }

    private static async Task<IResult> ReadCartAsync(string key, IHttpClientFactory clients, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (!CartStores.Contains(key)) return Results.NotFound();
        try
        {
            using var res = await clients.CreateClient("shopping").GetAsync($"cart/{Uri.EscapeDataString(who.UserId)}/{key}", ct);
            return await RelayAsync<CartSummaryDto>(res, ct);
        }
        catch (HttpRequestException) { return Unavailable(); }
        catch (TaskCanceledException) { return Results.Problem(statusCode: 504, title: "The store took too long. Please try again."); }
    }

    /// <summary>Used by the chat assistants: adds items for the owner and says, in words, what happened. The owner always pays on the store themselves.</summary>
    public static async Task<string> AddForChatAsync(IHttpClientFactory clients, string userId, string key, IReadOnlyList<string> items, CancellationToken ct)
    {
        if (!Stores.TryGetValue(key, out var store)) return "ℹ️ I don't know that store.";
        if (!CartStores.Contains(key)) return $"ℹ️ I can't fill a {store.Name} cart yet. Add the items to your shopping list and open them on {store.Name} from the Apps page.";
        if (items.Count == 0) return $"What should I add to your {store.Name} cart?";
        try
        {
            using var res = await clients.CreateClient("shopping").PostAsJsonAsync($"cart/{Uri.EscapeDataString(userId)}/{key}/add", new { items }, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadFromJsonAsync<ServiceError>(Json, ct).ConfigureAwait(false);
                return err?.Code == "not_signed_in" ? $"ℹ️ You are not signed in to {store.Name} in Daksha. Open Apps → {store.Name} and sign in, then ask again." : "ℹ️ " + (err?.Error ?? "I couldn't reach the store right now.");
            }
            var done = await res.Content.ReadFromJsonAsync<AddToCartResponse>(Json, ct);
            if (done is null) return "ℹ️ I couldn't read the store's answer.";
            var lines = done.Results.Select(r => r.Status == "added" ? $"✅ {r.Title ?? r.Query}{(r.Price is null ? "" : " — " + r.Price)}" : $"❌ {r.Query}: {r.Note ?? r.Status}");
            var added = done.Results.Count(r => r.Status == "added");
            var tail = added == 0 ? "Nothing was added." : $"{added} of {done.Results.Count} added to your {store.Name} cart" + (done.Cart?.Total is { } t ? $" (cart total {t})" : "") + ". Open the cart to check and place the order yourself:\n" + store.Home + (key == "amazon" ? "cart" : "viewcart");
            return string.Join("\n", lines) + "\n\n" + tail;
        }
        catch (HttpRequestException) { return "ℹ️ Daksha's shopping helper is not running on this computer."; }
        catch (TaskCanceledException) { return "ℹ️ The store took too long. Check your cart on the store."; }
    }

    private static async Task<IResult> ListAsync(IMongoDatabase db, IHttpClientFactory clients, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var keys = Stores.Keys.ToList();
        var connections = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == who.UserId && keys.Contains(c.IntegrationKey) && c.Status == ConnectionStatus.Connected).ToListAsync(ct);
        var sessions = new Dictionary<string, string>();
        foreach (var k in CartStores)
        {
            var state = await SessionStateAsync(clients, who.UserId, k, ct);
            sessions[k] = state;
            if (state is "connected" or "none") await SyncConnectionAsync(db, who.UserId, k, state, ct); // the browser's sign-in is the truth for these stores
        }
        connections = await db.GetCollection<IntegrationConnection>("integrationConnections")
            .Find(c => c.UserId == who.UserId && keys.Contains(c.IntegrationKey) && c.Status == ConnectionStatus.Connected).ToListAsync(ct);
        return Results.Ok(Stores.Select(s =>
        {
            var c = connections.FirstOrDefault(x => x.IntegrationKey == s.Key);
            return new ShoppingStoreDto(s.Key, s.Value.Name, s.Value.Home, s.Value.Search, c is not null, c?.Id, CartStores.Contains(s.Key), sessions.GetValueOrDefault(s.Key, "none"));
        }));
    }

    private static async Task<IResult> EnableAsync(string key, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (!Stores.ContainsKey(key)) return Results.NotFound();
        if (CartStores.Contains(key)) return Results.Problem(statusCode: 400, title: "Sign in to this store from the Apps page.");
        await EnableForAsync(key, db, who.UserId, ct);
        return Results.NoContent();
    }

    private static async Task EnableForAsync(string key, IMongoDatabase db, string userId, CancellationToken ct)
    {
        var store = Stores[key];
        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var now = DateTime.UtcNow;
        var existing = await connections.Find(c => c.UserId == userId && c.IntegrationKey == key).SortByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (existing is null)
            await connections.InsertOneAsync(new IntegrationConnection
            {
                UserId = userId, IntegrationKey = key, DisplayName = store.Name, Status = ConnectionStatus.Connected,
                ConnectedAt = now, CreatedAt = now, UpdatedAt = now,
            }, cancellationToken: ct);
        else
            await connections.UpdateOneAsync(c => c.Id == existing.Id, Builders<IntegrationConnection>.Update
                .Set(c => c.Status, ConnectionStatus.Connected).Set(c => c.ConnectedAt, now).Set(c => c.DisconnectedAt, null).Set(c => c.UpdatedAt, now), cancellationToken: ct);
    }

    private static async Task<IResult> DisableAsync(string key, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (!Stores.ContainsKey(key)) return Results.NotFound();
        await DisableForAsync(key, db, who.UserId, ct);
        return Results.NoContent();
    }

    private static async Task DisableForAsync(string key, IMongoDatabase db, string userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.UserId == userId && c.IntegrationKey == key && c.Status == ConnectionStatus.Connected).ToListAsync(ct);
        foreach (var c in existing)
        {
            await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(x => x.Id == c.Id,
                Builders<IntegrationConnection>.Update.Set(x => x.Status, ConnectionStatus.Disconnected).Set(x => x.DisconnectedAt, now).Set(x => x.UpdatedAt, now), cancellationToken: ct);
            await db.GetCollection<UserDashboardApp>("userDashboardApps").DeleteManyAsync(a => a.UserId == userId && a.ConnectionId == c.Id, ct);
        }
    }
}

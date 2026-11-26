using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Daksh.Api.Modules;

// Batch 3, part 2: stores and books (WooCommerce, eBay, Xero, Zoho Books), travel and maps (Mapbox, Amadeus), social (Mastodon, Bluesky),
// automation hooks (IFTTT, Zapier, Make), the home (Philips Hue, SmartThings, Tuya, Sonos) and utilities (Weather, Bhashini).

public static partial class ProviderApps
{
    private static string StripHtml(string? html) => System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html ?? "", @"<(br|/p)\s*/?>", "\n"), "<[^>]+>", "")).Trim();

    /// <summary>The (name, value) pairs of a JSON object, for services that key their lists by id.</summary>
    private static IEnumerable<(string Name, JsonElement Value)> Props(JsonElement e) => e.ValueKind == JsonValueKind.Object ? e.EnumerateObject().Select(p => (p.Name, p.Value)) : [];

    private static string Money2(string? value, string? currency) => string.IsNullOrEmpty(value) ? "" : $"{value} {currency}".Trim();

    // ============================================================================================
    // WooCommerce (your own store: address, consumer key and secret over https)
    // ============================================================================================
    private static string WooApi(ProviderCtx c) => c.Cred["url"] + "/wp-json/wc/v3/";

    private static ProviderApp WooCommerce() => new()
    {
        Key = "woocommerce", Name = "WooCommerce", Icon = "SiWoocommerce", Color = "#96588A", Account = "Orders, products and customers",
        ConnectTitle = "Connect your WooCommerce store", ConnectLabel = "Connect store",
        ConnectBlurb = "Create REST API keys in WordPress (WooCommerce → Settings → Advanced → REST API) and enter them with your store address.",
        WriteLabel = "Allow Daksh to update orders", WriteHint = "Full access lets Daksha add order notes and change an order's status (the key needs Read/Write).",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#woocommerce",
        Ask = Rx("woocommerce|my store|orders?|products?|customers?"),
        NoBearer = true, Decorate = BasicFromCred("key", "secret"),
        Setup = ["Nothing to set on the server. Each store owner creates **REST API keys** (WooCommerce → Settings → Advanced → REST API) with Read (or Read/Write) permission.", "In Daksha they enter the store address (must be https), the consumer key and the consumer secret."],
        Credentials = new()
        {
            Fields = [F("url", "Store address", "url", "https://shop.example.com"), F("key", "Consumer key", placeholder: "ck_…"), F("secret", "Consumer secret", "secret", "cs_…")],
            Prepare = PrepareUrl("url", allowHttp: false),
            Probe = async c => { using var d = await c.GetAsync(WooApi(c) + "orders?per_page=1"); return new ProfileInfo(new Uri(c.Cred["url"]).Authority, new Uri(c.Cred["url"]).Host); },
        },
        Views =
        [
            new ViewSpec { Id = "orders", Label = "Orders", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(WooApi(c) + "orders?per_page=25");
                return J.Items(d.RootElement, "", o => new AppItem(J.S(o, "id") ?? "", $"#{J.S(o, "number")} · {Money2(J.S(o, "total"), J.S(o, "currency"))}", $"{J.T(o, "{billing.first_name} {billing.last_name}")} · {J.When(J.S(o, "date_created"))}", null, J.S(o, "status"))); } },
            new ViewSpec { Id = "products", Label = "Products", Load = async (c, _) => { using var d = await c.GetAsync(WooApi(c) + "products?per_page=25");
                return J.Items(d.RootElement, "", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "name") ?? "Product", $"{J.S(p, "price")} · {J.S(p, "stock_status")}", J.S(p, "permalink"), null, J.S(p, "images[0].src"))); } },
            new ViewSpec { Id = "customers", Label = "Customers", Load = async (c, _) => { using var d = await c.GetAsync(WooApi(c) + "customers?per_page=25");
                return J.Items(d.RootElement, "", u => new AppItem(J.S(u, "id") ?? "", J.T(u, "{first_name} {last_name}") is { Length: > 0 } n ? n : J.S(u, "email") ?? "Customer", $"{J.S(u, "email")} · {J.S(u, "orders_count") ?? "0"} orders")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "note", Label = "Add a note to an order", Inputs = [F("order", "Order id", "number"), F("note", "Note", "textarea")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["order"])) throw new AppException(400, "Use the numeric order id."); using var _ = await c.PostAsync(WooApi(c) + $"orders/{i["order"]}/notes", new { note = i["note"] }); return new ActionResult("Note added."); } },
            new ActionSpec { Id = "status", Label = "Change an order's status", Inputs = [F("order", "Order id", "number"), new("status", "New status", "select", null, true, ["processing", "completed", "on-hold", "cancelled"], null, "processing")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["order"])) throw new AppException(400, "Use the numeric order id."); using var _ = await c.JsonAsync(HttpMethod.Put, WooApi(c) + $"orders/{i["order"]}", new { status = i["status"] }); return new ActionResult("Status updated."); } },
        ],
    };

    // ============================================================================================
    // eBay (sell-side: orders and listings; read-only)
    // ============================================================================================
    private static bool EbaySandbox(IConfiguration c) => c["EBay:Environment"] == "sandbox";
    private static string EbayApi(ProviderCtx c) => EbaySandbox(c.Config) ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
    private static readonly string[] EbayScopes = ["https://api.ebay.com/oauth/api_scope/sell.fulfillment.readonly", "https://api.ebay.com/oauth/api_scope/sell.inventory.readonly", "https://api.ebay.com/oauth/api_scope/commerce.identity.readonly"];

    private static ProviderApp Ebay() => new()
    {
        Key = "ebay", Name = "eBay", Icon = "SiEbay", Color = "#E53238", Account = "Orders and listings",
        ConnectTitle = "Connect your eBay seller account", ConnectLabel = "Continue with eBay",
        ConnectBlurb = "Sign in with eBay and Daksha can show your recent orders and listings. Read-only.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#ebay",
        Ask = Rx("ebay"),
        ConfiguredOverride = c => ProviderEngine.TryGetOAuthConfig(Get("ebay"), c, out _) && !string.IsNullOrWhiteSpace(c["EBay:RuName"]),
        Setup = ["developer.ebay.com → Hardware/Application keys: create a keyset (use Sandbox first). Copy the **App ID** (client id) and **Cert ID** (client secret).", "User Tokens → **Get a Token from eBay via Your Application**: create a **RuName** whose “Auth accepted URL” is `<API address>/api/integrations/ebay/callback`. Set it as `EBay__RuName`.", "Set `EBay__Environment=sandbox` while testing, and remove it for production."],
        Notes = "eBay's redirect value is the RuName, not a URL. Production keys need eBay's application growth check for some APIs.",
        OAuth = new()
        {
            Section = "EBay", AuthUrl = "https://auth.ebay.com/oauth2/authorize", TokenUrl = "https://api.ebay.com/identity/v1/oauth2/token", BasicAuth = true, ReadScopes = EbayScopes,
            BuildAuthUrl = (config, c, state, _, _, _) => QueryHelpers.AddQueryString(EbaySandbox(config) ? "https://auth.sandbox.ebay.com/oauth2/authorize" : "https://auth.ebay.com/oauth2/authorize",
                new Dictionary<string, string?> { ["client_id"] = c.ClientId, ["redirect_uri"] = config["EBay:RuName"], ["response_type"] = "code", ["scope"] = string.Join(' ', EbayScopes), ["state"] = state }),
            Exchange = async x =>
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, EbaySandbox(x.Configuration) ? "https://api.sandbox.ebay.com/identity/v1/oauth2/token" : "https://api.ebay.com/identity/v1/oauth2/token")
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = x.Code, ["redirect_uri"] = x.Configuration["EBay:RuName"] ?? "" }) };
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{x.Config.ClientId}:{x.Config.ClientSecret}")));
                using var res = await x.Clients.CreateClient("provider-apps").SendAsync(req, x.Ct);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(x.Ct));
                return ProviderEngine.ReadTokens(x.App.OAuth!, doc.RootElement, null);
            },
        },
        Profile = async c => { using var d = await c.GetAsync((EbaySandbox(c.Config) ? "https://apiz.sandbox.ebay.com" : "https://apiz.ebay.com") + "/commerce/identity/v1/user/"); return new ProfileInfo(J.S(d.RootElement, "userId") ?? J.S(d.RootElement, "username") ?? "", J.S(d.RootElement, "username") ?? "eBay seller"); },
        Views =
        [
            new ViewSpec { Id = "orders", Label = "Orders", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(EbayApi(c) + "/sell/fulfillment/v1/order?limit=25");
                return J.Items(d.RootElement, "orders", o => new AppItem(J.S(o, "orderId") ?? "", $"{J.S(o, "buyer.username")} · {Money2(J.S(o, "pricingSummary.total.value"), J.S(o, "pricingSummary.total.currency"))}", J.When(J.S(o, "creationDate")), null, J.S(o, "orderFulfillmentStatus"))); } },
            new ViewSpec { Id = "listings", Label = "Inventory", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(EbayApi(c) + "/sell/inventory/v1/inventory_item?limit=25");
                return J.Items(d.RootElement, "inventoryItems", i => new AppItem(J.S(i, "sku") ?? "", J.S(i, "product.title") ?? J.S(i, "sku") ?? "Item", $"SKU {J.S(i, "sku")}", null, $"{J.S(i, "availability.shipToLocationAvailability.quantity") ?? "0"} in stock", J.S(i, "product.imageUrls[0]"))); } },
        ],
    };

    // ============================================================================================
    // Xero
    // ============================================================================================
    private const string XeroApi = "https://api.xero.com/api.xro/2.0/";

    private static ProviderApp Xero() => new()
    {
        Key = "xero", Name = "Xero", Icon = "SiXero", Color = "#13B5EA", Account = "Invoices, contacts and accounts",
        ConnectTitle = "Connect Xero", ConnectLabel = "Continue with Xero",
        ConnectBlurb = "Sign in with Xero and choose the organisation to share. Daksha can show invoices and contacts.",
        WriteLabel = "Allow Daksh to create contacts", WriteHint = "Full access lets Daksha create contacts.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#xero",
        Ask = Rx("xero|invoices?|unpaid|bills?|accounting"),
        NoBearer = false, Decorate = (r, c) => { r.Headers.Accept.ParseAdd("application/json"); if (c.Extra("tenantId") is { Length: > 0 } t) r.Headers.TryAddWithoutValidation("xero-tenant-id", t); },
        Setup = ["developer.xero.com → My Apps → **New app** (Web app). Copy the client id and secret.", "Scopes used: `accounting.transactions.read`, `accounting.contacts.read`, `accounting.settings.read` (read) and `accounting.contacts` (full access). Each person chooses one organisation when signing in."],
        OAuth = new()
        {
            Section = "Xero", AuthUrl = "https://login.xero.com/identity/connect/authorize", TokenUrl = "https://identity.xero.com/connect/token", BasicAuth = true,
            ReadScopes = ["offline_access", "openid", "profile", "email", "accounting.transactions.read", "accounting.contacts.read", "accounting.settings.read"], WriteScopes = ["accounting.contacts"],
        },
        Profile = async c => { using var d = await c.GetAsync("https://api.xero.com/connections"); var t = J.Arr(d.RootElement, "").FirstOrDefault(); if (t.ValueKind != JsonValueKind.Object) throw new AppException(400, "No Xero organisation was shared.");
            return new ProfileInfo(J.S(t, "tenantId") ?? "", J.S(t, "tenantName") ?? "Xero", new() { ["tenantId"] = J.S(t, "tenantId") ?? "" }); },
        Views =
        [
            new ViewSpec { Id = "invoices", Label = "Recent invoices", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(XeroApi + "Invoices?order=UpdatedDateUTC%20DESC&page=1");
                return J.Items(d.RootElement, "Invoices", i => new AppItem(J.S(i, "InvoiceID") ?? "", $"{J.S(i, "InvoiceNumber")} · {J.S(i, "Contact.Name")}", $"{Money2(J.S(i, "Total"), J.S(i, "CurrencyCode"))} · due {J.When(J.S(i, "DueDateString"))}", $"https://go.xero.com/AccountsReceivable/View.aspx?InvoiceID={J.S(i, "InvoiceID")}", J.S(i, "Status")), 25); } },
            new ViewSpec { Id = "unpaid", Label = "Awaiting payment", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(XeroApi + $"Invoices?where={J.E("Status==\"AUTHORISED\"")}&order=DueDate");
                return J.Items(d.RootElement, "Invoices", i => new AppItem(J.S(i, "InvoiceID") ?? "", $"{J.S(i, "InvoiceNumber")} · {J.S(i, "Contact.Name")}", $"{Money2(J.S(i, "AmountDue"), J.S(i, "CurrencyCode"))} due {J.When(J.S(i, "DueDateString"))}", null, J.S(i, "Status")), 25); } },
            new ViewSpec { Id = "contacts", Label = "Contacts", Load = async (c, _) => { using var d = await c.GetAsync(XeroApi + "Contacts?page=1");
                return J.Items(d.RootElement, "Contacts", x => new AppItem(J.S(x, "ContactID") ?? "", J.S(x, "Name") ?? "Contact", J.S(x, "EmailAddress")), 50); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "contact", Label = "Create a contact", Inputs = [F("name", "Name"), F("email", "Email", required: false)],
                Run = async (c, i) => { using var _ = await c.PostAsync(XeroApi + "Contacts", new { Name = i["name"], EmailAddress = i["email"] }); return new ActionResult("Contact created."); } },
        ],
    };

    // ============================================================================================
    // Zoho Books (one host per country: ZohoBooks:Region)
    // ============================================================================================
    private static string ZbBase(ProviderCtx c) => $"https://www.zohoapis.{(c.Server("ZohoBooks:Region") is { Length: > 0 } r && Regex.IsMatch(r, "^[a-z.]{2,12}$") ? r : "com")}/books/v3/";
    private static string ZbOrg(ProviderCtx c) => $"organization_id={J.E(c.Account.AccountId)}";

    private static ProviderApp ZohoBooks() => new()
    {
        Key = "zoho-books", Name = "Zoho Books", Icon = "SiZoho", Color = "#E42527", Account = "Invoices, bills and customers",
        ConnectTitle = "Connect Zoho Books", ConnectLabel = "Continue with Zoho",
        ConnectBlurb = "Sign in with Zoho and Daksha can show invoices, bills and customers.",
        WriteLabel = "Allow Daksh to create customers", WriteHint = "Full access lets Daksha create customers.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#zoho-books",
        Ask = Rx("zoho books|invoices?|unpaid|bills?|accounting"),
        NoBearer = true, Decorate = (r, c) => { if (c.Token is not null) r.Headers.TryAddWithoutValidation("Authorization", "Zoho-oauthtoken " + c.Token); },
        Setup = ["Open the Zoho API Console for your data centre and create a **Server-based Application**. Copy the client id and secret.", "Set `ZohoBooks__Region` to your data centre suffix (`in`, `com`, `eu`, `com.au`, `jp`)."],
        OAuth = new()
        {
            Section = "ZohoBooks", AuthUrl = "https://accounts.zoho.{region}/oauth/v2/auth", TokenUrl = "https://accounts.zoho.{region}/oauth/v2/token", ScopeSeparator = ",", DefaultRegion = "com",
            ReadScopes = ["ZohoBooks.invoices.READ", "ZohoBooks.contacts.READ", "ZohoBooks.bills.READ", "ZohoBooks.settings.READ"], WriteScopes = ["ZohoBooks.contacts.CREATE"], AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent" },
        },
        Profile = async c => { using var d = await c.GetAsync(ZbBase(c) + "organizations"); var o = J.Arr(d.RootElement, "organizations").FirstOrDefault(); if (o.ValueKind != JsonValueKind.Object) throw new AppException(400, "No Zoho Books organization was found.");
            return new ProfileInfo(J.S(o, "organization_id") ?? "", J.S(o, "name") ?? "Zoho Books"); },
        Views =
        [
            new ViewSpec { Id = "invoices", Label = "Recent invoices", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(ZbBase(c) + $"invoices?{ZbOrg(c)}&sort_column=date&sort_order=D&per_page=25");
                return J.Items(d.RootElement, "invoices", i => new AppItem(J.S(i, "invoice_id") ?? "", $"{J.S(i, "invoice_number")} · {J.S(i, "customer_name")}", $"{Money2(J.S(i, "total"), J.S(i, "currency_code"))} · due {J.S(i, "due_date")}", null, J.S(i, "status"))); } },
            new ViewSpec { Id = "bills", Label = "Bills", Load = async (c, _) => { using var d = await c.GetAsync(ZbBase(c) + $"bills?{ZbOrg(c)}&per_page=25");
                return J.Items(d.RootElement, "bills", b => new AppItem(J.S(b, "bill_id") ?? "", $"{J.S(b, "bill_number")} · {J.S(b, "vendor_name")}", $"{Money2(J.S(b, "total"), J.S(b, "currency_code"))} · due {J.S(b, "due_date")}", null, J.S(b, "status"))); } },
            new ViewSpec { Id = "customers", Label = "Customers", Load = async (c, _) => { using var d = await c.GetAsync(ZbBase(c) + $"contacts?{ZbOrg(c)}&contact_type=customer&per_page=50");
                return J.Items(d.RootElement, "contacts", x => new AppItem(J.S(x, "contact_id") ?? "", J.S(x, "contact_name") ?? "Customer", J.S(x, "email"), null, J.S(x, "outstanding_receivable_amount") is { } o && o != "0" ? $"owes {o}" : null)); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "customer", Label = "Create a customer", Inputs = [F("name", "Name")],
                Run = async (c, i) => { using var _ = await c.PostAsync(ZbBase(c) + $"contacts?{ZbOrg(c)}", new { contact_name = i["name"], contact_type = "customer" }); return new ActionResult("Customer created."); } },
        ],
    };

    // ============================================================================================
    // Mapbox (one server token)
    // ============================================================================================
    private static async Task<(string Name, double Lon, double Lat)> MapboxGeocodeAsync(ProviderCtx c, string text)
    {
        using var d = await c.GetAsync($"https://api.mapbox.com/search/geocode/v6/forward?q={J.E(text)}&limit=1&access_token={J.E(c.Server("Mapbox:AccessToken"))}");
        var f = J.Arr(d.RootElement, "features").FirstOrDefault();
        if (f.ValueKind != JsonValueKind.Object) throw new AppException(404, $"Mapbox could not find “{J.Clip(text, 40)}”.");
        var coords = J.At(f, "geometry.coordinates");
        return (J.S(f, "properties.full_address") ?? J.S(f, "properties.name") ?? text, coords[0].GetDouble(), coords[1].GetDouble());
    }

    private static async Task<IReadOnlyList<AppItem>> MapboxRouteAsync(ProviderCtx c, string from, string to, string profile)
    {
        var (a, b) = (await MapboxGeocodeAsync(c, from), await MapboxGeocodeAsync(c, to));
        string N(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
        using var d = await c.GetAsync($"https://api.mapbox.com/directions/v5/mapbox/{profile}/{N(a.Lon)},{N(a.Lat)};{N(b.Lon)},{N(b.Lat)}?overview=false&access_token={J.E(c.Server("Mapbox:AccessToken"))}");
        return J.Items(d.RootElement, "routes", r => new AppItem($"{from}>{to}", $"{Math.Max(1, (int)Math.Round((double.TryParse(J.S(r, "duration"), CultureInfo.InvariantCulture, out var s) ? s : 0) / 60))} min · {((double.TryParse(J.S(r, "distance"), CultureInfo.InvariantCulture, out var m) ? m : 0) / 1000).ToString("0.#", CultureInfo.InvariantCulture)} km",
            $"{a.Name} → {b.Name}", $"https://www.google.com/maps/dir/?api=1&origin={J.E(from)}&destination={J.E(to)}", profile));
    }

    private static ProviderApp Mapbox() => new()
    {
        Key = "mapbox", Name = "Mapbox", Icon = "SiMapbox", Color = "#000000", Account = "Places, directions and commute",
        ConnectTitle = "Turn on Mapbox", ConnectLabel = "Turn on Mapbox",
        ConnectBlurb = "Search for places, get directions and see your commute. Daksha uses its own Mapbox token, so there is nothing to sign in to.",
        WriteLabel = "Full access", WriteHint = "Mapbox is read-only.", Auth = AuthKind.ServerKey, HasWrite = false, SetupDoc = "Amit.md#mapbox",
        ServerKeys = ["Mapbox:AccessToken"], Ask = Rx("mapbox|directions?|route|commute|travel time|how long"),
        ExtraSettings = [new("home", "Home address", "text", "Used for your commute tile.", "Bandra West, Mumbai"), new("work", "Work address", "text", "Used for your commute tile.", "Nariman Point, Mumbai")],
        Setup = ["account.mapbox.com → Tokens → **Create a token** with the default public scopes. Set it as `Mapbox__AccessToken`. Restrict it to your server's URL if you can."],
        Notes = "Mapbox only accepts the token in the address of each request, so avoid logging full request URLs.",
        Views =
        [
            new ViewSpec { Id = "commute", Label = "Commute", Dashboard = true, Empty = "Add your home and work addresses in Manage to see your commute.", Load = async (c, _) => { var (home, work) = (c.Setting("home").Trim(), c.Setting("work").Trim()); if (home.Length == 0 || work.Length == 0) return [];
                var there = await MapboxRouteAsync(c, home, work, "driving-traffic"); return there.Select(r => r with { Id = "to-work", Badge = "now" }).ToList(); } },
            new ViewSpec { Id = "places", Label = "Find places", Inputs = [F("q", "What are you looking for?", placeholder: "Indiranagar, Bengaluru")], Load = async (c, i) => { using var d = await c.GetAsync($"https://api.mapbox.com/search/geocode/v6/forward?q={J.E(i["q"])}&limit=8&access_token={J.E(c.Server("Mapbox:AccessToken"))}");
                return J.Items(d.RootElement, "features", f => new AppItem(J.S(f, "id") ?? "", J.S(f, "properties.name") ?? "Place", J.S(f, "properties.full_address") ?? J.S(f, "properties.place_formatted"), $"https://www.google.com/maps/search/?api=1&query={J.E(J.S(f, "properties.full_address") ?? J.S(f, "properties.name"))}", J.S(f, "properties.feature_type"))); } },
            new ViewSpec { Id = "directions", Label = "Directions", Inputs = [F("from", "From"), F("to", "To"), new("mode", "Travel by", "select", null, true, ["driving-traffic", "driving", "walking", "cycling"], null, "driving-traffic")], Load = (c, i) => MapboxRouteAsync(c, i["from"], i["to"], i["mode"]) },
        ],
    };

    // ============================================================================================
    // Amadeus (self-service flight and hotel search)
    // ============================================================================================
    private static string AmBase(ProviderCtx c) => c.Cred.GetValueOrDefault("env") == "production" ? "https://api.amadeus.com" : "https://test.api.amadeus.com";

    private static async Task AmLoginAsync(ProviderCtx c)
    {
        using var d = await c.PostFormAsync(AmBase(c) + "/v1/security/oauth2/token", new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = c.Cred["clientId"], ["client_secret"] = c.Cred["secret"] });
        c.Token = J.S(d.RootElement, "access_token") ?? throw new AppException(409, "Amadeus did not accept those credentials.", "reconnect_required");
    }

    private static string Duration(string? iso) => Regex.Match(iso ?? "", @"PT(?:(\d+)H)?(?:(\d+)M)?") is { Success: true } m ? $"{(m.Groups[1].Success ? m.Groups[1].Value + "h " : "")}{(m.Groups[2].Success ? m.Groups[2].Value + "m" : "")}".Trim() : "";

    private static ProviderApp Amadeus() => new()
    {
        Key = "amadeus", Name = "Amadeus", Icon = "mono:AM", Color = "#003087", Account = "Flight and hotel search",
        ConnectTitle = "Connect Amadeus", ConnectLabel = "Connect Amadeus",
        ConnectBlurb = "Create a free Amadeus for Developers app and enter its API key and secret. Daksha searches flights and hotels. Booking happens with the airline or hotel.",
        WriteLabel = "Full access", WriteHint = "Daksha only searches.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#amadeus",
        Ask = Rx("flights?|fly|airfare|hotels?|amadeus"),
        Setup = ["Nothing to set on the server. Each person registers at developers.amadeus.com, creates an app (Self-Service) and enters its **API key** (client id) and **secret**. Use Test to start; switch to Production once approved."],
        Notes = "The Test environment returns cached sample data.",
        Credentials = new()
        {
            Fields = [F("clientId", "API key"), F("secret", "API secret", "secret"), new("env", "Environment", "select", null, true, ["test", "production"], null, "test")],
            Probe = async c => { await AmLoginAsync(c); return new ProfileInfo($"{c.Cred["env"]}-{c.Cred["clientId"][..Math.Min(8, c.Cred["clientId"].Length)]}", $"Amadeus ({c.Cred["env"]})"); },
        },
        Views =
        [
            new ViewSpec { Id = "start", Label = "Search", Dashboard = true, Load = (_, _) => Task.FromResult<IReadOnlyList<AppItem>>([new AppItem("flights", "Search flights or hotels", "Use the other tabs to look up airports, flights and hotels.")]) },
            new ViewSpec { Id = "places", Label = "Find an airport or city", Inputs = [F("q", "City or airport", placeholder: "Mumbai")], Load = async (c, i) => { await AmLoginAsync(c);
                using var d = await c.GetAsync(AmBase(c) + $"/v1/reference-data/locations?subType=CITY,AIRPORT&keyword={J.E(i["q"])}&page%5Blimit%5D=10"); return J.Items(d.RootElement, "data", l => new AppItem(J.S(l, "id") ?? "", $"{J.S(l, "name")} ({J.S(l, "iataCode")})", J.S(l, "address.countryName"), null, J.S(l, "subType"))); } },
            new ViewSpec { Id = "flights", Label = "Flights", Inputs = [F("from", "From (airport code)", placeholder: "BOM"), F("to", "To (airport code)", placeholder: "DEL"), F("date", "Date", "date"), F("adults", "Adults", "number", def: "1")], Load = async (c, i) =>
                { if (!Regex.IsMatch(i["from"], "^[A-Za-z]{3}$") || !Regex.IsMatch(i["to"], "^[A-Za-z]{3}$")) throw new AppException(400, "Airport codes have three letters, like BOM."); var adults = int.TryParse(i["adults"], out var a) ? Math.Clamp(a, 1, 9) : 1; await AmLoginAsync(c);
                  using var d = await c.GetAsync(AmBase(c) + $"/v2/shopping/flight-offers?originLocationCode={i["from"].ToUpperInvariant()}&destinationLocationCode={i["to"].ToUpperInvariant()}&departureDate={J.E(i["date"])}&adults={adults}&max=10&currencyCode=INR");
                  return J.Items(d.RootElement, "data", o => new AppItem(J.S(o, "id") ?? "", $"{Money2(J.S(o, "price.grandTotal"), J.S(o, "price.currency"))} · {J.S(o, "validatingAirlineCodes[0]")}", $"{Duration(J.S(o, "itineraries[0].duration"))} · {J.Arr(o, "itineraries[0].segments").Count()} flight(s) · {J.S(o, "itineraries[0].segments[0].departure.at")?.Replace('T', ' ')[..16]}")); } },
            new ViewSpec { Id = "hotels", Label = "Hotels in a city", Inputs = [F("city", "City code", placeholder: "BOM")], Load = async (c, i) =>
                { if (!Regex.IsMatch(i["city"], "^[A-Za-z]{3}$")) throw new AppException(400, "City codes have three letters, like BOM."); await AmLoginAsync(c);
                  using var d = await c.GetAsync(AmBase(c) + $"/v1/reference-data/locations/hotels/by-city?cityCode={i["city"].ToUpperInvariant()}&radius=10"); return J.Items(d.RootElement, "data", h => new AppItem(J.S(h, "hotelId") ?? "", J.S(h, "name") ?? "Hotel", $"{J.S(h, "distance.value")} {J.S(h, "distance.unit")} from the centre"), 30); } },
        ],
    };

    // ============================================================================================
    // Mastodon (any instance, with an access token the person creates)
    // ============================================================================================
    private static ProviderApp Mastodon() => new()
    {
        Key = "mastodon", Name = "Mastodon", Icon = "SiMastodon", Color = "#6364FF", Account = "Timeline, notifications and posts",
        ConnectTitle = "Connect Mastodon", ConnectLabel = "Connect Mastodon",
        ConnectBlurb = "Enter your instance (for example mastodon.social) and an access token (Preferences → Development → New application).",
        WriteLabel = "Allow Daksh to post", WriteHint = "Full access lets Daksha publish posts (the token needs the write:statuses scope).",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#mastodon",
        Ask = Rx("mastodon|toots?|fediverse"),
        Setup = ["Nothing to set on the server. Each person opens their instance → Preferences → **Development → New application**, grants `read` (and `write:statuses` for posting), and copies **your access token**.", "In Daksha they enter the instance address and the token."],
        Credentials = new()
        {
            Fields = [F("instance", "Instance", placeholder: "mastodon.social"), F("token", "Access token", "secret")],
            Prepare = async (v, ct) => { var d = new Dictionary<string, string> { ["u"] = v["instance"] }; await PrepareUrl("u", allowHttp: false)(d, ct); v["instance"] = d["u"]; },
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["instance"] + "/api/v1/accounts/verify_credentials"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", "@" + (J.S(d.RootElement, "acct") ?? "me") + "@" + new Uri(c.Cred["instance"]).Host); },
        },
        Views =
        [
            new ViewSpec { Id = "home", Label = "Home", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["instance"] + "/api/v1/timelines/home?limit=25"); return MastoStatuses(d.RootElement); } },
            new ViewSpec { Id = "notifications", Label = "Notifications", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["instance"] + "/api/v1/notifications?limit=25");
                return J.Items(d.RootElement, "", n => new AppItem(J.S(n, "id") ?? "", $"@{J.S(n, "account.acct")} {J.S(n, "type")?.Replace('_', ' ')}", J.Clip(StripHtml(J.S(n, "status.content")), 120), J.S(n, "status.url"), J.When(J.S(n, "created_at")))); } },
            new ViewSpec { Id = "bookmarks", Label = "Bookmarks", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["instance"] + "/api/v1/bookmarks?limit=25"); return MastoStatuses(d.RootElement); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search posts and people")], Load = async (c, i) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(c.Cred["instance"] + $"/api/v2/search?q={J.E(i["q"])}&limit=10"); return MastoStatuses(J.At(d.RootElement, "statuses")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Publish a post", Inputs = [F("text", "What is on your mind?", "textarea"), new("visibility", "Who can see it", "select", null, true, ["public", "unlisted", "private", "direct"], null, "unlisted")],
                Run = async (c, i) => { c.Token = c.Cred["token"]; using var d = await c.PostAsync(c.Cred["instance"] + "/api/v1/statuses", new { status = i["text"], visibility = i["visibility"] }); return new ActionResult("Posted.", J.S(d.RootElement, "url")); } },
        ],
    };

    private static IReadOnlyList<AppItem> MastoStatuses(JsonElement root) => J.Items(root, "", s => new AppItem(J.S(s, "id") ?? "", J.Clip(StripHtml(J.S(s, "content")), 140) is { Length: > 0 } t ? t : "(media)", $"@{J.S(s, "account.acct")} · {J.When(J.S(s, "created_at"))} · {J.S(s, "favourites_count") ?? "0"} ★", J.S(s, "url")));

    // ============================================================================================
    // Bluesky (handle and app password)
    // ============================================================================================
    private static async Task<string> BskyLoginAsync(ProviderCtx c)
    {
        using var d = await c.PostAsync(c.Cred["pds"] + "/xrpc/com.atproto.server.createSession", new { identifier = c.Cred["handle"], password = c.Cred["password"] });
        c.Token = J.S(d.RootElement, "accessJwt") ?? throw new AppException(409, "Bluesky did not accept that handle and app password. Reconnect Bluesky.", "reconnect_required");
        return J.S(d.RootElement, "did") ?? "";
    }

    private static IReadOnlyList<AppItem> BskyPosts(JsonElement root, string path) => J.Items(root, path, f => { var p = J.At(f, "post"); var rkey = (J.S(p, "uri") ?? "").Split('/').Last();
        return new AppItem(J.S(p, "uri") ?? "", J.Clip(J.S(p, "record.text"), 140) is { Length: > 0 } t ? t : "(media)", $"@{J.S(p, "author.handle")} · {J.When(J.S(p, "indexedAt"))} · {J.S(p, "likeCount") ?? "0"} likes", $"https://bsky.app/profile/{J.S(p, "author.handle")}/post/{rkey}"); });

    private static ProviderApp Bluesky() => new()
    {
        Key = "bluesky", Name = "Bluesky", Icon = "SiBluesky", Color = "#0285FF", Account = "Timeline, notifications and posts",
        ConnectTitle = "Connect Bluesky", ConnectLabel = "Connect Bluesky",
        ConnectBlurb = "Create an app password (Settings → Privacy and security → App passwords) and enter it with your handle. Never use your main password.",
        WriteLabel = "Allow Daksh to post", WriteHint = "Full access lets Daksha publish posts.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#bluesky",
        Ask = Rx("bluesky|bsky|skeets?"),
        Setup = ["Nothing to set on the server. Each person creates an **app password** in Bluesky (Settings → Privacy and security → App passwords) and enters it with their handle (for example `you.bsky.social`)."],
        Credentials = new()
        {
            Fields = [F("handle", "Handle", placeholder: "you.bsky.social"), F("password", "App password", "secret", placeholder: "xxxx-xxxx-xxxx-xxxx"), F("pds", "Server", "url", "https://bsky.social", required: false, def: "https://bsky.social")],
            Prepare = async (v, ct) => { if (v["pds"].Length == 0) v["pds"] = "https://bsky.social"; await PrepareUrl("pds", allowHttp: false)(v, ct); v["handle"] = v["handle"].TrimStart('@'); },
            Probe = async c => { var did = await BskyLoginAsync(c); return new ProfileInfo(did, "@" + c.Cred["handle"]); },
        },
        Views =
        [
            new ViewSpec { Id = "timeline", Label = "Timeline", Dashboard = true, Load = async (c, _) => { await BskyLoginAsync(c); using var d = await c.GetAsync(c.Cred["pds"] + "/xrpc/app.bsky.feed.getTimeline?limit=25"); return BskyPosts(d.RootElement, "feed"); } },
            new ViewSpec { Id = "notifications", Label = "Notifications", Dashboard = true, Load = async (c, _) => { await BskyLoginAsync(c); using var d = await c.GetAsync(c.Cred["pds"] + "/xrpc/app.bsky.notification.listNotifications?limit=25");
                return J.Items(d.RootElement, "notifications", n => new AppItem(J.S(n, "uri") ?? "", $"@{J.S(n, "author.handle")} {J.S(n, "reason")}", J.Clip(J.S(n, "record.text"), 120) is { Length: > 0 } t ? t : J.When(J.S(n, "indexedAt")), null, J.S(n, "isRead") == "false" ? "new" : null)); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search posts")], Load = async (c, i) => { await BskyLoginAsync(c); using var d = await c.GetAsync(c.Cred["pds"] + $"/xrpc/app.bsky.feed.searchPosts?q={J.E(i["q"])}&limit=20");
                return J.Items(d.RootElement, "posts", p => new AppItem(J.S(p, "uri") ?? "", J.Clip(J.S(p, "record.text"), 140), $"@{J.S(p, "author.handle")} · {J.When(J.S(p, "indexedAt"))}", $"https://bsky.app/profile/{J.S(p, "author.handle")}/post/{(J.S(p, "uri") ?? "").Split('/').Last()}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "post", Label = "Publish a post", Inputs = [F("text", "What is on your mind?", "textarea")],
                Run = async (c, i) => { if (i["text"].Length > 300) throw new AppException(400, "Bluesky posts can be at most 300 characters."); var did = await BskyLoginAsync(c);
                    using var d = await c.PostAsync(c.Cred["pds"] + "/xrpc/com.atproto.repo.createRecord", new { repo = did, collection = "app.bsky.feed.post", record = new Dictionary<string, object> { ["$type"] = "app.bsky.feed.post", ["text"] = i["text"], ["createdAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) } });
                    return new ActionResult("Posted.", $"https://bsky.app/profile/{c.Cred["handle"]}/post/{(J.S(d.RootElement, "uri") ?? "").Split('/').Last()}"); } },
        ],
    };

    // ============================================================================================
    // Webhook-style automation: IFTTT, Zapier and Make (the person's own trigger addresses; Daksha sends to them)
    // ============================================================================================
    private static IEnumerable<(string Name, string Url)> Hooks(ProviderCtx c)
    {
        if (c.Cred.GetValueOrDefault("url") is { Length: > 0 } u) yield return (c.Cred.GetValueOrDefault("name") is { Length: > 0 } n ? n : "Main", u);
        foreach (var line in c.Setting("hooks").Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && Uri.TryCreate(line[(eq + 1)..].Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) yield return (line[..eq].Trim(), uri.ToString());
        }
    }

    private static ProviderApp WebhookApp(string key, string name, string icon, string color, Regex hostPattern, string hostText, string[] setup) => new()
    {
        Key = key, Name = name, Icon = icon, Color = color, Account = "Triggers you can start from Daksha",
        ConnectTitle = $"Connect {name}", ConnectLabel = $"Connect {name}",
        ConnectBlurb = $"Add a {name} webhook address (a trigger that starts one of your automations). Daksha can start it with a message you choose. More triggers can be added in Manage → Preferences.",
        WriteLabel = "Allow Daksh to start automations", WriteHint = $"Full access lets Daksha send a message to your {name} triggers.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = $"Amit.md#{name.ToLowerInvariant()}",
        Ask = Rx(name.ToLowerInvariant() + "|automations?|triggers?|webhooks?"),
        ExtraSettings = [new("hooks", "More triggers", "text", "One per line as name=address (or separated by semicolons).", "Daily report=https://…", null, "", 2000)],
        Setup = setup,
        Notes = $"Only addresses on {hostText} are accepted. Daksha cannot read results back from {name}; it only starts the trigger.",
        Credentials = new()
        {
            Fields = [F("name", "Name", placeholder: "Daily report", def: "Main"), F("url", "Webhook address", "url", hostText.Split(',')[0])],
            Prepare = (v, _) => Uri.TryCreate(v["url"].Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && hostPattern.IsMatch(u.Host) ? Task.CompletedTask : throw new AppException(400, $"Use a webhook address on {hostText}."),
            Probe = c => Task.FromResult(new ProfileInfo(key, $"{name} triggers")),
        },
        Views =
        [
            new ViewSpec { Id = "hooks", Label = "Triggers", Dashboard = true, Empty = "No triggers yet. Add one in Manage → Preferences.", Load = (c, _) => Task.FromResult<IReadOnlyList<AppItem>>(Hooks(c).Select(h => new AppItem(h.Name, h.Name, new Uri(h.Url).Host)).ToList()) },
        ],
        Actions =
        [
            new ActionSpec { Id = "trigger", Label = "Start a trigger", Inputs = [new("hook", "Trigger", "select-view", null, true, null, "hooks"), F("message", "Message", "textarea", required: false)],
                Run = async (c, i) =>
                {
                    var hook = Hooks(c).FirstOrDefault(h => h.Name == i["hook"]);
                    if (hook.Url is null || !hostPattern.IsMatch(new Uri(hook.Url).Host)) throw new AppException(400, "Choose one of your triggers.");
                    await c.RawAsync(HttpMethod.Post, hook.Url, JsonSerializer.Serialize(new { text = i["message"], source = "daksha", sentAt = DateTime.UtcNow }), "application/json");
                    return new ActionResult("Trigger started.");
                } },
        ],
    };

    private static ProviderApp Zapier() => WebhookApp("zapier", "Zapier", "SiZapier", "#FF4F00", new(@"^hooks\.zapier\.com$", RegexOptions.IgnoreCase), "hooks.zapier.com",
        ["Nothing to set on the server. In Zapier create a Zap that starts with **Webhooks by Zapier → Catch Hook** and copy its address (`https://hooks.zapier.com/hooks/catch/…`).", "In Daksha enter a name and that address. Add more Zaps later in Manage → Preferences."]);

    private static ProviderApp Make() => WebhookApp("make", "Make", "SiMake", "#6D00CC", new(@"^hook\.[a-z0-9\-]+\.make\.(com|celonis\.com)$|^hook\.integromat\.com$", RegexOptions.IgnoreCase), "hook.eu1.make.com, hook.us1.make.com, …",
        ["Nothing to set on the server. In Make create a scenario that starts with **Webhooks → Custom webhook** and copy its address (`https://hook.eu1.make.com/…`).", "In Daksha enter a name and that address. Add more scenarios later in Manage → Preferences."]);

    private static ProviderApp Ifttt() => new()
    {
        Key = "ifttt", Name = "IFTTT", Icon = "SiIfttt", Color = "#000000", Account = "Webhook events you can start from Daksha",
        ConnectTitle = "Connect IFTTT", ConnectLabel = "Connect IFTTT",
        ConnectBlurb = "Enter your IFTTT Webhooks key (ifttt.com/maker_webhooks → Documentation). Daksha can fire any event name you use in your applets.",
        WriteLabel = "Allow Daksh to fire events", WriteHint = "Full access lets Daksha trigger IFTTT events.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#ifttt",
        Ask = Rx("ifttt|applets?|automations?"),
        ExtraSettings = [new("events", "My event names", "text", "Event names used in your applets, separated by commas.", "door_open, send_report", null, "", 600)],
        Setup = ["Nothing to set on the server. Each person turns on the **Webhooks** service in IFTTT (ifttt.com/maker_webhooks) and copies their key from Documentation."],
        Notes = "IFTTT cannot be read back from; Daksha only fires events. Connecting fires one harmless test event named `daksha_connected`.",
        Credentials = new()
        {
            Fields = [F("key", "Webhooks key", "secret")],
            Prepare = (v, _) => Regex.IsMatch(v["key"], @"^[A-Za-z0-9_\-]{10,60}$") ? Task.CompletedTask : throw new AppException(400, "A Webhooks key is letters and numbers only."),
            Probe = async c => { await c.RawAsync(HttpMethod.Post, $"https://maker.ifttt.com/trigger/daksha_connected/with/key/{c.Cred["key"]}", "{}", "application/json"); return new ProfileInfo(c.Cred["key"][..6], "IFTTT Webhooks"); },
        },
        Views =
        [
            new ViewSpec { Id = "events", Label = "My events", Dashboard = true, Empty = "Add your event names in Manage → Preferences.", Load = (c, _) => Task.FromResult<IReadOnlyList<AppItem>>(c.Setting("events").Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(e => Regex.IsMatch(e, @"^[\w\-]{1,60}$")).Select(e => new AppItem(e, e, "Webhook event")).ToList()) },
        ],
        Actions =
        [
            new ActionSpec { Id = "fire", Label = "Fire an event", Inputs = [F("event", "Event name", placeholder: "send_report"), F("value1", "Value 1", required: false), F("value2", "Value 2", required: false), F("value3", "Value 3", required: false)],
                Run = async (c, i) => { if (!Regex.IsMatch(i["event"], @"^[\w\-]{1,60}$")) throw new AppException(400, "An event name is letters, numbers, dashes and underscores."); await c.RawAsync(HttpMethod.Post, $"https://maker.ifttt.com/trigger/{i["event"]}/with/key/{c.Cred["key"]}", JsonSerializer.Serialize(new { value1 = i["value1"], value2 = i["value2"], value3 = i["value3"] }), "application/json"); return new ActionResult($"Fired {i["event"]}."); } },
        ],
    };

    // ============================================================================================
    // Philips Hue (the local bridge; Daksha can pair when you press the link button)
    // ============================================================================================
    private static string HueBase(ProviderCtx c) => $"http://{c.Cred["bridge"]}/api/{c.Cred["appKey"]}/";

    private static void HueCheck(JsonDocument d)
    {
        if (d.RootElement.ValueKind != JsonValueKind.Array) return;
        var err = d.RootElement.EnumerateArray().Select(e => J.S(e, "error.description")).FirstOrDefault(x => x is not null);
        if (err is not null) throw new AppException(400, $"The Hue bridge said: {err}");
    }

    private static ProviderApp Hue() => new()
    {
        Key = "philips-hue", Name = "Philips Hue", Icon = "SiPhilipshue", Color = "#0065D3", Account = "Lights, rooms and scenes",
        ConnectTitle = "Connect your Hue bridge", ConnectLabel = "Connect bridge",
        ConnectBlurb = "Enter your bridge's address. Press the round link button on top of the bridge first, then connect: Daksha pairs itself. The server running Daksha must be on the same network as the bridge (or reach it through a tunnel).",
        WriteLabel = "Allow Daksh to control lights", WriteHint = "Full access lets Daksha switch lights on and off, set brightness and recall scenes.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#philips-hue",
        Ask = Rx("hue|lights?|lamps?|bulbs?|scenes?|rooms?"),
        Setup = ["Nothing to set on the server. Find the bridge address in the Hue app (Settings → My Hue system → bridge) or at discovery.meethue.com.", "In Daksha press the **link button** on the bridge, then enter the address and connect. Daksha receives its own key from the bridge (you can also paste an existing key)."],
        Notes = "Uses the bridge's local API over plain HTTP, so it must run on a trusted network.",
        Credentials = new()
        {
            Fields = [F("bridge", "Bridge address", placeholder: "192.168.1.20"), F("appKey", "Application key", "secret", required: false, hint: "Leave empty to pair: press the link button on the bridge first.")],
            Prepare = async (v, ct) =>
            {
                var host = Regex.Replace(v["bridge"].Trim(), "^https?://", "").TrimEnd('/'); if (!Regex.IsMatch(host, @"^[A-Za-z0-9.\-]{1,100}(:\d{2,5})?$")) throw new AppException(400, "Enter the bridge address, like 192.168.1.20.");
                await HostGuard.CheckHostAsync(host.Split(':')[0], ct); v["bridge"] = host;
                if (v["appKey"].Length > 0) return;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                try
                {
                    using var res = await http.PostAsync($"http://{host}/api", new StringContent("{\"devicetype\":\"daksha#server\"}", Encoding.UTF8, "application/json"), ct);
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    var first = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default;
                    if (J.S(first, "success.username") is { Length: > 0 } key) { v["appKey"] = key; return; }
                    throw new AppException(400, J.S(first, "error.type") == "101" ? "Press the round link button on the Hue bridge, then connect again within 30 seconds." : $"The Hue bridge said: {J.S(first, "error.description")}");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { throw new AppException(502, "Daksha could not reach the Hue bridge at that address."); }
            },
            Probe = async c => { using var d = await c.GetAsync(HueBase(c) + "config"); HueCheck(d); return new ProfileInfo(J.S(d.RootElement, "bridgeid") ?? c.Cred["bridge"], J.S(d.RootElement, "name") ?? "Hue bridge"); },
        },
        Views =
        [
            new ViewSpec { Id = "lights", Label = "Lights", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(HueBase(c) + "lights"); HueCheck(d);
                return Props(d.RootElement).Select(p => new AppItem(p.Name, J.S(p.Value, "name") ?? $"Light {p.Name}", J.S(p.Value, "state.reachable") == "false" ? "Not reachable" : J.S(p.Value, "state.bri") is { } b ? $"Brightness {(int.TryParse(b, out var n) ? n * 100 / 254 : 0)}%" : null, null, J.S(p.Value, "state.on") == "true" ? "on" : "off")).ToList(); } },
            new ViewSpec { Id = "rooms", Label = "Rooms", Load = async (c, _) => { using var d = await c.GetAsync(HueBase(c) + "groups"); HueCheck(d);
                return Props(d.RootElement).Select(p => new AppItem(p.Name, J.S(p.Value, "name") ?? $"Group {p.Name}", $"{J.Arr(p.Value, "lights").Count()} lights", null, J.S(p.Value, "state.any_on") == "true" ? "on" : "off")).ToList(); } },
            new ViewSpec { Id = "scenes", Label = "Scenes", Load = async (c, _) => { using var d = await c.GetAsync(HueBase(c) + "scenes"); HueCheck(d); return Props(d.RootElement).Select(p => new AppItem(p.Name, J.S(p.Value, "name") ?? p.Name, $"{J.Arr(p.Value, "lights").Count()} lights")).Take(100).ToList(); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "light", Label = "Control a light", Inputs = [new("light", "Light", "select-view", null, true, null, "lights"), new("power", "Power", "select", null, true, ["on", "off"], null, "on"), F("brightness", "Brightness (1–100)", "number", required: false)],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["light"])) throw new AppException(400, "Choose a light.");
                    var state = new Dictionary<string, object> { ["on"] = i["power"] == "on" }; if (i["brightness"].Length > 0) { if (!int.TryParse(i["brightness"], out var b) || b is < 1 or > 100) throw new AppException(400, "Brightness is 1 to 100."); state["bri"] = Math.Max(1, b * 254 / 100); }
                    using var d = await c.JsonAsync(HttpMethod.Put, HueBase(c) + $"lights/{i["light"]}/state", state); HueCheck(d); return new ActionResult("Done."); } },
            new ActionSpec { Id = "scene", Label = "Recall a scene", Inputs = [new("scene", "Scene", "select-view", null, true, null, "scenes")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["scene"], @"^[\w\-]{1,40}$")) throw new AppException(400, "Choose a scene."); using var d = await c.JsonAsync(HttpMethod.Put, HueBase(c) + "groups/0/action", new { scene = i["scene"] }); HueCheck(d); return new ActionResult("Scene on."); } },
        ],
    };

    // ============================================================================================
    // SmartThings (a personal access token)
    // ============================================================================================
    private const string StApiBase = "https://api.smartthings.com/v1/";

    private static ProviderApp SmartThings() => new()
    {
        Key = "smartthings", Name = "SmartThings", Icon = "SiSmartthings", Color = "#15BFFF", Account = "Devices and scenes",
        ConnectTitle = "Connect SmartThings", ConnectLabel = "Connect SmartThings",
        ConnectBlurb = "Create a personal access token (account.smartthings.com/tokens) and paste it here.",
        WriteLabel = "Allow Daksh to control devices", WriteHint = "Full access lets Daksha switch devices and run scenes (the token needs those permissions).",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#smartthings",
        Ask = Rx("smartthings|devices?|lights?|tv|scenes?|smart home"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** at account.smartthings.com/tokens with the Devices (list, read, execute) and Scenes (list, execute) permissions."],
        Credentials = new()
        {
            Fields = [F("token", "Personal access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(StApiBase + "locations"); var l = J.Arr(d.RootElement, "items").FirstOrDefault(); return new ProfileInfo(J.S(l, "locationId") ?? "smartthings", J.S(l, "name") ?? "SmartThings"); },
        },
        Views =
        [
            new ViewSpec { Id = "devices", Label = "Devices", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(StApiBase + "devices");
                return J.Items(d.RootElement, "items", x => new AppItem(J.S(x, "deviceId") ?? "", J.First(x, "label", "name") ?? "Device", J.S(x, "deviceManufacturerCode") ?? J.S(x, "type"))); } },
            new ViewSpec { Id = "scenes", Label = "Scenes", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(StApiBase + "scenes"); return J.Items(d.RootElement, "items", x => new AppItem(J.S(x, "sceneId") ?? "", J.S(x, "sceneName") ?? "Scene", null)); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "command", Label = "Switch a device", Inputs = [new("device", "Device", "select-view", null, true, null, "devices"), new("command", "Do", "select", null, true, ["on", "off"], null, "on")],
                Run = async (c, i) => { if (!Guid36.IsMatch(i["device"])) throw new AppException(400, "Choose a device."); c.Token = c.Cred["token"]; using var _ = await c.PostAsync(StApiBase + $"devices/{i["device"]}/commands", new { commands = new[] { new { component = "main", capability = "switch", command = i["command"] } } }); return new ActionResult("Done."); } },
            new ActionSpec { Id = "scene", Label = "Run a scene", Inputs = [new("scene", "Scene", "select-view", null, true, null, "scenes")],
                Run = async (c, i) => { if (!Guid36.IsMatch(i["scene"])) throw new AppException(400, "Choose a scene."); c.Token = c.Cred["token"]; using var _ = await c.PostAsync(StApiBase + $"scenes/{i["scene"]}/execute"); return new ActionResult("Scene started."); } },
        ],
    };

    // ============================================================================================
    // Tuya (a cloud project: client id, secret and the data centre for your region)
    // ============================================================================================
    /// <summary>The Tuya request signature: HMAC-SHA256 over client id, token, time, nonce and the canonical request, as capital hex.</summary>
    public static string TuyaSign(string clientId, string secret, string? token, string t, string nonce, string method, string pathAndQuery, string body)
    {
        var stringToSign = $"{method}\n{AwsSigV4.Sha256Hex(body)}\n\n{pathAndQuery}";
        var payload = clientId + (token ?? "") + t + nonce + stringToSign;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
    }

    private static async Task<JsonDocument> TuyaAsync(ProviderCtx c, string method, string pathAndQuery, object? body = null)
    {
        var host = c.Cred["region"] switch { "india" => "openapi.tuyain.com", "eu" => "openapi.tuyaeu.com", "us" => "openapi.tuyaus.com", "china" => "openapi.tuyacn.com", _ => "openapi.tuyain.com" };
        var json = body is null ? "" : JsonSerializer.Serialize(body);
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture); var nonce = Guid.NewGuid().ToString("N");
        string? token = null;
        if (!pathAndQuery.StartsWith("/v1.0/token", StringComparison.Ordinal))
        {
            using var login = await TuyaAsync(c, "GET", "/v1.0/token?grant_type=1");
            token = J.S(login.RootElement, "result.access_token") ?? throw new AppException(409, "Tuya did not accept those credentials.", "reconnect_required");
            t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture); nonce = Guid.NewGuid().ToString("N");
        }
        var headers = new Dictionary<string, string> { ["client_id"] = c.Cred["clientId"], ["sign"] = TuyaSign(c.Cred["clientId"], c.Cred["secret"], token, t, nonce, method, pathAndQuery, json), ["t"] = t, ["sign_method"] = "HMAC-SHA256", ["nonce"] = nonce };
        if (token is not null) headers["access_token"] = token;
        var d = await c.JsonAsync(new HttpMethod(method), $"https://{host}{pathAndQuery}", body, headers: headers);
        if (J.S(d.RootElement, "success") == "false") { var msg = J.S(d.RootElement, "msg"); d.Dispose(); throw new AppException(400, $"Tuya said: {msg}"); }
        return d;
    }

    private static ProviderApp Tuya() => new()
    {
        Key = "tuya", Name = "Tuya", Icon = "mono:TU", Color = "#17D4D4", Account = "Smart devices",
        ConnectTitle = "Connect Tuya", ConnectLabel = "Connect Tuya",
        ConnectBlurb = "Create a cloud project on the Tuya IoT Platform, link your Smart Life app account to it, and enter the project's access id and secret.",
        WriteLabel = "Allow Daksh to control devices", WriteHint = "Full access lets Daksha switch devices on and off.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#tuya",
        NoBearer = true,
        Ask = Rx("tuya|smart life|plugs?|switches?|devices?"),
        Setup = ["platform.tuya.com → Cloud → **Create a cloud project** (Smart Home, your data centre). Copy the **Access ID** and **Access Secret**.", "Devices → **Link App Account** and scan the QR code with the Smart Life / Tuya app so the project can see your devices. Subscribe the project to the *Smart Home Basic Service* API group."],
        Notes = "Signed requests follow Tuya's documented HMAC-SHA256 scheme. The list-devices call needs the Smart Home Basic Service subscription.",
        Credentials = new()
        {
            Fields = [F("clientId", "Access ID"), F("secret", "Access secret", "secret"), new("region", "Data centre", "select", null, true, ["india", "eu", "us", "china"], null, "india")],
            Probe = async c => { using var d = await TuyaAsync(c, "GET", "/v1.0/token?grant_type=1"); return new ProfileInfo(c.Cred["clientId"], $"Tuya project ({c.Cred["region"]})"); },
        },
        Views =
        [
            new ViewSpec { Id = "devices", Label = "Devices", Dashboard = true, Load = async (c, _) => { using var d = await TuyaAsync(c, "GET", "/v1.0/iot-01/associated-users/devices?size=50");
                return J.Items(d.RootElement, "result.devices", x => new AppItem(J.S(x, "id") ?? "", J.S(x, "name") ?? "Device", J.S(x, "product_name"), null, J.S(x, "online") == "true" ? "online" : "offline")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "switch", Label = "Switch a device", Inputs = [new("device", "Device", "select-view", null, true, null, "devices"), F("code", "Switch code", def: "switch_1", hint: "Usually switch_1 or switch_led."), new("value", "Set to", "select", null, true, ["true", "false"], null, "true")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["device"], @"^[\w\-]{8,40}$") || !Regex.IsMatch(i["code"], @"^[\w]{1,30}$")) throw new AppException(400, "Check the device and the switch code."); using var _ = await TuyaAsync(c, "POST", $"/v1.0/iot-03/devices/{i["device"]}/commands", new { commands = new[] { new { code = i["code"], value = i["value"] == "true" } } }); return new ActionResult("Done."); } },
        ],
    };

    // ============================================================================================
    // Sonos (Sonos Control API)
    // ============================================================================================
    private const string SonosApi = "https://api.ws.sonos.com/control/api/v1/";

    private static ProviderApp Sonos() => new()
    {
        Key = "sonos", Name = "Sonos", Icon = "mono:SO", Color = "#000000", Account = "Speakers, rooms and playback",
        ConnectTitle = "Connect Sonos", ConnectLabel = "Continue with Sonos",
        ConnectBlurb = "Sign in with your Sonos account and Daksha can show your rooms and what is playing.",
        WriteLabel = "Allow Daksh to control playback", WriteHint = "Sonos has one permission for looking and controlling, so this is Daksha's own switch: it only controls speakers when it is on.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = false, SetupDoc = "Amit.md#sonos",
        Ask = Rx("sonos|speakers?|rooms?|volume|playing"),
        Setup = ["developer.sonos.com → **Create integration** (Control API). Copy the key (client id) and secret.", "Add the redirect URI `<API address>/api/integrations/sonos/callback` (Sonos requires HTTPS)."],
        OAuth = new() { Section = "Sonos", AuthUrl = "https://api.sonos.com/login/v3/oauth", TokenUrl = "https://api.sonos.com/login/v3/oauth/access", BasicAuth = true, ReadScopes = ["playback-control-all"], AuthExtra = new() },
        Profile = async c => { using var d = await c.GetAsync(SonosApi + "households"); var h = J.Arr(d.RootElement, "households").FirstOrDefault(); if (h.ValueKind != JsonValueKind.Object) throw new AppException(400, "No Sonos household was found.");
            return new ProfileInfo(J.S(h, "id") ?? "", $"Sonos {J.S(h, "name") ?? J.S(h, "id")}"); },
        Views =
        [
            new ViewSpec { Id = "groups", Label = "Rooms", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(SonosApi + $"households/{J.E(c.Account.AccountId)}/groups");
                return J.Items(d.RootElement, "groups", g => new AppItem(J.S(g, "id") ?? "", J.S(g, "name") ?? "Room", $"{J.Arr(g, "playerIds").Count()} speaker(s)", null, J.S(g, "playbackState")?.Replace("PLAYBACK_STATE_", "").ToLowerInvariant())); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "playback", Label = "Control playback", Inputs = [new("group", "Room", "select-view", null, true, null, "groups"), new("command", "Do", "select", null, true, ["play", "pause", "skipToNextTrack", "skipToPreviousTrack"], null, "play")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["group"], @"^[\w.:\-]{1,120}$")) throw new AppException(400, "Choose a room."); using var _ = await c.PostAsync(SonosApi + $"groups/{J.E(i["group"])}/playback/{i["command"]}"); return new ActionResult("Done."); } },
            new ActionSpec { Id = "volume", Label = "Set the volume", Inputs = [new("group", "Room", "select-view", null, true, null, "groups"), F("volume", "Volume (0–100)", "number", def: "30")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["group"], @"^[\w.:\-]{1,120}$") || !int.TryParse(i["volume"], out var v) || v is < 0 or > 100) throw new AppException(400, "Choose a room and a volume from 0 to 100."); using var _ = await c.PostAsync(SonosApi + $"groups/{J.E(i["group"])}/groupVolume", new { volume = v }); return new ActionResult("Volume set."); } },
        ],
    };

    // ============================================================================================
    // Weather (Open-Meteo: free, no key)
    // ============================================================================================
    private static string WmoText(int code) => code switch
    {
        0 => "Clear", 1 => "Mostly clear", 2 => "Partly cloudy", 3 => "Overcast", 45 or 48 => "Fog", 51 or 53 or 55 => "Drizzle", 56 or 57 => "Freezing drizzle", 61 => "Light rain", 63 => "Rain", 65 => "Heavy rain",
        66 or 67 => "Freezing rain", 71 or 73 or 75 or 77 => "Snow", 80 or 81 or 82 => "Showers", 85 or 86 => "Snow showers", 95 => "Thunderstorm", 96 or 99 => "Thunderstorm with hail", _ => "Weather",
    };

    private static async Task<(string Name, double Lat, double Lon)> GeocodeAsync(ProviderCtx c, string place)
    {
        using var d = await c.GetAsync($"https://geocoding-api.open-meteo.com/v1/search?name={J.E(place)}&count=1&language=en&format=json");
        var r = J.Arr(d.RootElement, "results").FirstOrDefault();
        if (r.ValueKind != JsonValueKind.Object) throw new AppException(404, $"No place called “{J.Clip(place, 40)}” was found.");
        return ($"{J.S(r, "name")}{(J.S(r, "admin1") is { Length: > 0 } a ? ", " + a : "")}", double.Parse(J.S(r, "latitude")!, CultureInfo.InvariantCulture), double.Parse(J.S(r, "longitude")!, CultureInfo.InvariantCulture));
    }

    private static async Task<IReadOnlyList<AppItem>> ForecastAsync(ProviderCtx c, string place, bool days)
    {
        var (name, lat, lon) = await GeocodeAsync(c, place);
        using var d = await c.GetAsync($"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&forecast_days=4&timezone=auto");
        var cur = J.At(d.RootElement, "current");
        var now = new AppItem(name, $"{name}: {J.S(cur, "temperature_2m")}°C, {WmoText(int.TryParse(J.S(cur, "weather_code"), out var w) ? w : -1)}", $"Feels like {J.S(cur, "apparent_temperature")}°C · wind {J.S(cur, "wind_speed_10m")} km/h", null,
            $"{J.S(d.RootElement, "daily.temperature_2m_max[0]")}° / {J.S(d.RootElement, "daily.temperature_2m_min[0]")}°");
        if (!days) return [now];
        var dates = J.Arr(d.RootElement, "daily.time").Select(x => x.GetString() ?? "").ToList();
        return [now, .. dates.Select((date, i) => new AppItem($"{name}-{date}", $"{(DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt.ToString("ddd d MMM", CultureInfo.InvariantCulture) : date)}: {WmoText(int.TryParse(J.S(d.RootElement, $"daily.weather_code[{i}]"), out var code) ? code : -1)}",
            $"Rain chance {J.S(d.RootElement, $"daily.precipitation_probability_max[{i}]")}%", null, $"{J.S(d.RootElement, $"daily.temperature_2m_max[{i}]")}° / {J.S(d.RootElement, $"daily.temperature_2m_min[{i}]")}°")).Skip(1)];
    }

    private static ProviderApp Weather() => new()
    {
        Key = "weather", Name = "Weather", Icon = "mono:WE", Color = "#38BDF8", Account = "Forecasts for your places",
        ConnectTitle = "Turn on Weather", ConnectLabel = "Turn on Weather",
        ConnectBlurb = "Forecasts from Open-Meteo (free, no account). Add your places in Manage and they appear on your dashboard.",
        WriteLabel = "Full access", WriteHint = "Weather is read-only.", Auth = AuthKind.Open, HasWrite = false, SetupDoc = "Amit.md#weather",
        Ask = Rx("weather|forecast|rain|temperature|umbrella|hot|cold"),
        ExtraSettings = [new("places", "My places", "text", "Up to five, separated by commas.", "Mumbai, Delhi", null, "Mumbai", 300)],
        Setup = ["Nothing to set. Weather uses the free Open-Meteo service, which needs no key (for non-commercial use)."],
        Notes = "Open-Meteo's free service is for non-commercial use; for commercial use take one of its paid plans.",
        Views =
        [
            new ViewSpec { Id = "now", Label = "My places", Dashboard = true, Empty = "Add your places in Manage → Preferences.", Load = async (c, _) =>
                { var items = new List<AppItem>(); foreach (var place in c.Setting("places").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5)) { try { items.AddRange(await ForecastAsync(c, place, false)); } catch (AppException ex) { items.Add(new AppItem(place, place, ex.Message)); } } return items; } },
            new ViewSpec { Id = "forecast", Label = "Forecast", Inputs = [F("place", "Place", placeholder: "Bengaluru")], Load = (c, i) => ForecastAsync(c, i["place"], true) },
        ],
        Assistant = async c => { var items = new List<string>(); foreach (var place in c.Setting("places").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)) { try { var f = await ForecastAsync(c, place, false); items.Add($"{f[0].Title} (today {f[0].Badge})"); } catch (AppException) { } } return items.Count == 0 ? null : "Weather right now: " + string.Join("; ", items) + "."; },
    };

    // ============================================================================================
    // Bhashini (Indian-language translation through the national language platform)
    // ============================================================================================
    private static readonly string[] BhashiniLanguages = ["en", "hi", "bn", "ta", "te", "mr", "gu", "kn", "ml", "pa", "or", "as", "ur", "sa", "ne"];

    private static ProviderApp Bhashini() => new()
    {
        Key = "bhashini", Name = "Bhashini", Icon = "mono:BH", Color = "#FF6B35", Account = "Translation between Indian languages",
        ConnectTitle = "Connect Bhashini", ConnectLabel = "Connect Bhashini",
        ConnectBlurb = "Enter your Bhashini (ULCA) user id and API key. Daksha can translate text between English and Indian languages.",
        WriteLabel = "Full access", WriteHint = "Translation only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#bhashini",
        Ask = Rx("bhashini|translate|translation|in hindi|in tamil|in bengali|in telugu"),
        Setup = ["Nothing to set on the server. Each person registers at bhashini.gov.in, opens **My Profile → API keys** and copies their **User ID** and **ULCA API key**."],
        Notes = "Uses the Bhashini pipeline service (look up the model, then run it). Languages: English, Hindi, Bengali, Tamil, Telugu, Marathi, Gujarati, Kannada, Malayalam, Punjabi, Odia, Assamese, Urdu, Sanskrit, Nepali.",
        NoBearer = true,
        Credentials = new()
        {
            Fields = [F("userId", "User ID"), F("apiKey", "ULCA API key", "secret")],
            Probe = async c => { using var d = await BhashiniPipelineAsync(c, "en", "hi"); return new ProfileInfo(c.Cred["userId"], "Bhashini"); },
        },
        Views =
        [
            new ViewSpec { Id = "translate", Label = "Translate", Dashboard = true, Inputs = [F("text", "Text", "textarea", required: false), new("from", "From", "select", null, false, BhashiniLanguages, null, "en"), new("to", "To", "select", null, false, BhashiniLanguages, null, "hi")], Load = async (c, i) =>
                { if (i.GetValueOrDefault("text", "").Length == 0) return [new AppItem("hint", "Type some text above and choose the languages.", null)];
                  if (i["text"].Length > 1500) throw new AppException(400, "Translate up to 1,500 characters at a time.");
                  var (from, to) = (i.GetValueOrDefault("from", "en"), i.GetValueOrDefault("to", "hi")); if (!BhashiniLanguages.Contains(from) || !BhashiniLanguages.Contains(to)) throw new AppException(400, "Choose the languages.");
                  using var pipe = await BhashiniPipelineAsync(c, from, to);
                  var service = J.S(pipe.RootElement, "pipelineResponseConfig[0].config[0].serviceId") ?? throw new AppException(502, "Bhashini has no translation model for that pair.");
                  var callback = J.S(pipe.RootElement, "pipelineInferenceAPIEndPoint.callbackUrl") ?? throw new AppException(502, "Bhashini did not return a service address.");
                  var keyName = J.S(pipe.RootElement, "pipelineInferenceAPIEndPoint.inferenceApiKey.name") ?? "Authorization"; var keyValue = J.S(pipe.RootElement, "pipelineInferenceAPIEndPoint.inferenceApiKey.value") ?? "";
                  if (!Uri.TryCreate(callback, UriKind.Absolute, out var cb) || cb.Scheme != Uri.UriSchemeHttps) throw new AppException(502, "Bhashini returned an unexpected service address.");
                  using var d = await c.JsonAsync(HttpMethod.Post, callback, new { pipelineTasks = new[] { new { taskType = "translation", config = new { language = new { sourceLanguage = from, targetLanguage = to }, serviceId = service } } }, inputData = new { input = new[] { new { source = i["text"] } } } },
                      headers: new Dictionary<string, string> { [keyName] = keyValue });
                  var target = J.S(d.RootElement, "pipelineResponse[0].output[0].target") ?? throw new AppException(502, "Bhashini returned no translation.");
                  return [new AppItem("result", J.Clip(target, 1500), $"{from} → {to}", null, null, null, null, target)]; } },
        ],
    };

    private static Task<JsonDocument> BhashiniPipelineAsync(ProviderCtx c, string from, string to) =>
        c.JsonAsync(HttpMethod.Post, "https://meity-auth.ulcacontrib.org/ulca/apis/v0/model/getModelsPipeline",
            new { pipelineTasks = new[] { new { taskType = "translation", config = new { language = new { sourceLanguage = from, targetLanguage = to } } } }, pipelineRequestConfig = new { pipelineId = "64392f96daac500b55c543cd" } },
            headers: new Dictionary<string, string> { ["userID"] = c.Cred["userId"], ["ulcaApiKey"] = c.Cred["apiKey"] });
}

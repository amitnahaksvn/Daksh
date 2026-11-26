using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// Shopping, money, travel, documents and the home: Shopify, Amazon Selling Partner, QuickBooks Online, Uber, Booking.com, IRCTC, DigiLocker,
// Google Maps Platform and Home Assistant.

public static partial class ProviderApps
{
    private static string Money(string? amount, string? currency) => string.IsNullOrEmpty(amount) ? "" : $"{amount} {currency}".Trim();
    private static readonly Regex NumericId = new(@"^\d{1,20}$", RegexOptions.Compiled);

    // ============================================================================================
    // Shopify (one store per person; the person enters the shop address before signing in)
    // ============================================================================================
    private const string ShopifyVersion = "2025-01";
    private static readonly Regex ShopHost = new(@"^[a-z0-9][a-z0-9\-]{0,60}\.myshopify\.com$", RegexOptions.Compiled);

    public static string NormalizeShop(string raw)
    {
        var s = raw.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
        if (s.Contains('/')) s = s[..s.IndexOf('/')];
        if (!s.Contains('.')) s += ".myshopify.com";
        return ShopHost.IsMatch(s) ? s : throw new AppException(400, "Enter your shop address, like my-store.myshopify.com.");
    }

    private static string ShopUrl(ProviderCtx c, string path) => $"https://{NormalizeShop(c.Extra("shop"))}/admin/api/{ShopifyVersion}/{path}";

    private static ProviderApp Shopify() => new()
    {
        Key = "shopify", Name = "Shopify", Icon = "SiShopify", Color = "#7AB55C", Account = "Orders, products and customers",
        ConnectTitle = "Connect your Shopify store", ConnectLabel = "Continue with Shopify",
        ConnectBlurb = "Enter your shop address and approve Daksha in Shopify. Daksha can then show your orders, products and customers.",
        WriteLabel = "Allow Daksh to change the store", WriteHint = "Full access lets Daksha add products (as drafts) and add notes to orders.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#shopify",
        Ask = Rx("shopify|my store|orders?|products?|customers?|inventory"),
        NoBearer = true,
        Decorate = (r, c) => { if (c.Token is not null) r.Headers.TryAddWithoutValidation("X-Shopify-Access-Token", c.Token); },
        OAuth = new()
        {
            Section = "Shopify", AuthUrl = "https://{shop}/admin/oauth/authorize", TokenUrl = "https://{shop}/admin/oauth/access_token", ScopeSeparator = ",",
            ReadScopes = ["read_orders", "read_products", "read_customers"], WriteScopes = ["write_products", "write_orders"],
            ConnectInputs = [new("shop", "Shop address", Placeholder: "my-store.myshopify.com")],
            CallbackExtras = ["shop"],
            BuildAuthUrl = (_, c, state, _, write, inputs) =>
            {
                var shop = NormalizeShop(inputs["shop"]);
                var scopes = write ? new[] { "read_orders", "read_products", "read_customers", "write_products", "write_orders" } : new[] { "read_orders", "read_products", "read_customers" };
                return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"https://{shop}/admin/oauth/authorize",
                    new Dictionary<string, string?> { ["client_id"] = c.ClientId, ["scope"] = string.Join(',', scopes), ["redirect_uri"] = c.RedirectUri, ["state"] = state });
            },
            ValidateCallback = (c, q) =>
            {
                if (!q.TryGetValue("shop", out var shop) || !ShopHost.IsMatch(shop)) return "bad shop";
                if (!q.TryGetValue("hmac", out var hmac)) return "no hmac";
                var message = string.Join('&', q.Where(p => p.Key != "hmac").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
                var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(c.ClientSecret ?? ""), Encoding.UTF8.GetBytes(message)));
                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(hmac.ToLowerInvariant())) ? null : "hmac mismatch";
            },
            Exchange = async x =>
            {
                var shop = x.Query.GetValueOrDefault("shop") ?? "";
                if (!ShopHost.IsMatch(shop)) return null;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{shop}/admin/oauth/access_token") { Content = System.Net.Http.Json.JsonContent.Create(new { client_id = x.Config.ClientId, client_secret = x.Config.ClientSecret, code = x.Code }) };
                using var res = await x.Clients.CreateClient("provider-apps").SendAsync(req, x.Ct);
                if (!res.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(x.Ct));
                var token = J.S(doc.RootElement, "access_token");
                // Shopify store tokens do not expire.
                return token is null ? null : new TokenSet(token, null, DateTimeOffset.UtcNow.AddYears(10), new() { ["shop"] = shop });
            },
        },
        Profile = async c => { using var d = await c.GetAsync(ShopUrl(c, "shop.json")); return new ProfileInfo(J.S(d.RootElement, "shop.id") ?? c.Extra("shop"), J.S(d.RootElement, "shop.name") ?? c.Extra("shop")); },
        Views =
        [
            new ViewSpec { Id = "orders", Label = "Orders", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(ShopUrl(c, "orders.json?status=any&limit=25"));
                return J.Items(d.RootElement, "orders", o => new AppItem(J.S(o, "id") ?? "", $"{J.S(o, "name")} · {Money(J.S(o, "total_price"), J.S(o, "currency"))}",
                    J.T(o, $"{{financial_status}} · {(J.S(o, "fulfillment_status") ?? "unfulfilled")} · {{customer.first_name}} {{customer.last_name}} · ") + J.When(J.S(o, "created_at")), $"https://{c.Extra("shop")}/admin/orders/{J.S(o, "id")}")); } },
            new ViewSpec { Id = "products", Label = "Products", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(ShopUrl(c, "products.json?limit=25"));
                return J.Items(d.RootElement, "products", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "title") ?? "Product", $"{J.S(p, "status")} · {J.S(p, "variants[0].price")} · {J.Arr(p, "variants").Count()} variants", $"https://{c.Extra("shop")}/admin/products/{J.S(p, "id")}", J.S(p, "status") == "active" ? null : J.S(p, "status"), J.S(p, "image.src"))); } },
            new ViewSpec { Id = "customers", Label = "Customers", Load = async (c, _) => { using var d = await c.GetAsync(ShopUrl(c, "customers.json?limit=25"));
                return J.Items(d.RootElement, "customers", u => new AppItem(J.S(u, "id") ?? "", J.T(u, "{first_name} {last_name}") is { Length: > 0 } n ? n : J.S(u, "email") ?? "Customer", $"{J.S(u, "email")} · {J.S(u, "orders_count") ?? "0"} orders · {J.S(u, "total_spent")}", $"https://{c.Extra("shop")}/admin/customers/{J.S(u, "id")}")); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create-product", Label = "Add a product (as a draft)", Inputs = [new("title", "Title"), new("description", "Description", "textarea", Required: false), new("price", "Price", "number", Placeholder: "499.00"), new("vendor", "Vendor", Required: false)],
                Run = async (c, i) => { if (!decimal.TryParse(i["price"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price) || price < 0) throw new AppException(400, "Enter the price as a number.");
                    using var d = await c.PostAsync(ShopUrl(c, "products.json"), new { product = new { title = i["title"], body_html = System.Net.WebUtility.HtmlEncode(i["description"]), vendor = i["vendor"], status = "draft", variants = new[] { new { price = price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) } } } });
                    return new ActionResult("Product added as a draft.", $"https://{c.Extra("shop")}/admin/products/{J.S(d.RootElement, "product.id")}"); } },
            new ActionSpec { Id = "order-note", Label = "Add a note to an order", Inputs = [new("order", "Order id", Placeholder: "From the Orders list"), new("note", "Note", "textarea")],
                Run = async (c, i) => { if (!NumericId.IsMatch(i["order"])) throw new AppException(400, "Use the numeric order id.");
                    using var _ = await c.JsonAsync(HttpMethod.Put, ShopUrl(c, $"orders/{i["order"]}.json"), new { order = new { id = long.Parse(i["order"]), note = i["note"] } }); return new ActionResult("Note saved."); } },
        ],
    };

    // ============================================================================================
    // Amazon Selling Partner API (read-only: orders, listings, inventory)
    // ============================================================================================
    private static string SpEndpoint(ProviderCtx c) => c.Server("AmazonSellingPartner:Region").ToLowerInvariant() switch
    {
        "na" => "https://sellingpartnerapi-na.amazon.com", "fe" => "https://sellingpartnerapi-fe.amazon.com", _ => "https://sellingpartnerapi-eu.amazon.com",
    };
    private static string SpMarketplace(ProviderCtx c) => c.Server("AmazonSellingPartner:MarketplaceId") is { Length: > 0 } m ? m : "A21TJRUUN4KXV";
    private static string SpSellerCentral(IConfiguration c) => (c["AmazonSellingPartner:SellerCentralUrl"] is { Length: > 0 } u ? u : "https://sellercentral.amazon.in").TrimEnd('/');

    private static ProviderApp AmazonSellingPartner() => new()
    {
        Key = "amazon-selling-partner", Name = "Amazon Selling Partner API", Icon = "mono:ASP", Color = "#FF9900", Account = "Orders, listings and inventory",
        ConnectTitle = "Connect your Amazon seller account", ConnectLabel = "Continue to Seller Central",
        ConnectBlurb = "For Amazon sellers. Authorize Daksha in Seller Central and it can show your recent orders, listings and stock. Read-only.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads from Amazon.",
        Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#amazon-selling-partner-api",
        Ask = Rx("amazon seller|seller central|amazon orders?|my listings?|fba"),
        NoBearer = true,
        Decorate = (r, c) => { if (c.Token is not null) r.Headers.TryAddWithoutValidation("x-amz-access-token", c.Token); },
        ConfiguredOverride = c => ProviderEngine.TryGetOAuthConfig(Get("amazon-selling-partner"), c, out _) && !string.IsNullOrWhiteSpace(c["AmazonSellingPartner:ApplicationId"]),
        OAuth = new()
        {
            Section = "AmazonSellingPartner", AuthUrl = "https://sellercentral.amazon.in/apps/authorize/consent", TokenUrl = "https://api.amazon.com/auth/o2/token",
            CodeParam = "spapi_oauth_code", CallbackExtras = ["selling_partner_id"],
            BuildAuthUrl = (config, c, state, _, _, _) => Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString($"{SpSellerCentral(config)}/apps/authorize/consent",
                new Dictionary<string, string?> { ["application_id"] = config["AmazonSellingPartner:ApplicationId"], ["state"] = state, ["redirect_uri"] = c.RedirectUri, ["version"] = config["AmazonSellingPartner:Draft"] == "false" ? null : "beta" }),
        },
        Profile = async c =>
        {
            using var d = await c.GetAsync($"{SpEndpoint(c)}/sellers/v1/marketplaceParticipations");
            var names = string.Join(", ", J.Arr(d.RootElement, "payload").Where(p => J.S(p, "participation.isParticipating") == "true").Select(p => J.S(p, "marketplace.name")).Where(n => n is not null));
            var seller = c.Extra("selling_partner_id");
            if (seller.Length == 0) throw new AppException(502, "Amazon did not say which seller account this is.");
            return new ProfileInfo(seller, names.Length > 0 ? $"Seller {seller} ({names})" : $"Seller {seller}");
        },
        Views =
        [
            new ViewSpec { Id = "orders", Label = "Recent orders", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{SpEndpoint(c)}/orders/v0/orders?MarketplaceIds={SpMarketplace(c)}&CreatedAfter={J.E(Ago(30))}&MaxResultsPerPage=25");
                return J.Items(d.RootElement, "payload.Orders", o => new AppItem(J.S(o, "AmazonOrderId") ?? "", $"{J.S(o, "AmazonOrderId")} · {Money(J.S(o, "OrderTotal.Amount"), J.S(o, "OrderTotal.CurrencyCode"))}", $"{J.S(o, "OrderStatus")} · {J.When(J.S(o, "PurchaseDate"))}", $"{SpSellerCentral(c.Config)}/orders-v3/order/{J.S(o, "AmazonOrderId")}", J.S(o, "OrderStatus"))); } },
            new ViewSpec { Id = "listings", Label = "Listings", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{SpEndpoint(c)}/listings/2021-08-01/items/{J.E(c.Account.AccountId)}?marketplaceIds={SpMarketplace(c)}&includedData=summaries&pageSize=20");
                return J.Items(d.RootElement, "items", l => new AppItem(J.S(l, "sku") ?? "", J.S(l, "summaries[0].itemName") ?? J.S(l, "sku") ?? "Listing", $"SKU {J.S(l, "sku")} · ASIN {J.S(l, "summaries[0].asin")}", $"https://www.amazon.in/dp/{J.S(l, "summaries[0].asin")}", J.Arr(l, "summaries[0].status").FirstOrDefault().ValueKind == JsonValueKind.String ? J.Arr(l, "summaries[0].status").First().GetString() : null)); } },
            new ViewSpec { Id = "inventory", Label = "FBA inventory", Load = async (c, _) => { using var d = await c.GetAsync($"{SpEndpoint(c)}/fba/inventory/v1/summaries?details=false&granularityType=Marketplace&granularityId={SpMarketplace(c)}&marketplaceIds={SpMarketplace(c)}");
                return J.Items(d.RootElement, "payload.inventorySummaries", s => new AppItem(J.S(s, "sellerSku") ?? "", J.S(s, "productName") ?? J.S(s, "sellerSku") ?? "Item", $"SKU {J.S(s, "sellerSku")} · ASIN {J.S(s, "asin")}", null, $"{J.S(s, "totalQuantity") ?? "0"} in stock")); } },
        ],
    };

    // ============================================================================================
    // QuickBooks Online
    // ============================================================================================
    private static string QbBase(ProviderCtx c) => (c.Server("QuickBooks:Environment") == "sandbox" ? "https://sandbox-quickbooks.api.intuit.com" : "https://quickbooks.api.intuit.com") + $"/v3/company/{J.E(c.Extra("realmId"))}/";
    private static string QbQuery(ProviderCtx c, string sql) => $"{QbBase(c)}query?minorversion=75&query={J.E(sql)}";

    private static ProviderApp QuickBooks() => new()
    {
        Key = "quickbooks-online", Name = "QuickBooks Online", Icon = "SiQuickbooks", Color = "#2CA01C", Account = "Invoices, customers and bills",
        ConnectTitle = "Connect QuickBooks Online", ConnectLabel = "Continue with QuickBooks",
        ConnectBlurb = "Sign in with Intuit and choose your company. Daksha can show invoices, customers, bills and your monthly profit and loss.",
        WriteLabel = "Allow Daksh to create customers and invoices", WriteHint = "QuickBooks has a single permission for reading and changing books, so Daksha adds its own switch: it only creates records when this is on.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = false, SetupDoc = "Amit.md#quickbooks-online",
        Ask = Rx("quickbooks|invoices?|unpaid|bills?|profit|accounting|books"),
        Decorate = (r, _) => r.Headers.Accept.ParseAdd("application/json"),
        OAuth = new()
        {
            Section = "QuickBooks", AuthUrl = "https://appcenter.intuit.com/connect/oauth2", TokenUrl = "https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer", BasicAuth = true,
            ReadScopes = ["com.intuit.quickbooks.accounting"], CallbackExtras = ["realmId"],
        },
        Profile = async c =>
        {
            if (c.Extra("realmId").Length == 0) throw new AppException(502, "QuickBooks did not say which company was chosen.");
            using var d = await c.GetAsync($"{QbBase(c)}companyinfo/{J.E(c.Extra("realmId"))}?minorversion=75");
            return new ProfileInfo(c.Extra("realmId"), J.S(d.RootElement, "CompanyInfo.CompanyName") ?? "QuickBooks company");
        },
        Views =
        [
            new ViewSpec { Id = "invoices", Label = "Recent invoices", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(QbQuery(c, "select * from Invoice orderby TxnDate desc maxresults 25")); return QbInvoices(d.RootElement); } },
            new ViewSpec { Id = "unpaid", Label = "Unpaid invoices", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(QbQuery(c, "select * from Invoice where Balance > '0' orderby DueDate maxresults 25")); return QbInvoices(d.RootElement); } },
            new ViewSpec { Id = "customers", Label = "Customers", Load = async (c, _) => { using var d = await c.GetAsync(QbQuery(c, "select * from Customer orderby DisplayName maxresults 50"));
                return J.Items(d.RootElement, "QueryResponse.Customer", u => new AppItem(J.S(u, "Id") ?? "", J.S(u, "DisplayName") ?? "Customer", J.First(u, "PrimaryEmailAddr.Address", "PrimaryPhone.FreeFormNumber"), null, J.S(u, "Balance") is { } b && b != "0" ? $"owes {b}" : null)); } },
            new ViewSpec { Id = "bills", Label = "Bills", Load = async (c, _) => { using var d = await c.GetAsync(QbQuery(c, "select * from Bill orderby DueDate desc maxresults 25"));
                return J.Items(d.RootElement, "QueryResponse.Bill", b => new AppItem(J.S(b, "Id") ?? "", $"{J.S(b, "VendorRef.name")} · {J.S(b, "TotalAmt")}", $"due {J.S(b, "DueDate")} · balance {J.S(b, "Balance")}", null, J.S(b, "Balance") is "0" ? "paid" : "open")); } },
            new ViewSpec { Id = "profit", Label = "Profit and loss (this month)", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{QbBase(c)}reports/ProfitAndLoss?minorversion=75&date_macro={J.E("This Month")}");
                return J.Items(d.RootElement, "Rows.Row", r => J.S(r, "Summary.ColData[1].value") is { } v ? new AppItem(J.S(r, "group") ?? J.S(r, "Summary.ColData[0].value") ?? "", J.S(r, "Summary.ColData[0].value") ?? J.S(r, "group") ?? "", null, null, v) : null); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create-customer", Label = "Create a customer", Inputs = [new("name", "Customer name"), new("email", "Email", Required: false)],
                Run = async (c, i) => { using var d = await c.PostAsync($"{QbBase(c)}customer?minorversion=75", new { DisplayName = i["name"], PrimaryEmailAddr = i["email"].Length > 0 ? new { Address = i["email"] } : null }); return new ActionResult("Customer created."); } },
            new ActionSpec { Id = "create-invoice", Label = "Create an invoice", Hint = "Uses your default “Services” item (id 1). Create it in QuickBooks first if your books do not have one.", Inputs = [new("customer", "Customer", "select-view", OptionsView: "customers"), new("amount", "Amount", "number"), new("description", "Description", Required: false)],
                Run = async (c, i) => { if (!decimal.TryParse(i["amount"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0) throw new AppException(400, "Enter the amount as a number above zero.");
                    using var d = await c.PostAsync($"{QbBase(c)}invoice?minorversion=75", new { CustomerRef = new { value = i["customer"] }, Line = new[] { new { Amount = amount, DetailType = "SalesItemLineDetail", Description = i["description"], SalesItemLineDetail = new { ItemRef = new { value = "1" } } } } });
                    return new ActionResult("Invoice created.", $"https://app.qbo.intuit.com/app/invoice?txnId={J.S(d.RootElement, "Invoice.Id")}"); } },
        ],
    };

    private static IReadOnlyList<AppItem> QbInvoices(JsonElement root) => J.Items(root, "QueryResponse.Invoice", i => new AppItem(J.S(i, "Id") ?? "",
        $"Invoice {J.S(i, "DocNumber")} · {J.S(i, "CustomerRef.name")}", $"{Money(J.S(i, "TotalAmt"), J.S(i, "CurrencyRef.value"))} · due {J.S(i, "DueDate")}",
        $"https://app.qbo.intuit.com/app/invoice?txnId={J.S(i, "Id")}", J.S(i, "Balance") is "0" ? "paid" : $"owes {J.S(i, "Balance")}"));

    // ============================================================================================
    // Uber (Rides API; access to it is limited by Uber)
    // ============================================================================================
    private const string UberApi = "https://api.uber.com/v1.2/";

    private static (string Lat, string Lng) Coordinates(string value)
    {
        var p = value.Split(',', StringSplitOptions.TrimEntries);
        if (p.Length != 2 || !double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out var lat) || !double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out var lng) || Math.Abs(lat) > 90 || Math.Abs(lng) > 180)
            throw new AppException(400, "Enter a place as “latitude, longitude”, like 19.0760, 72.8777.");
        return (lat.ToString(System.Globalization.CultureInfo.InvariantCulture), lng.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static ProviderApp Uber() => new()
    {
        Key = "uber", Name = "Uber", Icon = "SiUber", Color = "#000000", Account = "Ride history, estimates and requests",
        ConnectTitle = "Connect your Uber account", ConnectLabel = "Continue with Uber",
        ConnectBlurb = "Sign in with Uber and Daksha can show your trips and price estimates between two places.",
        WriteLabel = "Allow Daksh to request rides", WriteHint = "Full access lets Daksha request and cancel rides. Uber only allows this for approved apps.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#uber",
        Ask = Rx("uber|cab|ride|taxi"),
        OAuth = new() { Section = "Uber", AuthUrl = "https://auth.uber.com/oauth/v2/authorize", TokenUrl = "https://auth.uber.com/oauth/v2/token", ReadScopes = ["profile", "history"], WriteScopes = ["request"] },
        Profile = async c => { using var d = await c.GetAsync($"{UberApi}me"); return new ProfileInfo(J.S(d.RootElement, "rider_id") ?? J.S(d.RootElement, "uuid") ?? "", J.T(d.RootElement, "{first_name} {last_name}") is { Length: > 0 } n ? n : J.S(d.RootElement, "email") ?? "Uber rider"); },
        Views =
        [
            new ViewSpec { Id = "history", Label = "Trips", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{UberApi}history?limit=25");
                return J.Items(d.RootElement, "history", h => new AppItem(J.S(h, "request_id") ?? "", $"{J.S(h, "start_city.display_name")} trip", $"{J.When(J.S(h, "start_time"))} · {J.S(h, "distance")?.Split('.')[0]} mi", null, J.S(h, "status"))); } },
            new ViewSpec { Id = "estimates", Label = "Price estimates", Inputs = [new("from", "From (latitude, longitude)", Placeholder: "19.0760, 72.8777"), new("to", "To (latitude, longitude)", Placeholder: "19.1136, 72.8697")], Load = async (c, i) =>
                { var (a, b) = (Coordinates(i["from"]), Coordinates(i["to"]));
                  using var d = await c.GetAsync($"{UberApi}estimates/price?start_latitude={a.Lat}&start_longitude={a.Lng}&end_latitude={b.Lat}&end_longitude={b.Lng}");
                  return J.Items(d.RootElement, "prices", p => new AppItem(J.S(p, "product_id") ?? "", J.S(p, "display_name") ?? "Ride", $"{(int.TryParse(J.S(p, "duration"), out var s) ? s / 60 : 0)} min · {J.S(p, "distance")} mi", null, J.S(p, "estimate"))); } },
            new ViewSpec { Id = "current", Label = "Current ride", Load = async (c, _) =>
                { try { using var d = await c.GetAsync($"{UberApi}requests/current");
                      return (IReadOnlyList<AppItem>)[new AppItem(J.S(d.RootElement, "request_id") ?? "", $"{J.S(d.RootElement, "status")} · {J.S(d.RootElement, "driver.name") ?? "finding a driver"}", J.T(d.RootElement, "{vehicle.make} {vehicle.model} {vehicle.license_plate}"), null, J.S(d.RootElement, "eta") is { } e ? $"{e} min" : null)]; }
                  catch (AppException ex) when (ex.StatusCode == 404) { return []; } }, Empty = "No ride in progress." },
        ],
        Actions =
        [
            new ActionSpec { Id = "request", Label = "Request a ride", Hint = "Get the product id from Price estimates.", Inputs = [new("product", "Product id"), new("from", "Pick-up (latitude, longitude)"), new("to", "Drop-off (latitude, longitude)")],
                Run = async (c, i) => { var (a, b) = (Coordinates(i["from"]), Coordinates(i["to"]));
                    using var d = await c.PostAsync($"{UberApi}requests", new { product_id = i["product"], start_latitude = double.Parse(a.Lat, System.Globalization.CultureInfo.InvariantCulture), start_longitude = double.Parse(a.Lng, System.Globalization.CultureInfo.InvariantCulture), end_latitude = double.Parse(b.Lat, System.Globalization.CultureInfo.InvariantCulture), end_longitude = double.Parse(b.Lng, System.Globalization.CultureInfo.InvariantCulture) });
                    return new ActionResult($"Ride requested ({J.S(d.RootElement, "status")})."); } },
            new ActionSpec { Id = "cancel", Label = "Cancel the current ride", Inputs = [], Run = async (c, _) => { using var d = await c.JsonAsync(HttpMethod.Delete, $"{UberApi}requests/current"); return new ActionResult("Ride cancelled."); } },
        ],
    };

    // ============================================================================================
    // Booking.com (Demand API, partner credentials)
    // ============================================================================================
    private const string BookingApi = "https://demandapi.booking.com/3.1/";

    private static ProviderApp BookingCom() => new()
    {
        Key = "booking-com", Name = "Booking.com", Icon = "SiBookingdotcom", Color = "#003A9A", Account = "Stays search",
        ConnectTitle = "Connect Booking.com", ConnectLabel = "Connect",
        ConnectBlurb = "For Booking.com Demand API partners. Paste your token and affiliate id to search stays by city and dates. Booking itself happens on Booking.com.",
        WriteLabel = "Full access", WriteHint = "Daksha only searches.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#bookingcom",
        Ask = Rx("booking\\.com|hotels?|stays?|accommodations?"),
        NoBearer = true,
        Decorate = (r, c) => { r.Headers.TryAddWithoutValidation("Authorization", $"Bearer {c.Cred.GetValueOrDefault("token")}"); r.Headers.TryAddWithoutValidation("X-Affiliate-Id", c.Cred.GetValueOrDefault("affiliateId")); },
        Credentials = new()
        {
            Fields = [new("token", "Demand API token", "secret"), new("affiliateId", "Affiliate id", Placeholder: "1234567")],
            Probe = async c => { using var d = await c.PostAsync($"{BookingApi}common/locations/countries", new { }); return new ProfileInfo(c.Cred["affiliateId"], $"Affiliate {c.Cred["affiliateId"]}"); },
        },
        Views =
        [
            new ViewSpec { Id = "start", Label = "Booking.com", Dashboard = true, Load = (_, _) => Task.FromResult<IReadOnlyList<AppItem>>([new AppItem("search", "Search stays", "Open Daksha → Booking.com to search by city and dates.", "https://www.booking.com/")]) },
            new ViewSpec { Id = "cities", Label = "Find a city id", Inputs = [new("country", "Country code", Placeholder: "in", Default: "in")], Load = async (c, i) =>
                { using var d = await c.PostAsync($"{BookingApi}common/locations/cities", new { countries = new[] { i["country"].ToLowerInvariant() } });
                  return J.Items(d.RootElement, "data", x => new AppItem(J.S(x, "id") ?? "", J.First(x, "name", "names.en") ?? J.S(x, "id") ?? "", $"City id {J.S(x, "id")}", null, J.S(x, "id")), 200); } },
            new ViewSpec { Id = "search", Label = "Search stays", Inputs = [new("city", "City id", "number", Placeholder: "-2092174", Hint: "Look up ids in “Find a city id”."), new("checkin", "Check-in", "date"), new("checkout", "Check-out", "date"), new("adults", "Adults", "number", Default: "2")], Load = async (c, i) =>
                { using var d = await c.PostAsync($"{BookingApi}accommodations/search", new { booker = new { country = "in", platform = "desktop" }, city = long.TryParse(i["city"], out var city) ? city : throw new AppException(400, "Enter the numeric city id."),
                      checkin = i["checkin"], checkout = i["checkout"], guests = new { number_of_adults = int.TryParse(i["adults"], out var a) ? Math.Clamp(a, 1, 10) : 2, number_of_rooms = 1 }, extras = new[] { "products" } });
                  return J.Items(d.RootElement, "data", h => new AppItem(J.S(h, "id") ?? "", J.First(h, "name", "details.name") ?? $"Property {J.S(h, "id")}", J.First(h, "location.address", "details.address"), $"https://www.booking.com/hotel/{J.S(h, "id")}.html",
                      J.First(h, "price.book", "price.base", "products[0].price.total.accommodation_currency.value", "products[0].price.base.accommodation_currency.value"))); } },
        ],
    };

    // ============================================================================================
    // IRCTC trains, through the RapidAPI "IRCTC" service (IRCTC has no public API; booking stays on irctc.co.in)
    // ============================================================================================
    private const string IrctcBase = "https://irctc1.p.rapidapi.com/api/";
    private static readonly Regex Pnr = new(@"^\d{10}$", RegexOptions.Compiled);

    private static async Task<AppItem> PnrItemAsync(ProviderCtx c, string pnr)
    {
        using var d = await c.GetAsync($"{IrctcBase}v3/getPNRStatus?pnrNumber={pnr}");
        var data = J.At(d.RootElement, "data");
        var pax = string.Join(", ", J.Arr(data, "PassengerStatus").Select(p => J.First(p, "CurrentStatus", "BookingStatus")).Where(s => s is not null));
        return new AppItem(pnr, $"{J.S(data, "TrainName")} ({J.S(data, "TrainNo")})", $"PNR {pnr} · {J.S(data, "Doj")} · {J.S(data, "From")} → {J.S(data, "To")}", "https://www.indianrail.gov.in/enquiry/PNR/PnrEnquiry.html", pax.Length > 0 ? pax : null, null, null, J.S(data, "ChartPrepared") == "true" ? "Chart prepared" : "Chart not prepared yet");
    }

    private static ProviderApp Irctc() => new()
    {
        Key = "irctc", Name = "IRCTC", Icon = "mono:IR", Color = "#1A5276", Account = "PNR status, trains and live running",
        ConnectTitle = "Connect IRCTC train information", ConnectLabel = "Connect",
        ConnectBlurb = "Check PNR status, find trains between stations and follow a train live. IRCTC has no public API, so this uses the RapidAPI “IRCTC” service with your own RapidAPI key. Tickets are booked on irctc.co.in.",
        WriteLabel = "Full access", WriteHint = "Daksha only looks things up.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#irctc",
        Ask = Rx("irctc|pnr|train|trains|railway"),
        NoBearer = true,
        Decorate = (r, c) => { r.Headers.TryAddWithoutValidation("X-RapidAPI-Key", c.Cred.GetValueOrDefault("key")); r.Headers.TryAddWithoutValidation("X-RapidAPI-Host", "irctc1.p.rapidapi.com"); },
        ExtraSettings = [new("pnrs", "My PNR numbers", "text", "Up to 5 ten-digit PNRs, separated by commas. Shown on your dashboard.", "2412345678, 4512345678")],
        Credentials = new()
        {
            Fields = [new("key", "RapidAPI key", "secret", Hint: "From rapidapi.com after subscribing to the IRCTC API.")],
            Probe = async c => { using var d = await c.GetAsync($"{IrctcBase}v1/searchStation?query=delhi"); return new ProfileInfo("rapidapi", "IRCTC via RapidAPI"); },
        },
        Views =
        [
            new ViewSpec { Id = "my-pnrs", Label = "My PNRs", Dashboard = true, Empty = "Add your PNR numbers in Manage to see them here.", Load = async (c, _) =>
                { var list = c.Setting("pnrs").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Where(p => Pnr.IsMatch(p)).Distinct().Take(5).ToList();
                  var items = new List<AppItem>(); foreach (var p in list) { try { items.Add(await PnrItemAsync(c, p)); } catch (AppException ex) { items.Add(new AppItem(p, $"PNR {p}", ex.Message)); } } return items; } },
            new ViewSpec { Id = "pnr", Label = "Check a PNR", Inputs = [new("pnr", "PNR number", Placeholder: "10 digits")], Load = async (c, i) => { if (!Pnr.IsMatch(i["pnr"])) throw new AppException(400, "A PNR has 10 digits."); return [await PnrItemAsync(c, i["pnr"])]; } },
            new ViewSpec { Id = "trains", Label = "Trains between stations", Inputs = [new("from", "From station code", Placeholder: "NDLS"), new("to", "To station code", Placeholder: "BCT"), new("date", "Date", "date")], Load = async (c, i) =>
                { using var d = await c.GetAsync($"{IrctcBase}v3/trainBetweenStations?fromStationCode={J.E(i["from"].ToUpperInvariant())}&toStationCode={J.E(i["to"].ToUpperInvariant())}&dateOfJourney={J.E(i["date"])}");
                  return J.Items(d.RootElement, "data", t => new AppItem(J.S(t, "train_number") ?? "", $"{J.S(t, "train_name")} ({J.S(t, "train_number")})", $"{J.S(t, "from_std")} → {J.S(t, "to_std")} · {J.S(t, "duration")}", $"https://www.irctc.co.in/nget/train-search", J.S(t, "train_type"))); } },
            new ViewSpec { Id = "live", Label = "Live train status", Inputs = [new("train", "Train number", Placeholder: "12951"), new("day", "Started", "select", Options: ["0", "1", "2"], Default: "0", Hint: "0 today, 1 yesterday, 2 two days ago")], Load = async (c, i) =>
                { if (!Regex.IsMatch(i["train"], @"^\d{4,5}$")) throw new AppException(400, "A train number has 4 or 5 digits.");
                  using var d = await c.GetAsync($"{IrctcBase}v1/liveTrainStatus?trainNo={i["train"]}&startDay={J.E(i["day"])}");
                  var data = J.At(d.RootElement, "data");
                  return (IReadOnlyList<AppItem>)[new AppItem(i["train"], J.S(data, "train_name") ?? i["train"], J.First(data, "status", "current_location_info[0].readable_message"), null, J.S(data, "current_station_name"))]; } },
            new ViewSpec { Id = "stations", Label = "Find a station code", Inputs = [new("q", "Station name", Placeholder: "mumbai")], Load = async (c, i) =>
                { using var d = await c.GetAsync($"{IrctcBase}v1/searchStation?query={J.E(i["q"])}"); return J.Items(d.RootElement, "data", s => new AppItem(J.S(s, "code") ?? "", J.S(s, "name") ?? "", J.S(s, "state_name"), null, J.S(s, "code"))); } },
        ],
    };

    // ============================================================================================
    // DigiLocker (read-only: the documents issued to the person)
    // ============================================================================================
    private static string DlBase(ProviderCtx c) => c.Server("DigiLocker:BaseUrl") is { Length: > 0 } b ? b.TrimEnd('/') : "https://digilocker.meripehchaan.gov.in";

    private static ProviderApp DigiLocker() => new()
    {
        Key = "digilocker", Name = "DigiLocker", Icon = "mono:DL", Color = "#0B5FFF", Account = "Documents issued to you",
        ConnectTitle = "Connect DigiLocker", ConnectLabel = "Continue with DigiLocker",
        ConnectBlurb = "Sign in to DigiLocker (with Aadhaar or mobile) and Daksha can list the documents issued to you, such as driving licence, PAN and marksheets. Daksha never downloads the documents.",
        WriteLabel = "Full access", WriteHint = "Daksha only lists your issued documents.", Auth = AuthKind.OAuth, HasWrite = false, SetupDoc = "Amit.md#digilocker",
        Ask = Rx("digilocker|documents?|licen[cs]e|marksheet|aadhaar"),
        OAuth = new()
        {
            Section = "DigiLocker", AuthUrl = "https://digilocker.meripehchaan.gov.in/public/oauth2/1/authorize", TokenUrl = "https://digilocker.meripehchaan.gov.in/public/oauth2/1/token", Pkce = true,
        },
        Profile = async c => { using var d = await c.GetAsync($"{DlBase(c)}/public/oauth2/1/user"); var id = J.S(d.RootElement, "digilockerid") ?? throw new AppException(502, "DigiLocker did not return the profile."); return new ProfileInfo(id, J.S(d.RootElement, "name") ?? "DigiLocker"); },
        Views =
        [
            new ViewSpec { Id = "issued", Label = "Issued documents", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync($"{DlBase(c)}/public/oauth2/2/files/issued");
                return J.Items(d.RootElement, "items", f => new AppItem(J.S(f, "uri") ?? J.S(f, "name") ?? "", J.S(f, "name") ?? "Document", $"{J.S(f, "issuer")} · {J.S(f, "description")} · {J.S(f, "date")}", null, J.S(f, "doctype"))); } },
        ],
    };

    // ============================================================================================
    // Google Maps Platform (one server key; nothing to sign in to)
    // ============================================================================================
    private static string Mins(string? duration) => double.TryParse((duration ?? "").TrimEnd('s'), System.Globalization.CultureInfo.InvariantCulture, out var s) ? $"{Math.Max(1, (int)Math.Round(s / 60))} min" : "";

    private static async Task<IReadOnlyList<AppItem>> MapsRouteAsync(ProviderCtx c, string origin, string destination, string mode)
    {
        var traffic = mode is "DRIVE" or "TWO_WHEELER";
        object body = traffic
            ? new { origin = new { address = origin }, destination = new { address = destination }, travelMode = mode, routingPreference = "TRAFFIC_AWARE" }
            : new { origin = new { address = origin }, destination = new { address = destination }, travelMode = mode };
        using var d = await c.PostAsync("https://routes.googleapis.com/directions/v2:computeRoutes", body, new Dictionary<string, string> { ["X-Goog-FieldMask"] = "routes.duration,routes.staticDuration,routes.distanceMeters,routes.description" });
        var url = $"https://www.google.com/maps/dir/?api=1&origin={J.E(origin)}&destination={J.E(destination)}&travelmode={J.E(mode.Replace("TWO_WHEELER", "two-wheeler").ToLowerInvariant().Replace("drive", "driving").Replace("walk", "walking").Replace("bicycle", "bicycling"))}";
        return J.Items(d.RootElement, "routes", r => new AppItem($"{origin}>{destination}", $"{Mins(J.S(r, "duration"))} · {(double.TryParse(J.S(r, "distanceMeters"), out var m) ? (m / 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "?")} km",
            J.First(r, "description") is { } via ? $"{origin} → {destination} · via {via}" : $"{origin} → {destination}", url, traffic ? "with traffic" : mode.ToLowerInvariant()));
    }

    private static ProviderApp GoogleMaps() => new()
    {
        Key = "google-maps-platform", Name = "Google Maps Platform", Icon = "SiGooglemaps", Color = "#4285F4", Account = "Places, directions and commute",
        ConnectTitle = "Turn on Google Maps", ConnectLabel = "Turn on Maps",
        ConnectBlurb = "Search for places, get directions and travel times, and see your commute between home and work. Daksha uses its own Maps key, so there is nothing to sign in to.",
        WriteLabel = "Full access", WriteHint = "Maps is read-only.", Auth = AuthKind.ServerKey, HasWrite = false, SetupDoc = "Amit.md#google-maps-platform",
        ServerKeys = ["GoogleMaps:ApiKey"],
        Ask = Rx("maps?|directions?|route|commute|travel time|how long|nearby|restaurants?|leave"),
        NoBearer = true,
        Decorate = (r, c) => r.Headers.TryAddWithoutValidation("X-Goog-Api-Key", c.Server("GoogleMaps:ApiKey")),
        ExtraSettings = [new("home", "Home address", "text", "Used for your commute tile.", "Bandra West, Mumbai"), new("work", "Work address", "text", "Used for your commute tile.", "Nariman Point, Mumbai")],
        Views =
        [
            new ViewSpec { Id = "commute", Label = "Commute", Dashboard = true, Empty = "Add your home and work addresses in Manage to see your commute.", Load = async (c, _) =>
                { var (home, work) = (c.Setting("home").Trim(), c.Setting("work").Trim()); if (home.Length == 0 || work.Length == 0) return [];
                  var there = await MapsRouteAsync(c, home, work, "DRIVE"); var back = await MapsRouteAsync(c, work, home, "DRIVE");
                  return [.. there.Select(a => a with { Id = "to-work", Subtitle = "Home → Work" }), .. back.Select(a => a with { Id = "to-home", Subtitle = "Work → Home" })]; } },
            new ViewSpec { Id = "places", Label = "Find places", Inputs = [new("q", "What are you looking for?", Placeholder: "coffee near Bandra station")], Load = async (c, i) =>
                { using var d = await c.PostAsync("https://places.googleapis.com/v1/places:searchText", new { textQuery = i["q"], maxResultCount = 12 }, new Dictionary<string, string> { ["X-Goog-FieldMask"] = "places.id,places.displayName,places.formattedAddress,places.rating,places.userRatingCount,places.googleMapsUri,places.currentOpeningHours.openNow" });
                  return J.Items(d.RootElement, "places", p => new AppItem(J.S(p, "id") ?? "", J.S(p, "displayName.text") ?? "Place", $"{J.S(p, "formattedAddress")}{(J.S(p, "currentOpeningHours.openNow") is { } o ? o == "true" ? " · open now" : " · closed now" : "")}", J.S(p, "googleMapsUri"), J.S(p, "rating") is { } r ? $"★ {r} ({J.S(p, "userRatingCount")})" : null)); } },
            new ViewSpec { Id = "directions", Label = "Directions", Inputs = [new("from", "From", Placeholder: "Address or place"), new("to", "To", Placeholder: "Address or place"), new("mode", "Travel by", "select", Options: ["DRIVE", "WALK", "BICYCLE", "TRANSIT", "TWO_WHEELER"], Default: "DRIVE")], Load = (c, i) => MapsRouteAsync(c, i["from"], i["to"], i["mode"]) },
        ],
        Assistant = async c =>
        {
            var (home, work) = (c.Setting("home").Trim(), c.Setting("work").Trim());
            if (home.Length == 0 || work.Length == 0) return "Google Maps is connected, but the person has not set a home and work address, so commute times are unknown.";
            var there = await MapsRouteAsync(c, home, work, "DRIVE");
            return there.Count == 0 ? null : $"Google Maps: driving from home to work takes {there[0].Title} right now ({there[0].Subtitle}).";
        },
    };

    // ============================================================================================
    // Home Assistant (the person's own server: address and long-lived access token)
    // ============================================================================================
    private static readonly string[] HaDevices = ["light", "switch", "fan", "cover", "climate", "lock", "media_player", "vacuum", "humidifier", "water_heater", "input_boolean"];

    private static string HaBase(ProviderCtx c) => c.Cred["url"].TrimEnd('/');

    private static async Task<IReadOnlyList<AppItem>> HaStatesAsync(ProviderCtx c, Func<string, bool> domain, bool withUnit = false)
    {
        using var d = await c.GetAsync($"{HaBase(c)}/api/states");
        return J.Items(d.RootElement, "", s =>
        {
            var id = J.S(s, "entity_id") ?? ""; var dom = id.Split('.')[0];
            if (!domain(dom)) return null;
            var unit = withUnit ? J.S(s, "attributes.unit_of_measurement") : null;
            return new AppItem(id, J.S(s, "attributes.friendly_name") ?? id, id, null, $"{J.S(s, "state")}{(unit is null ? "" : " " + unit)}");
        }, 200);
    }

    private static ProviderApp HomeAssistant() => new()
    {
        Key = "home-assistant", Name = "Home Assistant", Icon = "SiHomeassistant", Color = "#18BCF2", Account = "Lights, switches, sensors and scenes",
        ConnectTitle = "Connect Home Assistant", ConnectLabel = "Connect Home Assistant",
        ConnectBlurb = "Enter your Home Assistant address and a long-lived access token (Profile → Security → Create token). Daksha can then show your devices and sensors. The server running Daksha must be able to reach that address (use Nabu Casa or a tunnel for a cloud server).",
        WriteLabel = "Allow Daksh to control devices", WriteHint = "Full access lets Daksha switch lights and devices on and off, set brightness and activate scenes.",
        Auth = AuthKind.Credentials, WriteNeedsSignIn = false, SetupDoc = "Amit.md#home-assistant",
        Ask = Rx("home assistant|lights?|lamps?|fans?|thermostat|switch(es)?|smart home|sensors?|temperature|scenes?"),
        NoBearer = true,
        Decorate = (r, c) => r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", c.Cred.GetValueOrDefault("token")),
        Credentials = new()
        {
            Fields = [new("url", "Home Assistant address", "url", Placeholder: "http://homeassistant.local:8123"), new("token", "Long-lived access token", "secret")],
            Prepare = async (v, ct) => { var uri = await HostGuard.CheckAsync(v["url"].Contains("://") ? v["url"] : "http://" + v["url"], allowHttp: true, ct); v["url"] = uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath.TrimEnd('/'); },
            Probe = async c => { using var d = await c.GetAsync($"{HaBase(c)}/api/config"); var name = J.S(d.RootElement, "location_name") ?? "Home"; return new ProfileInfo(new Uri(c.Cred["url"]).Host, $"{name} (Home Assistant {J.S(d.RootElement, "version")})"); },
        },
        Views =
        [
            new ViewSpec { Id = "devices", Label = "Devices", Dashboard = true, Load = (c, _) => HaStatesAsync(c, d => HaDevices.Contains(d)) },
            new ViewSpec { Id = "sensors", Label = "Sensors", Dashboard = true, Load = (c, _) => HaStatesAsync(c, d => d is "sensor" or "binary_sensor", withUnit: true) },
            new ViewSpec { Id = "scenes", Label = "Scenes and automations", Load = (c, _) => HaStatesAsync(c, d => d is "scene" or "script" or "automation") },
        ],
        Actions =
        [
            new ActionSpec { Id = "switch", Label = "Turn a device on or off", Inputs = [new("entity", "Device", "select-view", OptionsView: "devices"), new("command", "Do", "select", Options: ["turn_on", "turn_off", "toggle"], Default: "turn_on")],
                Run = async (c, i) => { var domain = i["entity"].Split('.')[0]; if (!HaDevices.Contains(domain)) throw new AppException(400, "Choose a device from the list.");
                    var service = (domain, i["command"]) switch { ("cover", "turn_on") => "cover/open_cover", ("cover", "turn_off") => "cover/close_cover", ("lock", "turn_on") => "lock/lock", ("lock", "turn_off") => "lock/unlock", _ => $"homeassistant/{i["command"]}" };
                    using var _ = await c.PostAsync($"{HaBase(c)}/api/services/{service}", new { entity_id = i["entity"] }); return new ActionResult("Done."); } },
            new ActionSpec { Id = "brightness", Label = "Set a light's brightness", Inputs = [new("entity", "Light", "select-view", OptionsView: "devices"), new("percent", "Brightness (1–100)", "number", Default: "80")],
                Run = async (c, i) => { if (!i["entity"].StartsWith("light.", StringComparison.Ordinal) || !int.TryParse(i["percent"], out var p) || p is < 1 or > 100) throw new AppException(400, "Choose a light and a brightness from 1 to 100.");
                    using var _ = await c.PostAsync($"{HaBase(c)}/api/services/light/turn_on", new { entity_id = i["entity"], brightness_pct = p }); return new ActionResult("Brightness set."); } },
            new ActionSpec { Id = "activate", Label = "Activate a scene, script or automation", Inputs = [new("entity", "Scene or script", "select-view", OptionsView: "scenes")],
                Run = async (c, i) => { var domain = i["entity"].Split('.')[0]; var service = domain switch { "scene" => "scene/turn_on", "script" => "script/turn_on", "automation" => "automation/trigger", _ => throw new AppException(400, "Choose a scene, script or automation.") };
                    using var _ = await c.PostAsync($"{HaBase(c)}/api/services/{service}", new { entity_id = i["entity"] }); return new ActionResult("Activated."); } },
        ],
    };
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

// Batch 2, part 2: payments (Razorpay, Stripe, PayPal), mail (Zoho Mail), meetings (Calendly, Zoom) and files (Dropbox, Box).

public static partial class ProviderApps
{
    private static readonly HashSet<string> ZeroDecimal = ["JPY", "KRW", "VND", "CLP", "ISK", "UGX", "XAF", "XOF", "PYG", "RWF"];

    /// <summary>A minor-unit amount (paise, cents) as text with its currency: 49900 + INR → "499.00 INR".</summary>
    private static string Minor(string? amount, string? currency)
    {
        if (!long.TryParse(amount, out var n)) return "";
        var cur = (currency ?? "").ToUpperInvariant();
        var value = ZeroDecimal.Contains(cur) ? n.ToString("N0", CultureInfo.InvariantCulture) : (n / 100m).ToString("N2", CultureInfo.InvariantCulture);
        return $"{value} {cur}".Trim();
    }

    private static long ToMinor(string text, string currency)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 100_000_000) throw new AppException(400, "Enter the amount as a number above zero.");
        return ZeroDecimal.Contains(currency.ToUpperInvariant()) ? (long)Math.Round(v) : (long)Math.Round(v * 100m);
    }

    /// <summary>Converts a date and time typed in the person's own time zone (Manage → Preferences) to UTC.</summary>
    private static DateTime LocalToUtc(ProviderCtx c, string local)
    {
        var zone = TimeZoneInfo.Utc;
        try { if (c.Setting("timeZone").Trim() is { Length: > 0 } tz) zone = TimeZoneInfo.FindSystemTimeZoneById(tz); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new AppException(400, "Your time zone in Manage is not valid. Use a name like Asia/Kolkata."); }
        if (!DateTime.TryParse(local, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) throw new AppException(400, "Enter a valid date and time.");
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone);
    }

    private static readonly SettingRowSpec TimeZoneSetting = new("timeZone", "My time zone", "text", "Used when Daksha schedules something for you, for example Asia/Kolkata.", "Asia/Kolkata", null, "Asia/Kolkata");

    // ============================================================================================
    // Razorpay
    // ============================================================================================
    private const string RzpApi = "https://api.razorpay.com/v1/";

    private static ProviderApp Razorpay() => new()
    {
        Key = "razorpay", Name = "Razorpay", Icon = "SiRazorpay", Color = "#0C2451", Account = "Payments, orders and settlements",
        ConnectTitle = "Connect Razorpay", ConnectLabel = "Connect Razorpay",
        ConnectBlurb = "Enter your Razorpay API key id and secret (Dashboard → Account & Settings → API keys). Use a test key to try it safely.",
        WriteLabel = "Allow Daksh to create payment links", WriteHint = "Full access lets Daksha create payment links.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#razorpay",
        Ask = Rx("razorpay|payments?|settlements?|payment links?"),
        NoBearer = true, Decorate = BasicFromCred("keyId", "keySecret"),
        Setup = ["Nothing to set on the server. Each person generates an **API key** in the Razorpay Dashboard (Account & Settings → API keys) and enters the key id and secret. Test-mode keys (`rzp_test_…`) are safe to try."],
        Notes = "Amounts are shown in rupees. Daksha never stores the secret in plain text.",
        Credentials = new()
        {
            Fields = [F("keyId", "Key id", placeholder: "rzp_live_…"), F("keySecret", "Key secret", "secret")],
            Prepare = (v, _) => Regex.IsMatch(v["keyId"], @"^rzp_(live|test)_[A-Za-z0-9]{8,30}$") ? Task.CompletedTask : throw new AppException(400, "A Razorpay key id looks like rzp_live_… or rzp_test_…"),
            Probe = async c => { using var d = await c.GetAsync(RzpApi + "payments?count=1"); return new ProfileInfo(c.Cred["keyId"], c.Cred["keyId"].Contains("_test_") ? "Razorpay (test mode)" : "Razorpay (live)"); },
        },
        Views =
        [
            new ViewSpec { Id = "payments", Label = "Payments", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(RzpApi + "payments?count=25");
                return J.Items(d.RootElement, "items", p => new AppItem(J.S(p, "id") ?? "", Minor(J.S(p, "amount"), J.S(p, "currency")), $"{J.S(p, "method")} · {J.S(p, "email")} · {J.When(J.S(p, "created_at"))}", $"https://dashboard.razorpay.com/app/payments/{J.S(p, "id")}", J.S(p, "status"))); } },
            new ViewSpec { Id = "orders", Label = "Orders", Load = async (c, _) => { using var d = await c.GetAsync(RzpApi + "orders?count=25");
                return J.Items(d.RootElement, "items", o => new AppItem(J.S(o, "id") ?? "", Minor(J.S(o, "amount"), J.S(o, "currency")), $"{J.S(o, "receipt")} · {J.When(J.S(o, "created_at"))}", $"https://dashboard.razorpay.com/app/orders/{J.S(o, "id")}", J.S(o, "status"))); } },
            new ViewSpec { Id = "settlements", Label = "Settlements", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(RzpApi + "settlements?count=10");
                return J.Items(d.RootElement, "items", s => new AppItem(J.S(s, "id") ?? "", Minor(J.S(s, "amount"), "INR"), J.When(J.S(s, "created_at")), null, J.S(s, "status"))); } },
            new ViewSpec { Id = "links", Label = "Payment links", Load = async (c, _) => { using var d = await c.GetAsync(RzpApi + "payment_links?count=25");
                return J.Items(d.RootElement, "payment_links", l => new AppItem(J.S(l, "id") ?? "", $"{Minor(J.S(l, "amount"), J.S(l, "currency"))} · {J.Clip(J.S(l, "description"), 60)}", J.S(l, "short_url"), J.S(l, "short_url"), J.S(l, "status"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "payment-link", Label = "Create a payment link", Inputs = [F("amount", "Amount (₹)", "number", "499.00"), F("description", "What is it for?"), F("name", "Customer name", required: false), F("email", "Customer email", required: false)],
                Run = async (c, i) => { var paise = ToMinor(i["amount"], "INR");
                    using var d = await c.PostAsync(RzpApi + "payment_links", new { amount = paise, currency = "INR", description = i["description"], customer = new { name = i["name"], email = i["email"] } });
                    return new ActionResult("Payment link created.", J.S(d.RootElement, "short_url")); } },
        ],
    };

    // ============================================================================================
    // Stripe
    // ============================================================================================
    private const string StApi = "https://api.stripe.com/v1/";

    private static async Task<JsonDocument> StGet(ProviderCtx c, string path) { c.Token = c.Cred["key"]; return await c.GetAsync(StApi + path); }
    private static async Task<JsonDocument> StPost(ProviderCtx c, string path, Dictionary<string, string> form) { c.Token = c.Cred["key"]; return await c.PostFormAsync(StApi + path, form); }

    private static ProviderApp Stripe() => new()
    {
        Key = "stripe", Name = "Stripe", Icon = "SiStripe", Color = "#635BFF", Account = "Balance, payments, customers and invoices",
        ConnectTitle = "Connect Stripe", ConnectLabel = "Connect Stripe",
        ConnectBlurb = "Create a restricted key (Developers → API keys → Create restricted key) with read access, and paste it here. Use a test key to try it safely.",
        WriteLabel = "Allow Daksh to create customers and payment links", WriteHint = "Full access lets Daksha create customers and payment links (the key needs write access for those).",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#stripe",
        Ask = Rx("stripe|balance|charges?|payouts?|invoices?|customers?"),
        Setup = ["Nothing to set on the server. Each person creates a **restricted API key** in Stripe (Developers → API keys) with read access to Balance, Charges, Customers, Invoices and Payouts (and write for Customers, Products, Prices and Payment Links to use full access). Test keys (`rk_test_…`) are safe to try."],
        Credentials = new()
        {
            Fields = [F("key", "Secret or restricted key", "secret", "rk_live_… or sk_test_…")],
            Prepare = (v, _) => Regex.IsMatch(v["key"], @"^(sk|rk)_(live|test)_[A-Za-z0-9]{16,}$") ? Task.CompletedTask : throw new AppException(400, "A Stripe key starts with sk_ or rk_ followed by live_ or test_."),
            Probe = async c => { using var d = await StGet(c, "balance"); var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c.Cred["key"])))[..12]; return new ProfileInfo(id, c.Cred["key"].Contains("_test_") ? "Stripe (test mode)" : "Stripe (live)"); },
        },
        Views =
        [
            new ViewSpec { Id = "balance", Label = "Balance", Dashboard = true, Load = async (c, _) => { using var d = await StGet(c, "balance");
                return [.. J.Arr(d.RootElement, "available").Select(b => new AppItem("a-" + J.S(b, "currency"), "Available", Minor(J.S(b, "amount"), J.S(b, "currency")), null, "available")), .. J.Arr(d.RootElement, "pending").Select(b => new AppItem("p-" + J.S(b, "currency"), "Pending", Minor(J.S(b, "amount"), J.S(b, "currency")), null, "pending"))]; } },
            new ViewSpec { Id = "charges", Label = "Payments", Dashboard = true, Load = async (c, _) => { using var d = await StGet(c, "charges?limit=25");
                return J.Items(d.RootElement, "data", x => new AppItem(J.S(x, "id") ?? "", Minor(J.S(x, "amount"), J.S(x, "currency")), $"{J.First(x, "billing_details.email", "receipt_email", "description")} · {J.When(J.S(x, "created"))}", J.S(x, "receipt_url"), J.S(x, "status"))); } },
            new ViewSpec { Id = "customers", Label = "Customers", Load = async (c, _) => { using var d = await StGet(c, "customers?limit=25");
                return J.Items(d.RootElement, "data", x => new AppItem(J.S(x, "id") ?? "", J.First(x, "name", "email") ?? "Customer", $"{J.S(x, "email")} · {J.When(J.S(x, "created"))}", $"https://dashboard.stripe.com/customers/{J.S(x, "id")}")); } },
            new ViewSpec { Id = "invoices", Label = "Invoices", Load = async (c, _) => { using var d = await StGet(c, "invoices?limit=25");
                return J.Items(d.RootElement, "data", x => new AppItem(J.S(x, "id") ?? "", $"{J.S(x, "number")} · {Minor(J.S(x, "total"), J.S(x, "currency"))}", $"{J.S(x, "customer_email")} · {J.When(J.S(x, "created"))}", J.S(x, "hosted_invoice_url"), J.S(x, "status"))); } },
            new ViewSpec { Id = "payouts", Label = "Payouts", Load = async (c, _) => { using var d = await StGet(c, "payouts?limit=10");
                return J.Items(d.RootElement, "data", x => new AppItem(J.S(x, "id") ?? "", Minor(J.S(x, "amount"), J.S(x, "currency")), $"Arrives {J.When(J.S(x, "arrival_date"))}", null, J.S(x, "status"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "customer", Label = "Create a customer", Inputs = [F("name", "Name"), F("email", "Email", required: false)],
                Run = async (c, i) => { using var d = await StPost(c, "customers", new() { ["name"] = i["name"], ["email"] = i["email"] }); return new ActionResult("Customer created.", $"https://dashboard.stripe.com/customers/{J.S(d.RootElement, "id")}"); } },
            new ActionSpec { Id = "payment-link", Label = "Create a payment link", Inputs = [F("name", "What is it for?"), F("amount", "Amount", "number", "499.00"), new("currency", "Currency", "select", null, true, ["inr", "usd", "eur", "gbp", "aed", "sgd"], null, "inr")],
                Run = async (c, i) => { var minor = ToMinor(i["amount"], i["currency"]);
                    using var product = await StPost(c, "products", new() { ["name"] = i["name"] });
                    using var price = await StPost(c, "prices", new() { ["unit_amount"] = minor.ToString(CultureInfo.InvariantCulture), ["currency"] = i["currency"], ["product"] = J.S(product.RootElement, "id") ?? "" });
                    using var link = await StPost(c, "payment_links", new() { ["line_items[0][price]"] = J.S(price.RootElement, "id") ?? "", ["line_items[0][quantity]"] = "1" });
                    return new ActionResult("Payment link created.", J.S(link.RootElement, "url")); } },
        ],
    };

    // ============================================================================================
    // PayPal (read-only: transactions and balances, with REST app credentials)
    // ============================================================================================
    private static string PpBase(ProviderCtx c) => c.Cred.GetValueOrDefault("mode") == "sandbox" ? "https://api-m.sandbox.paypal.com" : "https://api-m.paypal.com";

    private static async Task PpLoginAsync(ProviderCtx c)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, PpBase(c) + "/v1/oauth2/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Cred["clientId"]}:{c.Cred["secret"]}")));
        using var res = await c.Clients.CreateClient("provider-apps").SendAsync(req, c.Ct);
        if (!res.IsSuccessStatusCode) throw new AppException(409, "PayPal did not accept those credentials. Check the client id, secret and mode (live or sandbox).", "reconnect_required");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(c.Ct));
        c.Token = J.S(doc.RootElement, "access_token") ?? throw new AppException(502, "PayPal did not return a token.");
    }

    private static ProviderApp PayPal() => new()
    {
        Key = "paypal", Name = "PayPal", Icon = "SiPaypal", Color = "#002991", Account = "Transactions and balances",
        ConnectTitle = "Connect PayPal", ConnectLabel = "Connect PayPal",
        ConnectBlurb = "Create a REST app (developer.paypal.com → Apps & Credentials) and enter its client id and secret. Daksha reads recent transactions and balances.",
        WriteLabel = "Full access", WriteHint = "Daksha only reads.", Auth = AuthKind.Credentials, HasWrite = false, SetupDoc = "Amit.md#paypal",
        Ask = Rx("paypal"),
        Setup = ["Nothing to set on the server. Each person creates a **REST app** at developer.paypal.com → Apps & Credentials, enables the **Transaction search** feature, and enters the client id and secret (choose Sandbox to try it)."],
        Notes = "PayPal only returns transactions from the last 31 days per request, and they can take a few hours to appear.",
        Credentials = new()
        {
            Fields = [F("clientId", "Client id"), F("secret", "Secret", "secret"), new("mode", "Mode", "select", null, true, ["live", "sandbox"], null, "live")],
            Probe = async c => { await PpLoginAsync(c); return new ProfileInfo($"{c.Cred["mode"]}-{c.Cred["clientId"][..Math.Min(10, c.Cred["clientId"].Length)]}", $"PayPal ({c.Cred["mode"]})"); },
        },
        Views =
        [
            new ViewSpec { Id = "transactions", Label = "Last 30 days", Dashboard = true, Load = async (c, _) => { await PpLoginAsync(c);
                var from = DateTimeOffset.UtcNow.AddDays(-30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); var to = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                using var d = await c.GetAsync(PpBase(c) + $"/v1/reporting/transactions?start_date={J.E(from)}&end_date={J.E(to)}&fields=all&page_size=25");
                return J.Items(d.RootElement, "transaction_details", t => new AppItem(J.S(t, "transaction_info.transaction_id") ?? "", $"{J.S(t, "transaction_info.transaction_amount.value")} {J.S(t, "transaction_info.transaction_amount.currency_code")}",
                    $"{J.First(t, "payer_info.email_address", "transaction_info.transaction_subject", "transaction_info.transaction_note")} · {J.When(J.S(t, "transaction_info.transaction_initiation_date"))}", null, J.S(t, "transaction_info.transaction_status"))); } },
            new ViewSpec { Id = "balances", Label = "Balances", Dashboard = true, Load = async (c, _) => { await PpLoginAsync(c); using var d = await c.GetAsync(PpBase(c) + "/v1/reporting/balances");
                return J.Items(d.RootElement, "balances", b => new AppItem(J.S(b, "currency") ?? "", J.S(b, "currency") ?? "Balance", $"Available {J.S(b, "available_balance.value")}", null, J.S(b, "total_balance.value"))); } },
        ],
    };

    // ============================================================================================
    // Zoho Mail (one host per country: ZohoMail:Region, for example in, com, eu, au, jp)
    // ============================================================================================
    private static string ZmBase(ProviderCtx c) => $"https://mail.zoho.{(c.Server("ZohoMail:Region") is { Length: > 0 } r && Regex.IsMatch(r, "^[a-z.]{2,12}$") ? r : "com")}/api/";

    private static async Task<string> ZmInboxFolderAsync(ProviderCtx c)
    {
        using var d = await c.GetAsync(ZmBase(c) + $"accounts/{J.E(c.Account.AccountId)}/folders");
        return J.Arr(d.RootElement, "data").Where(f => string.Equals(J.S(f, "folderType"), "Inbox", StringComparison.OrdinalIgnoreCase)).Select(f => J.S(f, "folderId")).FirstOrDefault() ?? throw new AppException(404, "Zoho did not list an Inbox folder.");
    }

    private static ProviderApp ZohoMail() => new()
    {
        Key = "zoho-mail", Name = "Zoho Mail", Icon = "SiZoho", Color = "#E42527", Account = "Inbox and sending",
        ConnectTitle = "Connect Zoho Mail", ConnectLabel = "Continue with Zoho",
        ConnectBlurb = "Sign in with Zoho and Daksha can show your inbox. It never sees your Zoho password.",
        WriteLabel = "Allow Daksh to send email", WriteHint = "Full access lets Daksha send email from your Zoho address.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#zoho-mail",
        Ask = Rx("zoho mail|zoho|emails?|inbox|mails?"),
        NoBearer = true, Decorate = (r, c) => { if (c.Token is not null) r.Headers.TryAddWithoutValidation("Authorization", "Zoho-oauthtoken " + c.Token); },
        Setup = ["Open the Zoho API Console for your data centre (`api-console.zoho.in` for India, `.com`, `.eu`, `.com.au`, `.jp`) and create a **Server-based Application**. Copy the client id and secret.", "Set `ZohoMail__Region` to your data centre suffix (`in`, `com`, `eu`, `com.au`, `jp`)."],
        Notes = "The Zoho account's data centre decides the region; mail for an India account lives on zoho.in.",
        OAuth = new()
        {
            Section = "ZohoMail", AuthUrl = "https://accounts.zoho.{region}/oauth/v2/auth", TokenUrl = "https://accounts.zoho.{region}/oauth/v2/token", ScopeSeparator = ",", DefaultRegion = "com",
            ReadScopes = ["ZohoMail.accounts.READ", "ZohoMail.folders.READ", "ZohoMail.messages.READ"], WriteScopes = ["ZohoMail.messages.CREATE"],
            AuthExtra = new() { ["access_type"] = "offline", ["prompt"] = "consent" },
        },
        Profile = async c => { using var d = await c.GetAsync(ZmBase(c) + "accounts"); var a = J.Arr(d.RootElement, "data").FirstOrDefault(); if (a.ValueKind != JsonValueKind.Object) throw new AppException(502, "Zoho did not return a mail account.");
            return new ProfileInfo(J.S(a, "accountId") ?? "", J.S(a, "primaryEmailAddress") ?? "Zoho Mail", new() { ["email"] = J.S(a, "primaryEmailAddress") ?? "" }); },
        Views =
        [
            new ViewSpec { Id = "inbox", Label = "Inbox", Dashboard = true, Load = async (c, _) => { var folder = await ZmInboxFolderAsync(c);
                using var d = await c.GetAsync(ZmBase(c) + $"accounts/{J.E(c.Account.AccountId)}/messages/view?folderId={J.E(folder)}&limit=25");
                return J.Items(d.RootElement, "data", m => new AppItem($"{folder}/{J.S(m, "messageId")}", J.S(m, "subject") is { Length: > 0 } s ? s : "(no subject)", $"{J.S(m, "sender")} · {J.When(J.S(m, "receivedTime") is { Length: > 3 } ms ? (long.Parse(ms) / 1000).ToString() : null)}", null, J.S(m, "status") == "0" ? "unread" : null, null, "message")); } },
            new ViewSpec { Id = "message", Label = "Message", Hidden = true, TakesArg = true, Load = async (c, i) => { var p = i["arg"].Split('/'); if (p.Length != 2 || !NumericId.IsMatch(p[0]) || !NumericId.IsMatch(p[1])) throw new AppException(400, "That message is not valid.");
                using var d = await c.GetAsync(ZmBase(c) + $"accounts/{J.E(c.Account.AccountId)}/folders/{p[0]}/messages/{p[1]}/content");
                var text = WebUtilityDecode(Regex.Replace(Regex.Replace(J.S(d.RootElement, "data.content") ?? "", @"<(br|/p|/div)\s*/?>", "\n"), "<[^>]+>", ""));
                return [new AppItem(p[1], "Message", null, null, null, null, null, J.Clip(Regex.Replace(text, @"\n{3,}", "\n\n").Trim(), 6000))]; } },
        ],
        Actions =
        [
            new ActionSpec { Id = "send", Label = "Send an email", Inputs = [F("to", "To", placeholder: "name@example.com"), F("subject", "Subject"), F("body", "Message", "textarea")],
                Run = async (c, i) => { if (!Regex.IsMatch(i["to"], @"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$")) throw new AppException(400, "Enter one valid email address.");
                    using var _ = await c.PostAsync(ZmBase(c) + $"accounts/{J.E(c.Account.AccountId)}/messages", new { fromAddress = c.Extra("email"), toAddress = i["to"], subject = i["subject"], content = i["body"], mailFormat = "plaintext" }); return new ActionResult($"Sent to {i["to"]}."); } },
        ],
    };

    private static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

    // ============================================================================================
    // Calendly (personal access token)
    // ============================================================================================
    private const string CalApi = "https://api.calendly.com/";

    private static ProviderApp Calendly() => new()
    {
        Key = "calendly", Name = "Calendly", Icon = "SiCalendly", Color = "#006BFF", Account = "Booked meetings and event types",
        ConnectTitle = "Connect Calendly", ConnectLabel = "Connect Calendly",
        ConnectBlurb = "Create a personal access token (Integrations → API & Webhooks) and paste it here.",
        WriteLabel = "Allow Daksh to cancel meetings", WriteHint = "Full access lets Daksha cancel a booked meeting.",
        Auth = AuthKind.Credentials, SetupDoc = "Amit.md#calendly",
        Ask = Rx("calendly|booked|bookings?|meetings?"),
        Setup = ["Nothing to set on the server. Each person creates a **personal access token** in Calendly (Integrations → API & Webhooks) and pastes it into Daksha."],
        Credentials = new()
        {
            Fields = [F("token", "Personal access token", "secret")],
            Probe = async c => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(CalApi + "users/me"); var uri = J.S(d.RootElement, "resource.uri") ?? throw new AppException(502, "Calendly did not return the account.");
                return new ProfileInfo(uri.Split('/').Last(), J.First(d.RootElement, "resource.name", "resource.email") ?? "Calendly", new() { ["uri"] = uri }); },
        },
        Views =
        [
            new ViewSpec { Id = "upcoming", Label = "Upcoming meetings", Dashboard = true, Load = async (c, _) => { c.Token = c.Cred["token"];
                using var d = await c.GetAsync(CalApi + $"scheduled_events?user={J.E(c.Extra("uri"))}&status=active&sort=start_time:asc&count=25&min_start_time={J.E(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}");
                return J.Items(d.RootElement, "collection", e => new AppItem((J.S(e, "uri") ?? "").Split('/').Last(), J.S(e, "name") ?? "Meeting", $"{(DateTimeOffset.TryParse(J.S(e, "start_time"), out var t) ? t.UtcDateTime.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) + " UTC" : "")} · {J.S(e, "location.type")?.Replace('_', ' ')}", J.S(e, "location.join_url"))); } },
            new ViewSpec { Id = "types", Label = "Event types", Load = async (c, _) => { c.Token = c.Cred["token"]; using var d = await c.GetAsync(CalApi + $"event_types?user={J.E(c.Extra("uri"))}&active=true&count=25");
                return J.Items(d.RootElement, "collection", e => new AppItem(J.S(e, "uri") ?? "", J.S(e, "name") ?? "Event type", $"{J.S(e, "duration")} min", J.S(e, "scheduling_url"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "cancel", Label = "Cancel a meeting", Inputs = [new("event", "Meeting", "select-view", null, true, null, "upcoming"), F("reason", "Reason", required: false)],
                Run = async (c, i) => { if (!Regex.IsMatch(i["event"], @"^[\w\-]{8,64}$")) throw new AppException(400, "Choose a meeting."); c.Token = c.Cred["token"];
                    using var _ = await c.PostAsync(CalApi + $"scheduled_events/{i["event"]}/cancellation", new { reason = i["reason"] }); return new ActionResult("Meeting cancelled."); } },
        ],
    };

    // ============================================================================================
    // Zoom
    // ============================================================================================
    private const string ZoomApi = "https://api.zoom.us/v2/";

    private static ProviderApp Zoom() => new()
    {
        Key = "zoom", Name = "Zoom", Icon = "SiZoom", Color = "#0B5CFF", Account = "Meetings and recordings",
        ConnectTitle = "Connect Zoom", ConnectLabel = "Continue with Zoom",
        ConnectBlurb = "Sign in with Zoom and Daksha can show your upcoming meetings and recordings. It never sees your Zoom password.",
        WriteLabel = "Allow Daksh to schedule meetings", WriteHint = "Full access lets Daksha schedule Zoom meetings.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#zoom",
        Ask = Rx("zoom|zoom meetings?|recordings?"),
        ExtraSettings = [TimeZoneSetting],
        Setup = ["Zoom App Marketplace → Develop → **Build App** → General app (user-managed OAuth). Copy the client id and secret.", "Add the scopes `meeting:read`, `user:read`, `recording:read` (read) and `meeting:write` (full access)."],
        OAuth = new()
        {
            Section = "Zoom", AuthUrl = "https://zoom.us/oauth/authorize", TokenUrl = "https://zoom.us/oauth/token", BasicAuth = true,
            ReadScopes = ["meeting:read", "user:read", "recording:read"], WriteScopes = ["meeting:write"],
        },
        Profile = async c => { using var d = await c.GetAsync(ZoomApi + "users/me"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.First(d.RootElement, "display_name", "email") ?? "Zoom"); },
        Views =
        [
            new ViewSpec { Id = "upcoming", Label = "Upcoming meetings", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(ZoomApi + "users/me/meetings?type=upcoming&page_size=25");
                return J.Items(d.RootElement, "meetings", m => new AppItem(J.S(m, "id") ?? "", J.S(m, "topic") ?? "Meeting", $"{(DateTimeOffset.TryParse(J.S(m, "start_time"), out var t) ? t.UtcDateTime.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) + " UTC" : "")} · {J.S(m, "duration")} min", J.S(m, "join_url"))); } },
            new ViewSpec { Id = "recordings", Label = "Recordings", Load = async (c, _) => { using var d = await c.GetAsync(ZoomApi + $"users/me/recordings?from={DateTime.UtcNow.AddDays(-30):yyyy-MM-dd}&page_size=25");
                return J.Items(d.RootElement, "meetings", m => new AppItem(J.S(m, "uuid") ?? "", J.S(m, "topic") ?? "Recording", $"{J.When(J.S(m, "start_time"))} · {(long.TryParse(J.S(m, "total_size"), out var b) ? b / 1_048_576 + " MB" : "")}", J.S(m, "share_url"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "schedule", Label = "Schedule a meeting", Inputs = [F("topic", "Topic"), F("start", "Starts", "datetime"), F("minutes", "Length (minutes)", "number", def: "30")],
                Run = async (c, i) => { if (!int.TryParse(i["minutes"], out var m) || m is < 5 or > 1440) throw new AppException(400, "Enter a length between 5 and 1,440 minutes.");
                    var start = LocalToUtc(c, i["start"]); using var d = await c.PostAsync(ZoomApi + "users/me/meetings", new { topic = i["topic"], type = 2, start_time = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), duration = m, timezone = "UTC" });
                    return new ActionResult("Meeting scheduled.", J.S(d.RootElement, "join_url")); } },
        ],
    };

    // ============================================================================================
    // Dropbox
    // ============================================================================================
    private const string DbxApi = "https://api.dropboxapi.com/2/";

    private static string? DbxTag(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(".tag", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static AppItem DbxEntry(JsonElement e)
    {
        var folder = DbxTag(e) == "folder";
        var path = J.S(e, "path_lower") ?? "";
        return new AppItem(path, J.S(e, "name") ?? "File", folder ? "Folder" : $"{(long.TryParse(J.S(e, "size"), out var b) ? (b < 1_048_576 ? $"{Math.Max(1, b / 1024)} KB" : $"{b / 1_048_576} MB") : "")} · {J.When(J.S(e, "client_modified"))}",
            folder ? null : "https://www.dropbox.com/home" + Uri.EscapeDataString(J.S(e, "path_display") ?? "").Replace("%2F", "/"), folder ? "folder" : null, null, folder ? "folder" : null);
    }

    private static ProviderApp Dropbox() => new()
    {
        Key = "dropbox", Name = "Dropbox", Icon = "SiDropbox", Color = "#0061FF", Account = "Files and folders",
        ConnectTitle = "Connect Dropbox", ConnectLabel = "Continue with Dropbox",
        ConnectBlurb = "Sign in with Dropbox and Daksha can browse and search your files. It never sees your Dropbox password.",
        WriteLabel = "Allow Daksh to create folders", WriteHint = "Full access lets Daksha create folders.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = true, SetupDoc = "Amit.md#dropbox",
        Ask = Rx("dropbox"),
        Setup = ["Dropbox App Console → **Create app** → Scoped access → Full Dropbox. Copy the app key (client id) and app secret.", "Permissions tab: enable `account_info.read`, `files.metadata.read` and `files.content.write`, then Submit."],
        OAuth = new()
        {
            Section = "Dropbox", AuthUrl = "https://www.dropbox.com/oauth2/authorize", TokenUrl = "https://api.dropboxapi.com/oauth2/token", Pkce = true, SecretOptional = true,
            ReadScopes = ["account_info.read", "files.metadata.read"], WriteScopes = ["files.content.write"], AuthExtra = new() { ["token_access_type"] = "offline" },
        },
        Profile = async c => { using var d = await c.JsonAsync(HttpMethod.Post, DbxApi + "users/get_current_account"); return new ProfileInfo(J.S(d.RootElement, "account_id") ?? "", J.First(d.RootElement, "name.display_name", "email") ?? "Dropbox"); },
        Views =
        [
            new ViewSpec { Id = "files", Label = "Files", Dashboard = true, Load = async (c, _) => { using var d = await c.PostAsync(DbxApi + "files/list_folder", new { path = "", limit = 25 }); return J.Items(d.RootElement, "entries", DbxEntry); } },
            new ViewSpec { Id = "folder", Label = "Folder", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!i["arg"].StartsWith('/') || i["arg"].Contains("..")) throw new AppException(400, "That folder is not valid.");
                using var d = await c.PostAsync(DbxApi + "files/list_folder", new { path = i["arg"], limit = 50 }); return J.Items(d.RootElement, "entries", DbxEntry); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search Dropbox")], Load = async (c, i) => { using var d = await c.PostAsync(DbxApi + "files/search_v2", new { query = i["q"], options = new { max_results = 20 } });
                return J.Items(d.RootElement, "matches", m => DbxEntry(J.At(m, "metadata.metadata"))); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create-folder", Label = "Create a folder", Inputs = [F("path", "Folder path", placeholder: "/Projects/New folder")],
                Run = async (c, i) => { if (!i["path"].StartsWith('/') || i["path"].Contains("..") || i["path"].Length > 300) throw new AppException(400, "Start the path with / and avoid “..”.");
                    using var _ = await c.PostAsync(DbxApi + "files/create_folder_v2", new { path = i["path"], autorename = false }); return new ActionResult("Folder created."); } },
        ],
    };

    // ============================================================================================
    // Box
    // ============================================================================================
    private const string BoxApi = "https://api.box.com/2.0/";

    private static AppItem BoxEntry(JsonElement e)
    {
        var folder = J.S(e, "type") == "folder";
        return new AppItem(J.S(e, "id") ?? "", J.S(e, "name") ?? "Item", folder ? "Folder" : $"{(long.TryParse(J.S(e, "size"), out var b) ? (b < 1_048_576 ? $"{Math.Max(1, b / 1024)} KB" : $"{b / 1_048_576} MB") : "")} · {J.When(J.S(e, "modified_at"))}",
            folder ? null : $"https://app.box.com/file/{J.S(e, "id")}", folder ? "folder" : null, null, folder ? "folder" : null);
    }

    private static ProviderApp Box() => new()
    {
        Key = "box", Name = "Box", Icon = "SiBox", Color = "#0061D5", Account = "Files and folders",
        ConnectTitle = "Connect Box", ConnectLabel = "Continue with Box",
        ConnectBlurb = "Sign in with Box and Daksha can browse and search your files. It never sees your Box password.",
        WriteLabel = "Allow Daksh to create folders", WriteHint = "Box sets permissions on the app itself, so this is Daksha's own switch: it only creates folders when it is on.",
        Auth = AuthKind.OAuth, WriteNeedsSignIn = false, SetupDoc = "Amit.md#box",
        Ask = Rx("box|box\\.com"),
        Setup = ["Box Developer Console → **Create New App** → Custom App → User Authentication (OAuth 2.0). Copy the client id and secret.", "Configuration tab: set the scopes to read and write all files and folders (Daksha only writes when full access is on)."],
        OAuth = new() { Section = "Box", AuthUrl = "https://account.box.com/api/oauth2/authorize", TokenUrl = "https://api.box.com/oauth2/token" },
        Profile = async c => { using var d = await c.GetAsync(BoxApi + "users/me"); return new ProfileInfo(J.S(d.RootElement, "id") ?? "", J.First(d.RootElement, "name", "login") ?? "Box"); },
        Views =
        [
            new ViewSpec { Id = "files", Label = "Files", Dashboard = true, Load = async (c, _) => { using var d = await c.GetAsync(BoxApi + "folders/0/items?limit=25&fields=id,type,name,modified_at,size"); return J.Items(d.RootElement, "entries", BoxEntry); } },
            new ViewSpec { Id = "folder", Label = "Folder", Hidden = true, TakesArg = true, Load = async (c, i) => { if (!NumericId.IsMatch(i["arg"])) throw new AppException(400, "That folder is not valid.");
                using var d = await c.GetAsync(BoxApi + $"folders/{i["arg"]}/items?limit=50&fields=id,type,name,modified_at,size"); return J.Items(d.RootElement, "entries", BoxEntry); } },
            new ViewSpec { Id = "search", Label = "Search", Inputs = [F("q", "Search Box")], Load = async (c, i) => { using var d = await c.GetAsync(BoxApi + $"search?query={J.E(i["q"])}&limit=20&fields=id,type,name,modified_at,size"); return J.Items(d.RootElement, "entries", BoxEntry); } },
        ],
        Actions =
        [
            new ActionSpec { Id = "create-folder", Label = "Create a folder", Inputs = [F("name", "Folder name")],
                Run = async (c, i) => { using var d = await c.PostAsync(BoxApi + "folders", new { name = i["name"], parent = new { id = "0" } }); return new ActionResult("Folder created.", $"https://app.box.com/folder/{J.S(d.RootElement, "id")}"); } },
        ],
    };
}

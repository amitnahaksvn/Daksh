using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Daksh.Api.Auth;
using Daksh.Platform.Security;
using Microsoft.AspNetCore.WebUtilities;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace Daksh.Api.Modules;

// Google Drive, Docs, Sheets and Slides ride on the same Google sign-in as Gmail and Calendar: the extra permissions are asked for
// through Gmail's /connect (features=drive,docs,sheets,slides[,workspace_write]) and Google remembers what was already granted.
// One IntegrationConnection ("google-drive") represents the user's Workspace connection so it can sit on the dashboard like any app;
// the Google tokens themselves stay with the Gmail connection named in GoogleWorkspaceSettings.GmailConnectionId.

public sealed record GoogleServiceDto(string Id, bool Granted, bool Enabled);
public sealed record GoogleWorkspaceStatusDto(bool Configured, bool GoogleConnected, bool Connected, string? ConnectionId, string? Email, IReadOnlyList<GoogleServiceDto> Services, bool Write, bool WriteGranted, string? Error);
public sealed record EnableGoogleWorkspaceRequest(IReadOnlyList<string>? Services, bool? Write);
public sealed record DisableGoogleServiceRequest(string? Service);
public sealed record SetGoogleWriteRequest(bool Enabled);
public sealed record GoogleWorkspaceSettingsDto(int FileCount, string FileSource, string SortBy, string OpenIn, int SheetRows, bool ShowOnDashboard);
public sealed record UpdateGoogleWorkspaceSettingsRequest(int? FileCount, string? FileSource, string? SortBy, string? OpenIn, int? SheetRows);
public sealed record GoogleFileDto(string Id, string Name, string MimeType, string Kind, bool IsFolder, DateTimeOffset? Modified, long? Size, string? WebUrl, bool Starred, string? Owner);
public sealed record GoogleFileListing(string? FolderId, IReadOnlyList<GoogleFileDto> Items, string? NextPageToken);
public sealed record GoogleDocDto(string Id, string Title, string Text, string? WebUrl);
public sealed record GoogleSheetTab(string Title, int Rows, int Columns);
public sealed record GoogleSheetDto(string Id, string Title, string? WebUrl, IReadOnlyList<GoogleSheetTab> Tabs, string Range, IReadOnlyList<IReadOnlyList<string>> Values, bool Truncated);
public sealed record GoogleSlideDto(int Number, string Text);
public sealed record GooglePresentationDto(string Id, string Title, string? WebUrl, IReadOnlyList<GoogleSlideDto> Slides);
public sealed record CreateGoogleDocRequest(string? Title, string? Text);
public sealed record AppendGoogleDocRequest(string? Text);
public sealed record CreateGoogleSheetRequest(string? Title, IReadOnlyList<IReadOnlyList<string>>? Rows);
public sealed record AppendGoogleSheetRequest(string? Range, IReadOnlyList<IReadOnlyList<string>>? Rows);
public sealed record GoogleWorkspaceDashboardDto(bool Connected, string? Email, IReadOnlyList<string> Services, IReadOnlyList<GoogleFileDto> Files, IReadOnlyList<string> Errors);

/// <summary>How the user wants Google Drive, Docs and Sheets to behave. Defaults suit most people.</summary>
[BsonIgnoreExtraElements]
public sealed class GoogleWorkspaceSettings
{
    [BsonId] public required string UserId { get; set; }
    /// <summary>The Gmail connection (Google account) whose sign-in is used.</summary>
    public string? GmailConnectionId { get; set; }
    /// <summary>The "google-drive" IntegrationConnection that represents this setup (dashboard tile, status).</summary>
    public string? ConnectionId { get; set; }
    public List<string> Enabled { get; set; } = [];
    /// <summary>Full access: create files and add to Docs and Sheets. Off = read-only even if Google granted more.</summary>
    public bool Write { get; set; }
    public int FileCount { get; set; } = 5;
    /// <summary>recent | shared | starred | mine</summary>
    public string FileSource { get; set; } = "recent";
    /// <summary>modified | name</summary>
    public string SortBy { get; set; } = "modified";
    /// <summary>google | daksha — where a Doc or Sheet opens.</summary>
    public string OpenIn { get; set; } = "daksha";
    public int SheetRows { get; set; } = 25;
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class GoogleWorkspaceException(int statusCode, string message, string? code = null, string? requiredFeatures = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public string? RequiredFeatures { get; } = requiredFeatures;
}

public static class GoogleWorkspaceModule
{
    public const string IntegrationKey = "google-drive";
    public const string Drive = "drive", Docs = "docs", Sheets = "sheets", Slides = "slides";
    public static readonly string[] Services = [Drive, Docs, Sheets, Slides];
    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{10,200}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> ReadScopes = new()
    {
        [Drive] = "https://www.googleapis.com/auth/drive.readonly",
        [Docs] = "https://www.googleapis.com/auth/documents.readonly",
        [Sheets] = "https://www.googleapis.com/auth/spreadsheets.readonly",
        [Slides] = "https://www.googleapis.com/auth/presentations.readonly",
    };

    /// <summary>The permission that lets Daksha add to a service's files (Slides stays read-only).</summary>
    private static readonly Dictionary<string, string> WriteScopes = new()
    {
        [Drive] = "https://www.googleapis.com/auth/drive.file",
        [Docs] = "https://www.googleapis.com/auth/documents",
        [Sheets] = "https://www.googleapis.com/auth/spreadsheets",
    };

    /// <summary>The Gmail connect "features" names that ask Google for these services' permissions.</summary>
    public static string FeaturesFor(IEnumerable<string> services, bool write) =>
        string.Join(',', services.Where(Services.Contains).Concat(write ? ["workspace_write"] : []));

    public static bool IsGranted(string? grantedScopes, string service) =>
        ReadScopes.TryGetValue(service, out var scope) && GmailScopes.Satisfies(GmailScopes.Parse(grantedScopes), scope);

    public static bool WriteGranted(string? grantedScopes) =>
        WriteScopes.Values.All(s => GmailScopes.Satisfies(GmailScopes.Parse(grantedScopes), s));

    public static void MapGoogleWorkspace(this IEndpointRouteBuilder app)
    {
        var a = app.MapGroup("/api/integrations/google-workspace").WithTags("Google Workspace").RequireAuthorization(AuthSetup.UserScheme);
        a.MapGet("/status", StatusAsync);
        a.MapPost("/enable", EnableAsync);
        a.MapPost("/services/disable", DisableServiceAsync);
        a.MapPost("/write", SetWriteAsync);
        a.MapPost("/disconnect", DisconnectAsync);
        a.MapGet("/settings", GetSettingsAsync);
        a.MapPut("/settings", UpdateSettingsAsync);
        a.MapGet("/drive/files", ListFilesAsync);
        a.MapGet("/docs/{id}", GetDocAsync);
        a.MapPost("/docs", CreateDocAsync);
        a.MapPost("/docs/{id}/append", AppendDocAsync);
        a.MapGet("/sheets/{id}", GetSheetAsync);
        a.MapPost("/sheets", CreateSheetAsync);
        a.MapPost("/sheets/{id}/append", AppendSheetAsync);
        a.MapGet("/slides/{id}", GetSlidesAsync);
        a.MapGet("/dashboard", DashboardAsync);
    }

    // ---- settings and status ------------------------------------------------------------------------

    internal static async Task<GoogleWorkspaceSettings> LoadSettingsAsync(IMongoDatabase db, string userId, CancellationToken ct) =>
        await db.GetCollection<GoogleWorkspaceSettings>("googleWorkspaceSettings").Find(x => x.UserId == userId).FirstOrDefaultAsync(ct) ?? new GoogleWorkspaceSettings { UserId = userId };

    private static Task SaveSettingsAsync(IMongoDatabase db, GoogleWorkspaceSettings s, CancellationToken ct)
    {
        s.UpdatedAtUtc = DateTime.UtcNow;
        return db.GetCollection<GoogleWorkspaceSettings>("googleWorkspaceSettings").ReplaceOneAsync(x => x.UserId == s.UserId, s, new ReplaceOptions { IsUpsert = true }, ct);
    }

    /// <summary>The Google account (Gmail connection + stored grants) to use: the one chosen earlier if it is still connected, else the default.</summary>
    private static async Task<(IntegrationConnection Connection, GmailAccountDoc Account)?> GoogleAccountAsync(IMongoDatabase db, string userId, string? preferredId, CancellationToken ct)
    {
        var connection = await GmailIntegrationModule.ResolveConnectionAsync(db, userId, preferredId, ct) ?? await GmailIntegrationModule.ResolveConnectionAsync(db, userId, null, ct);
        if (connection is null) return null;
        return (connection, await GmailIntegrationModule.LoadAccountAsync(db, connection, ct));
    }

    private static async Task<IResult> StatusAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, IConfiguration config, CancellationToken ct)
    {
        var configured = !string.IsNullOrWhiteSpace(config["Gmail:OAuth:ClientId"]);
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        var google = await GoogleAccountAsync(db, who.UserId, s.GmailConnectionId, ct);
        if (google is null) return Results.Ok(new GoogleWorkspaceStatusDto(configured, false, false, null, null, Services.Select(x => new GoogleServiceDto(x, false, false)).ToArray(), false, false, null));
        var granted = google.Value.Account.GrantedScopes;
        var live = s.ConnectionId is not null && await db.GetCollection<IntegrationConnection>("integrationConnections").Find(c => c.Id == s.ConnectionId && c.Status == ConnectionStatus.Connected).AnyAsync(ct);
        var services = Services.Select(x => new GoogleServiceDto(x, IsGranted(granted, x), live && s.Enabled.Contains(x) && IsGranted(granted, x))).ToArray();
        var connected = live && services.Any(x => x.Enabled);
        return Results.Ok(new GoogleWorkspaceStatusDto(configured, true, connected, connected ? s.ConnectionId : null, google.Value.Account.Email, services, connected && s.Write && WriteGranted(granted), WriteGranted(granted), null));
    }

    private static IResult Invalid(string field, string message) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static IResult NeedsGoogle(IEnumerable<string> services, bool write, string message) =>
        Results.Problem(statusCode: 409, title: message, extensions: new Dictionary<string, object?> { ["code"] = "scope_upgrade_required", ["requiredScope"] = FeaturesFor(services, write) });

    /// <summary>Turns services on in Daksha. When Google has not been asked for a permission yet the answer says which "features" to connect with.</summary>
    private static async Task<IResult> EnableAsync(EnableGoogleWorkspaceRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var wanted = (r.Services ?? []).Select(x => x.ToLowerInvariant()).Where(Services.Contains).Distinct().ToList();
        if (wanted.Count == 0) wanted = [Drive, Docs, Sheets];
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        var google = await GoogleAccountAsync(db, who.UserId, s.GmailConnectionId, ct);
        if (google is null) return NeedsGoogle(wanted, r.Write == true, "Sign in with Google to continue.");
        var granted = google.Value.Account.GrantedScopes;
        var missing = wanted.Where(x => !IsGranted(granted, x)).ToList();
        var wantWrite = r.Write == true;
        if (missing.Count > 0 || (wantWrite && !WriteGranted(granted)))
            return NeedsGoogle(missing.Count > 0 ? missing : wanted, wantWrite, "Google needs your approval for these permissions.");

        var connections = db.GetCollection<IntegrationConnection>("integrationConnections");
        var email = google.Value.Account.Email;
        var now = DateTime.UtcNow;
        var existing = s.ConnectionId is null ? null : await connections.Find(c => c.Id == s.ConnectionId && c.UserId == who.UserId).FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            existing = new IntegrationConnection
            {
                UserId = who.UserId, IntegrationKey = IntegrationKey, Status = ConnectionStatus.Connected, DisplayName = "Google Workspace",
                AccountIdentifier = email, ProviderAccountId = email, ConnectedAt = now, CreatedAt = now, UpdatedAt = now,
            };
            await connections.InsertOneAsync(existing, cancellationToken: ct);
        }
        else
            await connections.UpdateOneAsync(c => c.Id == existing.Id, Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Connected)
                .Set(c => c.AccountIdentifier, email).Set(c => c.ConnectedAt, now).Set(c => c.DisconnectedAt, null).Set(c => c.LastError, null).Set(c => c.UpdatedAt, now), cancellationToken: ct);

        s.ConnectionId = existing.Id;
        s.GmailConnectionId = google.Value.Connection.Id;
        s.Enabled = s.Enabled.Union(wanted).ToList();
        if (r.Write is { } w) s.Write = w;
        await SaveSettingsAsync(db, s, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DisableServiceAsync(DisableGoogleServiceRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (r.Service is null || !Services.Contains(r.Service)) return Results.Problem(statusCode: 400, title: "Unknown Google service.");
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        s.Enabled.Remove(r.Service);
        await SaveSettingsAsync(db, s, ct);
        if (s.Enabled.Count == 0) await DisconnectAsync(db, who, ct);
        return Results.NoContent();
    }

    /// <summary>Full access off is immediate. Turning it on needs Google's approval of the extra permissions unless it was granted before.</summary>
    private static async Task<IResult> SetWriteAsync(SetGoogleWriteRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        if (r.Enabled)
        {
            var google = await GoogleAccountAsync(db, who.UserId, s.GmailConnectionId, ct);
            if (google is null || !WriteGranted(google.Value.Account.GrantedScopes)) return NeedsGoogle(s.Enabled.Count > 0 ? s.Enabled : [Drive, Docs, Sheets], true, "Google needs your approval to let Daksha create and edit files.");
        }
        s.Write = r.Enabled;
        await SaveSettingsAsync(db, s, ct);
        return Results.NoContent();
    }

    /// <summary>Turns all Google Workspace services off in Daksha. The Google sign-in stays, because Gmail and Calendar still use it.</summary>
    private static async Task<IResult> DisconnectAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        if (s.ConnectionId is null) return Results.NoContent();
        var now = DateTime.UtcNow;
        await db.GetCollection<IntegrationConnection>("integrationConnections").UpdateOneAsync(c => c.Id == s.ConnectionId && c.UserId == who.UserId,
            Builders<IntegrationConnection>.Update.Set(c => c.Status, ConnectionStatus.Disconnected).Set(c => c.DisconnectedAt, now).Set(c => c.UpdatedAt, now), cancellationToken: ct);
        await db.GetCollection<UserDashboardApp>("userDashboardApps").DeleteManyAsync(a => a.UserId == who.UserId && a.ConnectionId == s.ConnectionId, ct);
        s.Enabled = [];
        s.Write = false;
        await SaveSettingsAsync(db, s, ct);
        return Results.NoContent();
    }

    private static async Task<GoogleWorkspaceSettingsDto> ToDtoAsync(IMongoDatabase db, string userId, GoogleWorkspaceSettings s, CancellationToken ct)
    {
        var onDashboard = s.ConnectionId is not null && await db.GetCollection<UserDashboardApp>("userDashboardApps").Find(a => a.UserId == userId && a.ConnectionId == s.ConnectionId).AnyAsync(ct);
        return new GoogleWorkspaceSettingsDto(s.FileCount, s.FileSource, s.SortBy, s.OpenIn, s.SheetRows, onDashboard);
    }

    private static async Task<IResult> GetSettingsAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct) =>
        Results.Ok(await ToDtoAsync(db, who.UserId, await LoadSettingsAsync(db, who.UserId, ct), ct));

    private static async Task<IResult> UpdateSettingsAsync(UpdateGoogleWorkspaceSettingsRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, CancellationToken ct)
    {
        if (r.FileCount is { } n && (n < 3 || n > 10)) return Invalid("fileCount", "Choose between 3 and 10.");
        if (r.FileSource is not null && r.FileSource is not ("recent" or "shared" or "starred" or "mine")) return Invalid("fileSource", "Unknown file source.");
        if (r.SortBy is not null && r.SortBy is not ("modified" or "name")) return Invalid("sortBy", "Unknown sort order.");
        if (r.OpenIn is not null && r.OpenIn is not ("google" or "daksha")) return Invalid("openIn", "Unknown value.");
        if (r.SheetRows is { } rows && rows is not (10 or 25 or 50 or 100)) return Invalid("sheetRows", "Choose 10, 25, 50 or 100 rows.");
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        if (r.FileCount is { } fc) s.FileCount = fc;
        if (r.FileSource is not null) s.FileSource = r.FileSource;
        if (r.SortBy is not null) s.SortBy = r.SortBy;
        if (r.OpenIn is not null) s.OpenIn = r.OpenIn;
        if (r.SheetRows is { } sr) s.SheetRows = sr;
        await SaveSettingsAsync(db, s, ct);
        return Results.Ok(await ToDtoAsync(db, who.UserId, s, ct));
    }

    // ---- calling Google -----------------------------------------------------------------------------

    internal sealed record Ctx(string UserId, string GmailConnectionId, GoogleWorkspaceSettings Settings, IMongoDatabase Db, ISecretProtector Protector, IConfiguration Config, IHttpClientFactory Clients);

    private static async Task<Ctx> RequireAsync(string service, bool write, string userId, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var s = await LoadSettingsAsync(db, userId, ct);
        if (s.GmailConnectionId is null || !s.Enabled.Contains(service))
            throw new GoogleWorkspaceException(409, $"{Label(service)} is turned off. Turn it on in Manage.", "service_off");
        var google = await GoogleAccountAsync(db, userId, s.GmailConnectionId, ct)
            ?? throw new GoogleWorkspaceException(409, "Sign in with Google to continue.", "not_connected");
        if (!IsGranted(google.Account.GrantedScopes, service))
            throw new GoogleWorkspaceException(409, $"Google has not approved {Label(service)} yet.", "scope_upgrade_required", FeaturesFor([service], false));
        if (write)
        {
            if (!s.Write) throw new GoogleWorkspaceException(403, "Daksha has read-only access to your Google files. Turn on full access in Manage to change them.", "write_required");
            if (!WriteGranted(google.Account.GrantedScopes)) throw new GoogleWorkspaceException(409, "Google has not approved creating and editing files yet.", "scope_upgrade_required", FeaturesFor([service], true));
        }
        return new Ctx(userId, google.Connection.Id, s, db, protector, config, clients);
    }

    private static string Label(string service) => service switch { Drive => "Google Drive", Docs => "Google Docs", Sheets => "Google Sheets", _ => "Google Slides" };

    private static async Task<JsonDocument> GoogleAsync(Ctx c, string url, HttpMethod method, object? body, CancellationToken ct)
    {
        try { return await GmailIntegrationModule.GmailApiForAsync(c.UserId, c.GmailConnectionId, url, method, body, c.Db, c.Protector, c.Config, c.Clients, ct); }
        catch (GmailIntegrationException ex)
        {
            var message = ex.Message.Replace("Gmail", "Google");
            throw new GoogleWorkspaceException(ex.StatusCode, message, ex.Code);
        }
    }

    private static IResult Fail(GoogleWorkspaceException ex) =>
        Results.Problem(statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : 502, title: ex.Message,
            extensions: new Dictionary<string, object?> { ["code"] = ex.Code, ["requiredScope"] = ex.RequiredFeatures });

    private static async Task<IResult> Guard(Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (GoogleWorkspaceException ex) { return Fail(ex); }
        catch (HttpRequestException) { return Results.Problem(statusCode: 502, title: "Google could not be reached. Please retry."); }
    }

    private static bool ValidId(string id) => IdPattern.IsMatch(id);
    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- Drive --------------------------------------------------------------------------------------

    private static string Kind(string mime) => mime switch
    {
        "application/vnd.google-apps.folder" => "folder",
        "application/vnd.google-apps.document" => "doc",
        "application/vnd.google-apps.spreadsheet" => "sheet",
        "application/vnd.google-apps.presentation" => "slides",
        "application/pdf" => "pdf",
        _ when mime.StartsWith("image/", StringComparison.Ordinal) => "image",
        _ => "file",
    };

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string? MimeFilter(string? kind) => kind switch
    {
        "folders" => "mimeType = 'application/vnd.google-apps.folder'",
        "docs" => "mimeType = 'application/vnd.google-apps.document'",
        "sheets" => "mimeType = 'application/vnd.google-apps.spreadsheet'",
        "slides" => "mimeType = 'application/vnd.google-apps.presentation'",
        "pdf" => "mimeType = 'application/pdf'",
        "images" => "mimeType contains 'image/'",
        _ => null,
    };

    private const string FileFields = "nextPageToken,files(id,name,mimeType,modifiedTime,size,webViewLink,starred,owners(displayName))";

    private static GoogleFileDto ToFile(JsonElement f)
    {
        var mime = Str(f, "mimeType") ?? "";
        var owner = f.TryGetProperty("owners", out var o) && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() > 0 ? Str(o[0], "displayName") : null;
        return new GoogleFileDto(Str(f, "id") ?? "", Str(f, "name") ?? "", mime, Kind(mime), mime == "application/vnd.google-apps.folder",
            Str(f, "modifiedTime") is { } t && DateTimeOffset.TryParse(t, out var d) ? d : null,
            Str(f, "size") is { } sz && long.TryParse(sz, out var n) ? n : null, Str(f, "webViewLink"),
            f.TryGetProperty("starred", out var st) && st.ValueKind == JsonValueKind.True, owner);
    }

    internal static async Task<(IReadOnlyList<GoogleFileDto> Items, string? Next)> QueryFilesAsync(Ctx c, string? folderId, string? search, string view, string? kind, string sortBy, int pageSize, string? pageToken, CancellationToken ct)
    {
        var q = new List<string> { "trashed = false" };
        string orderBy;
        if (!string.IsNullOrWhiteSpace(search))
        {
            q.Add($"(name contains {Quote(search.Trim())} or fullText contains {Quote(search.Trim())})");
            orderBy = "modifiedTime desc";
        }
        else if (folderId is not null) { q.Add($"{Quote(folderId)} in parents"); orderBy = "folder,name"; }
        else switch (view)
        {
            case "shared": q.Add("sharedWithMe = true"); orderBy = sortBy == "name" ? "name" : "sharedWithMeTime desc"; break;
            case "starred": q.Add("starred = true"); orderBy = sortBy == "name" ? "name" : "modifiedTime desc"; break;
            case "mine": q.Add("'root' in parents"); orderBy = sortBy == "name" ? "folder,name" : "folder,modifiedTime desc"; break;
            default: orderBy = sortBy == "name" ? "name" : "viewedByMeTime desc"; q.Add("viewedByMeTime != null"); break;
        }
        if (MimeFilter(kind) is { } mf) q.Add(mf);
        var url = QueryHelpers.AddQueryString("https://www.googleapis.com/drive/v3/files", new Dictionary<string, string?>
        {
            ["q"] = string.Join(" and ", q), ["orderBy"] = orderBy, ["pageSize"] = Math.Clamp(pageSize, 1, 50).ToString(), ["fields"] = FileFields,
            ["supportsAllDrives"] = "true", ["includeItemsFromAllDrives"] = "true", ["pageToken"] = pageToken,
        });
        using var doc = await GoogleAsync(c, url, HttpMethod.Get, null, ct);
        var items = doc.RootElement.TryGetProperty("files", out var v) ? v.EnumerateArray().Select(ToFile).ToArray() : [];
        return (items, Str(doc.RootElement, "nextPageToken"));
    }

    private static Task<IResult> ListFilesAsync(string? folderId, string? q, string? view, string? kind, string? pageToken, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (folderId is not null && !ValidId(folderId)) return Results.BadRequest("That folder is not valid.");
        if (q is { Length: > 200 }) return Results.BadRequest("Search is too long.");
        var c = await RequireAsync(Drive, false, who.UserId, db, protector, config, clients, ct);
        var (items, next) = await QueryFilesAsync(c, folderId, q, view is "shared" or "starred" or "mine" ? view : "recent", kind, c.Settings.SortBy, 30, pageToken, ct);
        return Results.Ok(new GoogleFileListing(folderId, items, next));
    });

    // ---- Docs ---------------------------------------------------------------------------------------

    /// <summary>The plain text of a Google Doc: paragraphs, and the cells of tables, in reading order.</summary>
    public static string DocText(JsonElement content)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement elements)
        {
            foreach (var el in elements.EnumerateArray())
            {
                if (el.TryGetProperty("paragraph", out var p) && p.TryGetProperty("elements", out var parts))
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("textRun", out var run) && Str(run, "content") is { } text) sb.Append(text);
                if (el.TryGetProperty("table", out var t) && t.TryGetProperty("tableRows", out var rows))
                    foreach (var row in rows.EnumerateArray())
                        if (row.TryGetProperty("tableCells", out var cells))
                            foreach (var cell in cells.EnumerateArray())
                                if (cell.TryGetProperty("content", out var inner)) { Walk(inner); sb.Append('\t'); }
            }
        }
        Walk(content);
        return sb.ToString();
    }

    private static Task<IResult> GetDocAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ValidId(id)) return Results.BadRequest("That document is not valid.");
        var c = await RequireAsync(Docs, false, who.UserId, db, protector, config, clients, ct);
        using var doc = await GoogleAsync(c, $"https://docs.googleapis.com/v1/documents/{id}", HttpMethod.Get, null, ct);
        var text = doc.RootElement.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var content) ? DocText(content) : "";
        if (text.Length > 200_000) text = text[..200_000];
        return Results.Ok(new GoogleDocDto(id, Str(doc.RootElement, "title") ?? "Untitled", text, $"https://docs.google.com/document/d/{id}/edit"));
    });

    private static Task<IResult> CreateDocAsync(CreateGoogleDocRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        var title = r.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200) return Results.Problem(statusCode: 400, title: "Give the document a title (up to 200 characters).");
        if (r.Text is { Length: > 100_000 }) return Results.Problem(statusCode: 400, title: "The text is too long.");
        var c = await RequireAsync(Docs, true, who.UserId, db, protector, config, clients, ct);
        using var created = await GoogleAsync(c, "https://docs.googleapis.com/v1/documents", HttpMethod.Post, new { title }, ct);
        var id = Str(created.RootElement, "documentId") ?? "";
        if (id.Length > 0 && !string.IsNullOrEmpty(r.Text))
            using (await GoogleAsync(c, $"https://docs.googleapis.com/v1/documents/{id}:batchUpdate", HttpMethod.Post, new { requests = new[] { new { insertText = new { location = new { index = 1 }, text = r.Text } } } }, ct)) { }
        return Results.Ok(new GoogleDocDto(id, title, r.Text ?? "", $"https://docs.google.com/document/d/{id}/edit"));
    });

    private static Task<IResult> AppendDocAsync(string id, AppendGoogleDocRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ValidId(id)) return Results.BadRequest("That document is not valid.");
        if (string.IsNullOrEmpty(r.Text) || r.Text.Length > 100_000) return Results.Problem(statusCode: 400, title: "Enter the text to add (up to 100,000 characters).");
        var c = await RequireAsync(Docs, true, who.UserId, db, protector, config, clients, ct);
        int end;
        using (var doc = await GoogleAsync(c, $"https://docs.googleapis.com/v1/documents/{id}?fields=body.content.endIndex", HttpMethod.Get, null, ct))
            end = doc.RootElement.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var content) && content.GetArrayLength() > 0 && content[content.GetArrayLength() - 1].TryGetProperty("endIndex", out var e) ? e.GetInt32() : 2;
        using (await GoogleAsync(c, $"https://docs.googleapis.com/v1/documents/{id}:batchUpdate", HttpMethod.Post, new { requests = new[] { new { insertText = new { location = new { index = Math.Max(1, end - 1) }, text = r.Text } } } }, ct)) { }
        return Results.NoContent();
    });

    // ---- Sheets -------------------------------------------------------------------------------------

    private static string Cell(JsonElement v) => v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Null => "", _ => v.ToString() };

    private static Task<IResult> GetSheetAsync(string id, string? range, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ValidId(id)) return Results.BadRequest("That spreadsheet is not valid.");
        if (range is { Length: > 100 }) return Results.BadRequest("That range is not valid.");
        var c = await RequireAsync(Sheets, false, who.UserId, db, protector, config, clients, ct);
        using var meta = await GoogleAsync(c, $"https://sheets.googleapis.com/v4/spreadsheets/{id}?fields=properties.title,sheets.properties(title,gridProperties)", HttpMethod.Get, null, ct);
        var tabs = meta.RootElement.TryGetProperty("sheets", out var sh) ? sh.EnumerateArray().Select(t =>
        {
            var p = t.GetProperty("properties");
            var grid = p.TryGetProperty("gridProperties", out var g) ? g : default;
            return new GoogleSheetTab(Str(p, "title") ?? "Sheet", grid.ValueKind == JsonValueKind.Object && grid.TryGetProperty("rowCount", out var rc) ? rc.GetInt32() : 0,
                grid.ValueKind == JsonValueKind.Object && grid.TryGetProperty("columnCount", out var cc) ? cc.GetInt32() : 0);
        }).ToArray() : [];
        var chosen = string.IsNullOrWhiteSpace(range) ? (tabs.FirstOrDefault()?.Title ?? "Sheet1") : range.Trim();
        var title = meta.RootElement.TryGetProperty("properties", out var pr) ? Str(pr, "title") ?? "Untitled" : "Untitled";
        var rows = new List<IReadOnlyList<string>>();
        var limit = c.Settings.SheetRows;
        var truncated = false;
        using (var values = await GoogleAsync(c, $"https://sheets.googleapis.com/v4/spreadsheets/{id}/values/{Uri.EscapeDataString(chosen)}?majorDimension=ROWS", HttpMethod.Get, null, ct))
            if (values.RootElement.TryGetProperty("values", out var vv))
                foreach (var row in vv.EnumerateArray())
                {
                    if (rows.Count >= limit) { truncated = true; break; }
                    rows.Add(row.EnumerateArray().Select(Cell).ToArray());
                }
        return Results.Ok(new GoogleSheetDto(id, title, $"https://docs.google.com/spreadsheets/d/{id}/edit", tabs, chosen, rows, truncated));
    });

    private static bool ValidRows(IReadOnlyList<IReadOnlyList<string>>? rows) =>
        rows is { Count: > 0 and <= 500 } && rows.All(r => r.Count is > 0 and <= 50 && r.All(x => x.Length <= 5000));

    private static Task<IResult> CreateSheetAsync(CreateGoogleSheetRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        var title = r.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200) return Results.Problem(statusCode: 400, title: "Give the spreadsheet a title (up to 200 characters).");
        if (r.Rows is not null && !ValidRows(r.Rows)) return Results.Problem(statusCode: 400, title: "Those rows are too large (up to 500 rows of 50 cells).");
        var c = await RequireAsync(Sheets, true, who.UserId, db, protector, config, clients, ct);
        using var created = await GoogleAsync(c, "https://sheets.googleapis.com/v4/spreadsheets", HttpMethod.Post, new { properties = new { title } }, ct);
        var id = Str(created.RootElement, "spreadsheetId") ?? "";
        if (id.Length > 0 && r.Rows is { Count: > 0 })
            using (await GoogleAsync(c, $"https://sheets.googleapis.com/v4/spreadsheets/{id}/values/A1?valueInputOption=USER_ENTERED", HttpMethod.Put, new { values = r.Rows }, ct)) { }
        return Results.Ok(new { id, title, webUrl = $"https://docs.google.com/spreadsheets/d/{id}/edit" });
    });

    private static Task<IResult> AppendSheetAsync(string id, AppendGoogleSheetRequest r, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ValidId(id)) return Results.BadRequest("That spreadsheet is not valid.");
        if (!ValidRows(r.Rows)) return Results.Problem(statusCode: 400, title: "Enter the rows to add (up to 500 rows of 50 cells).");
        if (r.Range is { Length: > 100 }) return Results.Problem(statusCode: 400, title: "That range is not valid.");
        var c = await RequireAsync(Sheets, true, who.UserId, db, protector, config, clients, ct);
        var range = string.IsNullOrWhiteSpace(r.Range) ? "A1" : r.Range.Trim();
        using (await GoogleAsync(c, $"https://sheets.googleapis.com/v4/spreadsheets/{id}/values/{Uri.EscapeDataString(range)}:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS", HttpMethod.Post, new { values = r.Rows }, ct)) { }
        return Results.NoContent();
    });

    // ---- Slides -------------------------------------------------------------------------------------

    private static Task<IResult> GetSlidesAsync(string id, IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct) => Guard(async () =>
    {
        if (!ValidId(id)) return Results.BadRequest("That presentation is not valid.");
        var c = await RequireAsync(Slides, false, who.UserId, db, protector, config, clients, ct);
        using var doc = await GoogleAsync(c, $"https://slides.googleapis.com/v1/presentations/{id}", HttpMethod.Get, null, ct);
        var slides = new List<GoogleSlideDto>();
        if (doc.RootElement.TryGetProperty("slides", out var sl))
            foreach (var slide in sl.EnumerateArray())
            {
                var sb = new StringBuilder();
                if (slide.TryGetProperty("pageElements", out var els))
                    foreach (var el in els.EnumerateArray())
                        if (el.TryGetProperty("shape", out var shape) && shape.TryGetProperty("text", out var text) && text.TryGetProperty("textElements", out var te))
                            foreach (var t in te.EnumerateArray())
                                if (t.TryGetProperty("textRun", out var run) && Str(run, "content") is { } s) sb.Append(s);
                slides.Add(new GoogleSlideDto(slides.Count + 1, sb.ToString().Trim()));
            }
        return Results.Ok(new GooglePresentationDto(id, Str(doc.RootElement, "title") ?? "Untitled", $"https://docs.google.com/presentation/d/{id}/edit", slides));
    });

    // ---- dashboard ----------------------------------------------------------------------------------

    private static async Task<IResult> DashboardAsync(IMongoDatabase db, ClaimsPrincipalAccessor who, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        var s = await LoadSettingsAsync(db, who.UserId, ct);
        var google = await GoogleAccountAsync(db, who.UserId, s.GmailConnectionId, ct);
        if (google is null || s.Enabled.Count == 0) return Results.Ok(new GoogleWorkspaceDashboardDto(false, null, [], [], []));
        var errors = new List<string>();
        IReadOnlyList<GoogleFileDto> files = [];
        if (s.Enabled.Contains(Drive))
        {
            try
            {
                var c = await RequireAsync(Drive, false, who.UserId, db, protector, config, clients, ct);
                files = (await QueryFilesAsync(c, null, null, s.FileSource, null, s.SortBy, s.FileCount, null, ct)).Items;
            }
            catch (GoogleWorkspaceException ex) { errors.Add($"Google Drive: {ex.Message}"); }
            catch (HttpRequestException) { errors.Add("Google Drive: Google could not be reached."); }
        }
        return Results.Ok(new GoogleWorkspaceDashboardDto(true, google.Value.Account.Email, s.Enabled, files, errors));
    }

    // ---- for the assistant --------------------------------------------------------------------------

    /// <summary>Files matching <paramref name="query"/> (or the most recent ones) for the chat assistant. Empty when Drive is off.</summary>
    internal static async Task<IReadOnlyList<GoogleFileDto>?> FilesForAssistantAsync(string userId, string? query, int max, IMongoDatabase db, ISecretProtector protector, IConfiguration config, IHttpClientFactory clients, CancellationToken ct)
    {
        try
        {
            var c = await RequireAsync(Drive, false, userId, db, protector, config, clients, ct);
            return (await QueryFilesAsync(c, null, query, "recent", null, "modified", max, null, ct)).Items;
        }
        catch (GoogleWorkspaceException) { return null; }
        catch (HttpRequestException) { return null; }
    }
}

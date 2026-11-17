using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Storage;

/// <summary>Dropbox API v2 metadata and small-file operations. OAuth access tokens are supplied per call.</summary>
public sealed class DropboxIntegration(HttpClient http)
{
    private const string ApiBase = "https://api.dropboxapi.com/2/";
    private const string ContentBase = "https://content.dropboxapi.com/2/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DropboxConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = ApiRequest(token, "users/get_current_account", new { });
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var account = await response.Content.ReadFromJsonAsync<DropboxAccount>(Json, ct);
        return new(true, account?.AccountId, account?.Email, account?.Name?.DisplayName);
    }

    public async Task<DropboxListResult> ListFolderAsync(string token, string path = "", bool recursive = false,
        int limit = 2000, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 2000);
        using var request = ApiRequest(token, "files/list_folder", new { path, recursive, limit, include_deleted = false });
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<DropboxListResult>(Json, ct) ?? new([], false, null);
    }

    public async Task<DropboxListResult> ContinueListFolderAsync(string token, string cursor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cursor)) throw new ArgumentException("Dropbox cursor is required.", nameof(cursor));
        using var request = ApiRequest(token, "files/list_folder/continue", new { cursor });
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<DropboxListResult>(Json, ct) ?? new([], false, null);
    }

    public async Task<DropboxMetadata> CreateFolderAsync(string token, string path, bool autorename = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/')) throw new ArgumentException("Dropbox folder path must be an absolute Dropbox path.", nameof(path));
        using var request = ApiRequest(token, "files/create_folder_v2", new { path, autorename });
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("metadata").Deserialize<DropboxMetadata>(Json)
            ?? throw new InvalidOperationException("Dropbox returned no folder metadata.");
    }

    public async Task<DropboxMetadata> UploadFileAsync(string token, string path, Stream content, long contentLength,
        string mode = "add", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/')) throw new ArgumentException("Dropbox file path must be an absolute Dropbox path.", nameof(path));
        if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));
        if (mode is not ("add" or "overwrite")) throw new ArgumentException("Mode must be add or overwrite.", nameof(mode));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ContentBase), "files/upload"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", JsonSerializer.Serialize(new { path, mode, autorename = false, mute = false, strict_conflict = false }));
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = contentLength;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<DropboxMetadata>(Json, ct) ?? throw new InvalidOperationException("Dropbox returned no upload metadata.");
    }

    private static HttpRequestMessage ApiRequest(string token, string endpoint, object payload)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Dropbox access token is required.", nameof(token));
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(ApiBase), endpoint));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload, options: Json);
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Dropbox returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record DropboxConnection(bool Connected, string? AccountId = null, string? Email = null, string? DisplayName = null);
public sealed record DropboxAccount(string? AccountId, string? Email, DropboxName? Name);
public sealed record DropboxName(string? DisplayName, string? GivenName, string? Surname);
public sealed record DropboxListResult(IReadOnlyList<DropboxMetadata>? Entries, bool HasMore, string? Cursor);
public sealed record DropboxMetadata(string? Id, string? Name, string? PathDisplay, string? PathLower, string? Tag, long? Size);

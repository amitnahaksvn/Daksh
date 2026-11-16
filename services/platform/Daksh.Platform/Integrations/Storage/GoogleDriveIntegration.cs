using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Storage;

/// <summary>Google Drive v3 metadata client. Supply a Google OAuth access token per call.</summary>
public sealed class GoogleDriveIntegration(HttpClient http)
{
    private const string Base = "https://www.googleapis.com/drive/v3/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GoogleDriveConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "about?fields=user(displayName,emailAddress,photoLink)");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var user = doc.RootElement.GetProperty("user");
        return new(true,
            user.TryGetProperty("displayName", out var name) ? name.GetString() : null,
            user.TryGetProperty("emailAddress", out var email) ? email.GetString() : null);
    }

    public async Task<GoogleDriveFilePage> ListFilesAsync(string token, string? query = null, int pageSize = 100,
        string? pageToken = null, string? orderBy = "modifiedTime desc", CancellationToken ct = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var parameters = new List<string> { $"pageSize={pageSize}", "fields=nextPageToken,files(id,name,mimeType,webViewLink,modifiedTime,size,parents)", "spaces=drive" };
        if (!string.IsNullOrWhiteSpace(query)) parameters.Add($"q={Uri.EscapeDataString(query)}");
        if (!string.IsNullOrWhiteSpace(pageToken)) parameters.Add($"pageToken={Uri.EscapeDataString(pageToken)}");
        if (!string.IsNullOrWhiteSpace(orderBy)) parameters.Add($"orderBy={Uri.EscapeDataString(orderBy)}");
        using var request = Request(token, HttpMethod.Get, "files?" + string.Join("&", parameters));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleDriveFilePage>(Json, ct) ?? new([], null);
    }

    public async Task<GoogleDriveFile> CreateFolderAsync(string token, string name, string? parentId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Google Drive folder name is required.", nameof(name));
        var payload = new Dictionary<string, object?> { ["name"] = name, ["mimeType"] = "application/vnd.google-apps.folder" };
        if (!string.IsNullOrWhiteSpace(parentId)) payload["parents"] = new[] { parentId };
        using var request = Request(token, HttpMethod.Post, "files?fields=id,name,mimeType,webViewLink,parents");
        request.Content = JsonContent.Create(payload, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleDriveFile>(Json, ct)
            ?? throw new InvalidOperationException("Google Drive returned no folder.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Google OAuth access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Google Drive returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record GoogleDriveConnection(bool Connected, string? DisplayName = null, string? Email = null);
public sealed record GoogleDriveFilePage(IReadOnlyList<GoogleDriveFile>? Files, string? NextPageToken);
public sealed record GoogleDriveFile(string Id, string? Name, string? MimeType, string? WebViewLink, DateTimeOffset? ModifiedTime,
    long? Size, IReadOnlyList<string>? Parents);

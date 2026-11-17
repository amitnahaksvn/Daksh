using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Storage;

/// <summary>Box API client for account probing, folder browsing and folder creation.</summary>
public sealed class BoxIntegration(HttpClient http)
{
    private const string Base = "https://api.box.com/2.0/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<BoxConnection> ProbeAsync(string token, CancellationToken ct = default)
    {
        using var request = Request(token, HttpMethod.Get, "users/me");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var user = await response.Content.ReadFromJsonAsync<BoxUser>(Json, ct);
        return new(true, user?.Id, user?.Name, user?.Login);
    }

    public async Task<BoxFolderItems> ListFolderItemsAsync(string token, string folderId = "0", int limit = 100,
        int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderId)) throw new ArgumentException("Box folder ID is required.", nameof(folderId));
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(offset, 0);
        using var request = Request(token, HttpMethod.Get,
            $"folders/{Uri.EscapeDataString(folderId)}/items?limit={limit}&offset={offset}&fields=id,type,name,size,modified_at,web_url");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<BoxFolderItems>(Json, ct) ?? new([], 0, 0, 0);
    }

    public async Task<BoxFolder> CreateFolderAsync(string token, string name, string parentFolderId = "0",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Box folder name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(parentFolderId)) throw new ArgumentException("Box parent folder ID is required.", nameof(parentFolderId));
        using var request = Request(token, HttpMethod.Post, "folders");
        request.Content = JsonContent.Create(new { name, parent = new { id = parentFolderId } }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<BoxFolder>(Json, ct) ?? throw new InvalidOperationException("Box returned no folder.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Box access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Box returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record BoxConnection(bool Connected, string? UserId = null, string? Name = null, string? Login = null);
public sealed record BoxUser(string? Id, string? Name, string? Login);
public sealed record BoxFolderItems(IReadOnlyList<BoxItem>? Entries, int TotalCount, int Offset, int Limit);
public sealed record BoxItem(string Id, string Type, string? Name, long? Size, DateTimeOffset? ModifiedAt, string? WebUrl);
public sealed record BoxFolder(string Id, string? Name, string? Type, BoxParent? Parent);
public sealed record BoxParent(string? Id, string? Name);

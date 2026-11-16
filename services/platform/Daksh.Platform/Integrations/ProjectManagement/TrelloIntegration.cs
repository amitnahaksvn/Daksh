using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.ProjectManagement;

/// <summary>Trello REST API client. Use a user-authorized OAuth access token; never place credentials in URLs.</summary>
public sealed class TrelloIntegration(HttpClient http)
{
    private const string Base = "https://api.trello.com/1/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TrelloConnection> ProbeAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = Request(accessToken, HttpMethod.Get, "members/me");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var member = await response.Content.ReadFromJsonAsync<TrelloMember>(Json, ct);
        return new(true, member?.Id, member?.FullName, member?.Username);
    }

    public async Task<IReadOnlyList<TrelloBoard>> ListBoardsAsync(string accessToken, bool includeClosed = false, CancellationToken ct = default)
    {
        var path = $"members/me/boards?filter={(includeClosed ? "all" : "open")}&fields=id,name,desc,url,closed";
        using var request = Request(accessToken, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<List<TrelloBoard>>(Json, ct) ?? [];
    }

    public async Task<IReadOnlyList<TrelloList>> ListBoardListsAsync(string accessToken, string boardId, CancellationToken ct = default)
    {
        using var request = Request(accessToken, HttpMethod.Get, $"boards/{Uri.EscapeDataString(boardId)}/lists?filter=open");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<List<TrelloList>>(Json, ct) ?? [];
    }

    public async Task<IReadOnlyList<TrelloCard>> ListCardsAsync(string accessToken, string listId, CancellationToken ct = default)
    {
        using var request = Request(accessToken, HttpMethod.Get, $"lists/{Uri.EscapeDataString(listId)}/cards");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<List<TrelloCard>>(Json, ct) ?? [];
    }

    public async Task<TrelloCard> CreateCardAsync(string accessToken, string listId, string name, string? description = null,
        DateTimeOffset? due = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(listId)) throw new ArgumentException("Trello list ID is required.", nameof(listId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Card name is required.", nameof(name));
        using var request = Request(accessToken, HttpMethod.Post, "cards");
        request.Content = JsonContent.Create(new { idList = listId, name, desc = description, due = due?.ToString("O") }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<TrelloCard>(Json, ct) ?? throw new InvalidOperationException("Trello returned no card.");
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Trello access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Trello returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, response.StatusCode);
        }
    }
}

public sealed record TrelloConnection(bool Connected, string? MemberId = null, string? FullName = null, string? Username = null);
public sealed record TrelloMember(string? Id, string? FullName, string? Username);
public sealed record TrelloBoard(string Id, string? Name, string? Description, string? Url, bool Closed);
public sealed record TrelloList(string Id, string? Name, bool Closed, string? IdBoard);
public sealed record TrelloCard(string Id, string? Name, string? Description, string? Url, string? IdList, string? Due);

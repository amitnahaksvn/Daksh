using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Google;

/// <summary>Google Docs API v1 client. Supply a user OAuth access token per call.</summary>
public sealed class GoogleDocsIntegration(HttpClient http)
{
    private const string Base = "https://docs.googleapis.com/v1/documents/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GoogleDocument> GetDocumentAsync(string token, string documentId, CancellationToken ct = default)
    {
        Validate(documentId, nameof(documentId));
        using var request = Request(token, HttpMethod.Get, Uri.EscapeDataString(documentId));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleDocument>(Json, ct)
            ?? throw new InvalidOperationException("Google Docs returned an empty document.");
    }

    public async Task<GoogleDocument> CreateDocumentAsync(string token, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Document title is required.", nameof(title));
        using var request = Request(token, HttpMethod.Post, "");
        request.Content = JsonContent.Create(new { title }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GoogleDocument>(Json, ct)
            ?? throw new InvalidOperationException("Google Docs returned an empty creation response.");
    }

    public async Task<GoogleDocument> InsertTextAsync(string token, string documentId, string text,
        int? index = null, CancellationToken ct = default)
    {
        Validate(documentId, nameof(documentId));
        ArgumentNullException.ThrowIfNull(text);
        var document = index.HasValue ? null : await GetDocumentAsync(token, documentId, ct);
        var insertionIndex = index ?? GetEndIndex(document!);
        if (insertionIndex < 1) throw new ArgumentOutOfRangeException(nameof(index), "Google Docs insertion index must be at least 1.");
        using var request = Request(token, HttpMethod.Post, $"{Uri.EscapeDataString(documentId)}:batchUpdate");
        request.Content = JsonContent.Create(new
        {
            requests = new[] { new { insertText = new { location = new { index = insertionIndex }, text } } }
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await GetDocumentAsync(token, documentId, ct);
    }

    public async Task<GoogleDocument> ReplaceTextAsync(string token, string documentId, string findText,
        string replaceText, bool matchCase = true, CancellationToken ct = default)
    {
        Validate(documentId, nameof(documentId));
        if (string.IsNullOrEmpty(findText)) throw new ArgumentException("Search text is required.", nameof(findText));
        ArgumentNullException.ThrowIfNull(replaceText);
        using var request = Request(token, HttpMethod.Post, $"{Uri.EscapeDataString(documentId)}:batchUpdate");
        request.Content = JsonContent.Create(new
        {
            requests = new[] { new { replaceAllText = new { containsText = new { text = findText, matchCase }, replaceText } } }
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await GetDocumentAsync(token, documentId, ct);
    }

    private static int GetEndIndex(GoogleDocument document)
    {
        var end = document.Body?.Content?.Select(x => x.EndIndex).Where(x => x.HasValue).Max();
        return Math.Max(1, (end ?? 2) - 1);
    }

    private static HttpRequestMessage Request(string token, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Google OAuth access token is required.", nameof(token));
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Google Docs returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record GoogleDocument(string? DocumentId, string? Title, GoogleDocumentBody? Body);
public sealed record GoogleDocumentBody(IReadOnlyList<GoogleDocumentContent>? Content);
public sealed record GoogleDocumentContent(int? StartIndex, int? EndIndex, JsonElement? Paragraph, JsonElement? Table);

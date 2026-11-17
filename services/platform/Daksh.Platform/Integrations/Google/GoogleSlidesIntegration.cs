using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Google;

/// <summary>Google Slides API v1 client. Supply a user OAuth access token per call.</summary>
public sealed class GoogleSlidesIntegration(HttpClient http)
{
    private const string Base = "https://slides.googleapis.com/v1/presentations/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<GooglePresentation> GetPresentationAsync(string token, string presentationId, CancellationToken ct = default)
    {
        Validate(presentationId, nameof(presentationId));
        using var request = Request(token, HttpMethod.Get, Uri.EscapeDataString(presentationId));
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GooglePresentation>(Json, ct)
            ?? throw new InvalidOperationException("Google Slides returned an empty presentation.");
    }

    public async Task<GooglePresentation> CreatePresentationAsync(string token, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Presentation title is required.", nameof(title));
        using var request = Request(token, HttpMethod.Post, "");
        request.Content = JsonContent.Create(new { title }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<GooglePresentation>(Json, ct)
            ?? throw new InvalidOperationException("Google Slides returned an empty creation response.");
    }

    public async Task<GooglePresentation> CreateSlideAsync(string token, string presentationId,
        string? objectId = null, string predefinedLayout = "BLANK", CancellationToken ct = default)
    {
        Validate(presentationId, nameof(presentationId));
        var allowed = new[] { "BLANK", "CAPTION_ONLY", "TITLE", "TITLE_AND_BODY", "TITLE_AND_TWO_COLUMNS",
            "TITLE_ONLY", "SECTION_HEADER", "SECTION_TITLE_AND_DESCRIPTION", "ONE_COLUMN_TEXT", "MAIN_POINT", "BIG_NUMBER" };
        if (!allowed.Contains(predefinedLayout, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported predefined slide layout.", nameof(predefinedLayout));
        var slideId = string.IsNullOrWhiteSpace(objectId) ? "slide_" + Guid.NewGuid().ToString("N") : objectId;
        ValidateObjectId(slideId);
        await BatchUpdateAsync(token, presentationId, new object[]
        {
            new { createSlide = new { objectId = slideId, slideLayoutReference = new { predefinedLayout } } }
        }, ct);
        return await GetPresentationAsync(token, presentationId, ct);
    }

    public async Task<GooglePresentation> InsertTextAsync(string token, string presentationId,
        string shapeObjectId, string text, int? insertionIndex = null, CancellationToken ct = default)
    {
        Validate(presentationId, nameof(presentationId));
        ValidateObjectId(shapeObjectId);
        ArgumentNullException.ThrowIfNull(text);
        var insertion = new Dictionary<string, object?> { ["objectId"] = shapeObjectId, ["text"] = text };
        if (insertionIndex.HasValue) insertion["insertionIndex"] = insertionIndex.Value;
        await BatchUpdateAsync(token, presentationId, new object[] { new { insertText = insertion } }, ct);
        return await GetPresentationAsync(token, presentationId, ct);
    }

    public async Task<GooglePresentation> ReplaceAllTextAsync(string token, string presentationId,
        string findText, string replaceText, bool matchCase = true, CancellationToken ct = default)
    {
        Validate(presentationId, nameof(presentationId));
        if (string.IsNullOrEmpty(findText)) throw new ArgumentException("Search text is required.", nameof(findText));
        ArgumentNullException.ThrowIfNull(replaceText);
        await BatchUpdateAsync(token, presentationId, new object[]
        {
            new { replaceAllText = new { containsText = new { text = findText, matchCase }, replaceText } }
        }, ct);
        return await GetPresentationAsync(token, presentationId, ct);
    }

    private async Task BatchUpdateAsync(string token, string presentationId, IReadOnlyList<object> requests, CancellationToken ct)
    {
        using var request = Request(token, HttpMethod.Post, $"{Uri.EscapeDataString(presentationId)}:batchUpdate");
        request.Content = JsonContent.Create(new { requests }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
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

    private static void ValidateObjectId(string value)
    {
        Validate(value, nameof(value));
        if (value.Length > 50 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-zA-Z0-9_][a-zA-Z0-9_:-]*$"))
            throw new ArgumentException("Google Slides object IDs must be 5-50 characters and contain only letters, digits, underscore, colon or hyphen.", nameof(value));
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Google Slides returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record GooglePresentation(string? PresentationId, string? Title, string? PageSize,
    [property: JsonPropertyName("slides")] IReadOnlyList<GooglePresentationSlide>? Slides);
public sealed record GooglePresentationSlide(string? ObjectId, string? PageType, IReadOnlyList<JsonElement>? PageElements);

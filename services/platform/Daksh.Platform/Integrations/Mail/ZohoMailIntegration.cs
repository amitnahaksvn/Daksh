using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Mail;

/// <summary>Zoho Mail API client. Pass a regional mail host and OAuth token per call; tokens are not persisted.</summary>
public sealed class ZohoMailIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mail.zoho.com", "mail.zoho.eu", "mail.zoho.in", "mail.zoho.com.au",
        "mail.zoho.jp", "mail.zohocloud.ca", "mail.zoho.com.cn", "mail.zoho.ae", "mail.zoho.sa"
    };

    public async Task<ZohoMailAccountsResult> ListAccountsAsync(string token, string regionalMailBaseUrl,
        CancellationToken ct = default)
    {
        using var request = CreateRequest(token, HttpMethod.Get, regionalMailBaseUrl, "/api/accounts");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZohoMailAccountsResult>(Json, ct)
            ?? throw new InvalidOperationException("Zoho Mail returned an empty accounts response.");
    }

    public async Task<ZohoMailFoldersResult> ListFoldersAsync(string token, string regionalMailBaseUrl,
        string accountId, CancellationToken ct = default)
    {
        ValidateId(accountId, nameof(accountId));
        using var request = CreateRequest(token, HttpMethod.Get, regionalMailBaseUrl,
            $"/api/accounts/{Uri.EscapeDataString(accountId)}/folders");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZohoMailFoldersResult>(Json, ct)
            ?? throw new InvalidOperationException("Zoho Mail returned an empty folders response.");
    }

    public async Task<ZohoMailMessagesResult> ListMessagesAsync(string token, string regionalMailBaseUrl,
        string accountId, string folderId, int start = 1, int limit = 50, CancellationToken ct = default)
    {
        ValidateId(accountId, nameof(accountId));
        ValidateId(folderId, nameof(folderId));
        start = Math.Max(1, start);
        limit = Math.Clamp(limit, 1, 200);
        using var request = CreateRequest(token, HttpMethod.Get, regionalMailBaseUrl,
            $"/api/accounts/{Uri.EscapeDataString(accountId)}/messages/view?folderId={Uri.EscapeDataString(folderId)}&start={start}&limit={limit}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZohoMailMessagesResult>(Json, ct)
            ?? throw new InvalidOperationException("Zoho Mail returned an empty messages response.");
    }

    public async Task<ZohoMailSendResult> SendEmailAsync(string token, string regionalMailBaseUrl,
        string accountId, ZohoMailMessageDraft draft, CancellationToken ct = default)
    {
        ValidateId(accountId, nameof(accountId));
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.FromAddress) || string.IsNullOrWhiteSpace(draft.ToAddress)
            || string.IsNullOrWhiteSpace(draft.Subject))
            throw new ArgumentException("From, To and Subject are required.", nameof(draft));
        using var request = CreateRequest(token, HttpMethod.Post, regionalMailBaseUrl,
            $"/api/accounts/{Uri.EscapeDataString(accountId)}/messages");
        request.Content = JsonContent.Create(new
        {
            fromAddress = draft.FromAddress,
            toAddress = draft.ToAddress,
            ccAddress = draft.CcAddress,
            bccAddress = draft.BccAddress,
            subject = draft.Subject,
            content = draft.Content,
            mailFormat = draft.MailFormat ?? "html"
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZohoMailSendResult>(Json, ct)
            ?? throw new InvalidOperationException("Zoho Mail returned an empty send response.");
    }

    public async Task<ZohoMailFolderResult> CreateFolderAsync(string token, string regionalMailBaseUrl,
        string accountId, string folderName, string? parentFolderId = null, CancellationToken ct = default)
    {
        ValidateId(accountId, nameof(accountId));
        if (string.IsNullOrWhiteSpace(folderName)) throw new ArgumentException("Folder name is required.", nameof(folderName));
        using var request = CreateRequest(token, HttpMethod.Post, regionalMailBaseUrl,
            $"/api/accounts/{Uri.EscapeDataString(accountId)}/folders");
        request.Content = JsonContent.Create(new { folderName, parentFolderId }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<ZohoMailFolderResult>(Json, ct)
            ?? throw new InvalidOperationException("Zoho Mail returned an empty folder response.");
    }

    private static HttpRequestMessage CreateRequest(string token, HttpMethod method, string regionalMailBaseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Zoho OAuth access token is required.", nameof(token));
        if (!Uri.TryCreate(regionalMailBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(baseUri.Host)
            || baseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new ArgumentException("Use a supported HTTPS Zoho Mail regional host, such as https://mail.zoho.in.", nameof(regionalMailBaseUrl));
        var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        request.Headers.TryAddWithoutValidation("Authorization", "Zoho-oauthtoken " + token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Zoho Mail identifier is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Zoho Mail returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record ZohoMailAccountsResult(JsonElement? Status, JsonElement? Data);
public sealed record ZohoMailFoldersResult(JsonElement? Status, JsonElement? Data);
public sealed record ZohoMailMessagesResult(JsonElement? Status, JsonElement? Data);
public sealed record ZohoMailSendResult(JsonElement? Status, JsonElement? Data);
public sealed record ZohoMailFolderResult(JsonElement? Status, JsonElement? Data);
public sealed record ZohoMailMessageDraft(string FromAddress, string ToAddress, string Subject,
    string Content, string? CcAddress = null, string? BccAddress = null, string? MailFormat = "html");

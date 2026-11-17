using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daksh.Platform.Integrations.Payments;

/// <summary>PayPal REST API client. OAuth access tokens are acquired on demand; credentials are supplied per call and never persisted.</summary>
public sealed class PayPalIntegration(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string LiveBase = "https://api-m.paypal.com/";
    private const string SandboxBase = "https://api-m.sandbox.paypal.com/";

    public async Task<PayPalAccessToken> GetAccessTokenAsync(string clientId, string clientSecret,
        bool sandbox = true, CancellationToken ct = default)
    {
        ValidateCredentials(clientId, clientSecret);
        var baseUri = GetBase(sandbox);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "v1/oauth2/token"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(clientId + ":" + clientSecret)));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<PayPalAccessToken>(Json, ct)
            ?? throw new InvalidOperationException("PayPal returned an empty OAuth token response.");
    }

    public async Task<PayPalOrder> CreateOrderAsync(string clientId, string clientSecret,
        IReadOnlyList<PayPalPurchaseUnit> purchaseUnits, string intent = "CAPTURE",
        string? returnUrl = null, string? cancelUrl = null, bool sandbox = true,
        string? requestId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(purchaseUnits);
        if (purchaseUnits.Count == 0) throw new ArgumentException("At least one purchase unit is required.", nameof(purchaseUnits));
        if (intent is not ("CAPTURE" or "AUTHORIZE")) throw new ArgumentException("Intent must be CAPTURE or AUTHORIZE.", nameof(intent));
        var token = await GetAccessTokenAsync(clientId, clientSecret, sandbox, ct);
        var payload = new Dictionary<string, object?> { ["intent"] = intent, ["purchase_units"] = purchaseUnits };
        if (!string.IsNullOrWhiteSpace(returnUrl) || !string.IsNullOrWhiteSpace(cancelUrl))
            payload["payment_source"] = null; // Redirect URLs are configured in application_context by the checkout UI.
        using var request = ApiRequest(token.AccessToken, sandbox, HttpMethod.Post, "v2/checkout/orders", requestId);
        request.Content = JsonContent.Create(new
        {
            intent,
            purchase_units = purchaseUnits,
            application_context = string.IsNullOrWhiteSpace(returnUrl) && string.IsNullOrWhiteSpace(cancelUrl)
                ? null : new { return_url = returnUrl, cancel_url = cancelUrl }
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<PayPalOrder>(Json, ct)
            ?? throw new InvalidOperationException("PayPal returned an empty order.");
    }

    public async Task<PayPalOrder> GetOrderAsync(string clientId, string clientSecret, string orderId,
        bool sandbox = true, CancellationToken ct = default)
    {
        ValidateId(orderId, nameof(orderId));
        var token = await GetAccessTokenAsync(clientId, clientSecret, sandbox, ct);
        using var request = ApiRequest(token.AccessToken, sandbox, HttpMethod.Get, $"v2/checkout/orders/{Uri.EscapeDataString(orderId)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<PayPalOrder>(Json, ct)
            ?? throw new InvalidOperationException("PayPal returned an empty order.");
    }

    public async Task<PayPalOrder> CaptureOrderAsync(string clientId, string clientSecret, string orderId,
        bool sandbox = true, string? requestId = null, CancellationToken ct = default)
    {
        ValidateId(orderId, nameof(orderId));
        var token = await GetAccessTokenAsync(clientId, clientSecret, sandbox, ct);
        using var request = ApiRequest(token.AccessToken, sandbox, HttpMethod.Post,
            $"v2/checkout/orders/{Uri.EscapeDataString(orderId)}/capture", requestId);
        request.Content = JsonContent.Create(new { }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<PayPalOrder>(Json, ct)
            ?? throw new InvalidOperationException("PayPal returned an empty capture response.");
    }

    public async Task<PayPalRefund> RefundCaptureAsync(string clientId, string clientSecret, string captureId,
        string? currencyCode = null, string? amount = null, bool sandbox = true,
        string? requestId = null, CancellationToken ct = default)
    {
        ValidateId(captureId, nameof(captureId));
        object? refundAmount = null;
        if (!string.IsNullOrWhiteSpace(amount))
        {
            if (string.IsNullOrWhiteSpace(currencyCode) || !System.Text.RegularExpressions.Regex.IsMatch(amount, @"^\d+(\.\d{1,2})?$"))
                throw new ArgumentException("A valid currency code and decimal amount are required for a partial refund.");
            refundAmount = new { value = amount, currency_code = currencyCode.ToUpperInvariant() };
        }
        var token = await GetAccessTokenAsync(clientId, clientSecret, sandbox, ct);
        using var request = ApiRequest(token.AccessToken, sandbox, HttpMethod.Post,
            $"v2/payments/captures/{Uri.EscapeDataString(captureId)}/refund", requestId);
        request.Content = JsonContent.Create(refundAmount is null ? new { } : new { amount = refundAmount }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<PayPalRefund>(Json, ct)
            ?? throw new InvalidOperationException("PayPal returned an empty refund response.");
    }

    public async Task<bool> VerifyWebhookAsync(string clientId, string clientSecret, string webhookId,
        PayPalWebhookHeaders headers, JsonElement webhookEvent, bool sandbox = true, CancellationToken ct = default)
    {
        ValidateId(webhookId, nameof(webhookId));
        ArgumentNullException.ThrowIfNull(headers);
        var token = await GetAccessTokenAsync(clientId, clientSecret, sandbox, ct);
        using var request = ApiRequest(token.AccessToken, sandbox, HttpMethod.Post, "v1/notifications/verify-webhook-signature");
        request.Content = JsonContent.Create(new
        {
            auth_algo = headers.AuthAlgorithm,
            cert_url = headers.CertUrl,
            transmission_id = headers.TransmissionId,
            transmission_sig = headers.TransmissionSignature,
            transmission_time = headers.TransmissionTime,
            webhook_id = webhookId,
            webhook_event = webhookEvent
        }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        var result = await response.Content.ReadFromJsonAsync<PayPalWebhookVerification>(Json, ct);
        return string.Equals(result?.VerificationStatus, "SUCCESS", StringComparison.OrdinalIgnoreCase);
    }

    private static HttpRequestMessage ApiRequest(string token, bool sandbox, HttpMethod method, string path, string? requestId = null)
    {
        var request = new HttpRequestMessage(method, new Uri(GetBase(sandbox), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Prefer", "return=representation");
        if (!string.IsNullOrWhiteSpace(requestId)) request.Headers.TryAddWithoutValidation("PayPal-Request-Id", requestId);
        return request;
    }

    private static Uri GetBase(bool sandbox) => new(sandbox ? SandboxBase : LiveBase);
    private static void ValidateCredentials(string id, string secret)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("PayPal client ID and client secret are required.");
    }
    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid PayPal identifier is required.", name);
    }
    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"PayPal returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record PayPalAccessToken(string AccessToken, string TokenType, int ExpiresIn, string? Scope = null);
public sealed record PayPalPurchaseUnit(PayPalAmount Amount, string? ReferenceId = null, string? Description = null,
    string? InvoiceId = null, string? CustomId = null);
public sealed record PayPalAmount(string CurrencyCode, string Value);
public sealed record PayPalOrder(string Id, string? Status, string? Intent,
    [property: JsonPropertyName("purchase_units")] IReadOnlyList<JsonElement>? PurchaseUnits,
    [property: JsonPropertyName("links")] IReadOnlyList<JsonElement>? Links);
public sealed record PayPalRefund(string Id, string? Status, PayPalAmount? Amount,
    [property: JsonPropertyName("create_time")] DateTimeOffset? CreateTime);
public sealed record PayPalWebhookHeaders(string AuthAlgorithm, string CertUrl, string TransmissionId,
    string TransmissionSignature, string TransmissionTime);
public sealed record PayPalWebhookVerification(string? VerificationStatus);

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Payments;

/// <summary>Razorpay Orders, Payments and Refunds API client with server-side signature verification.</summary>
public sealed class RazorpayIntegration(HttpClient http)
{
    private const string Base = "https://api.razorpay.com/v1/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RazorpayOrder> CreateOrderAsync(string keyId, string keySecret, long amount,
        string currency = "INR", string? receipt = null, IReadOnlyDictionary<string, string>? notes = null,
        CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be positive and expressed in the smallest currency unit.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3) throw new ArgumentException("A three-letter currency code is required.", nameof(currency));
        using var request = Request(keyId, keySecret, HttpMethod.Post, "orders");
        request.Content = JsonContent.Create(new { amount, currency = currency.ToUpperInvariant(), receipt, notes }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<RazorpayOrder>(Json, ct)
            ?? throw new InvalidOperationException("Razorpay returned an empty order.");
    }

    public async Task<RazorpayOrder> FetchOrderAsync(string keyId, string keySecret, string orderId, CancellationToken ct = default)
    {
        ValidateId(orderId, nameof(orderId));
        using var request = Request(keyId, keySecret, HttpMethod.Get, $"orders/{Uri.EscapeDataString(orderId)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<RazorpayOrder>(Json, ct)
            ?? throw new InvalidOperationException("Razorpay returned an empty order.");
    }

    public async Task<RazorpayPayment> FetchPaymentAsync(string keyId, string keySecret, string paymentId, CancellationToken ct = default)
    {
        ValidateId(paymentId, nameof(paymentId));
        using var request = Request(keyId, keySecret, HttpMethod.Get, $"payments/{Uri.EscapeDataString(paymentId)}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<RazorpayPayment>(Json, ct)
            ?? throw new InvalidOperationException("Razorpay returned an empty payment.");
    }

    public async Task<RazorpayRefund> CreateRefundAsync(string keyId, string keySecret, string paymentId,
        long? amount = null, string? receipt = null, IReadOnlyDictionary<string, string>? notes = null,
        string? idempotencyKey = null, CancellationToken ct = default)
    {
        ValidateId(paymentId, nameof(paymentId));
        if (amount is <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        using var request = Request(keyId, keySecret, HttpMethod.Post, $"payments/{Uri.EscapeDataString(paymentId)}/refund");
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            request.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);
        request.Content = JsonContent.Create(new { amount, receipt, notes }, options: Json);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<RazorpayRefund>(Json, ct)
            ?? throw new InvalidOperationException("Razorpay returned an empty refund.");
    }

    public static bool VerifyPaymentSignature(string orderId, string paymentId, string signature, string keySecret)
    {
        ValidateId(orderId, nameof(orderId));
        ValidateId(paymentId, nameof(paymentId));
        return VerifyHmacSha256(Encoding.UTF8.GetBytes(orderId + "|" + paymentId), signature, keySecret);
    }

    public static bool VerifyWebhookSignature(ReadOnlySpan<byte> rawBody, string signature, string webhookSecret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(webhookSecret)) return false;
        return VerifyHmacSha256(rawBody, signature, webhookSecret);
    }

    private static bool VerifyHmacSha256(ReadOnlySpan<byte> data, string signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(secret) || signature.Length != 64) return false;
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); }
        catch (FormatException) { return false; }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = hmac.ComputeHash(data.ToArray());
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static HttpRequestMessage Request(string keyId, string keySecret, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keySecret))
            throw new ArgumentException("Razorpay key ID and key secret are required.");
        var request = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(keyId + ":" + keySecret)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('/') || value.Contains('?') || value.Contains('#'))
            throw new ArgumentException("A valid Razorpay identifier is required.", name);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Razorpay returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 500)]}", null, response.StatusCode);
        }
    }
}

public sealed record RazorpayOrder(string Id, string? Entity, long Amount, long AmountPaid, long AmountDue,
    string? Currency, string? Receipt, string? Status, int Attempts, long? CreatedAt);
public sealed record RazorpayPayment(string Id, string? Entity, long Amount, string? Currency, string? Status,
    string? OrderId, bool Captured, string? Method, long AmountRefunded, long? CreatedAt);
public sealed record RazorpayRefund(string Id, string? Entity, long Amount, string? Currency, string? PaymentId,
    string? Status, string? Receipt, long? CreatedAt);

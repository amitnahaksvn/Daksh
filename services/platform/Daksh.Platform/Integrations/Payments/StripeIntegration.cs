using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Daksh.Platform.Integrations.Payments;

/// <summary>Stripe REST API client. Secret keys are supplied per call and must be loaded from the encrypted secret store.</summary>
public sealed class StripeIntegration(HttpClient http)
{
    private const string Base = "https://api.stripe.com/v1/";

    public async Task<StripeConnection> ProbeAsync(string secretKey, CancellationToken ct)
    {
        using var req = Request(secretKey, HttpMethod.Get, "account");
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return new(false, Error: $"Stripe returned {(int)res.StatusCode} ({res.ReasonPhrase}).");
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        return new(true, root.TryGetProperty("id", out var id) ? id.GetString() : null,
            root.TryGetProperty("business_profile", out var profile) && profile.TryGetProperty("name", out var name) ? name.GetString() : null);
    }

    public async Task<StripePaymentIntent> CreatePaymentIntentAsync(string secretKey, long amountMinor, string currency,
        string? description = null, string? idempotencyKey = null, CancellationToken ct = default)
    {
        if (amountMinor <= 0) throw new ArgumentOutOfRangeException(nameof(amountMinor));
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3 || currency.Any(c => !char.IsAsciiLetter(c)))
            throw new ArgumentException("Currency must be a three-letter ISO currency code.", nameof(currency));
        using var req = Request(secretKey, HttpMethod.Post, "payment_intents");
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["amount"] = amountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = currency.ToLowerInvariant(),
            ["description"] = description ?? ""
        });
        using var res = await http.SendAsync(req, ct);
        await EnsureSuccess(res, ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        return new(root.GetProperty("id").GetString()!, root.GetProperty("status").GetString()!,
            root.GetProperty("client_secret").GetString(), root.GetProperty("amount").GetInt64(), root.GetProperty("currency").GetString()!);
    }

    public static bool VerifyWebhookSignature(string payload, string signatureHeader, string webhookSecret, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrWhiteSpace(webhookSecret)) return false;
        string? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in signatureHeader.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "t") timestamp = pair[1];
            if (pair[0] == "v1") signatures.Add(pair[1]);
        }
        if (!long.TryParse(timestamp, out var unix) || signatures.Count == 0) return false;
        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unix);
        if (age > (long)(tolerance ?? TimeSpan.FromMinutes(5)).TotalSeconds) return false;
        var signed = timestamp + "." + payload;
        var digest = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), Encoding.UTF8.GetBytes(signed));
        var expected = Convert.ToHexString(digest).ToLowerInvariant();
        return signatures.Any(s => System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(s.ToLowerInvariant())));
    }

    private static HttpRequestMessage Request(string key, HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Stripe secret key is required.", nameof(key));
        var req = new HttpRequestMessage(method, new Uri(new Uri(Base), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":")));
        return req;
    }

    private static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Stripe returned {(int)res.StatusCode}: {body[..Math.Min(body.Length, 400)]}", null, res.StatusCode);
        }
    }
}

public sealed record StripeConnection(bool Connected, string? AccountId = null, string? BusinessName = null, string? Error = null);
public sealed record StripePaymentIntent(string Id, string Status, string? ClientSecret, long Amount, string Currency);

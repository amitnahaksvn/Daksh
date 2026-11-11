using System.Security.Cryptography;
using System.Text;

namespace Daksh.Platform.Security;

/// <summary>Hashes and generators for high-entropy tokens (API keys, refresh tokens, pairing secrets).</summary>
public static class Tokens
{
    /// <summary>URL-safe random token with the given number of random bytes.</summary>
    public static string Generate(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>SHA-256 is sufficient for tokens with at least 128 bits of entropy; never use it for passwords.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Short human-typed code (pairing). Ambiguous characters are left out.</summary>
    public static string Code(int length = 8)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string HmacSha256Hex(string secret, ReadOnlySpan<byte> data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));
}

public sealed class SecretProtectorOptions
{
    /// <summary>Key id to base64 32-byte key. Old keys stay listed so existing values can still be read.</summary>
    public Dictionary<string, string> Keys { get; set; } = [];
    public string ActiveKeyId { get; set; } = "";
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
    /// <summary>True when the value was written with a key other than the active one.</summary>
    bool NeedsRotation(string protectedValue);
}

/// <summary>AES-256-GCM envelope: "v1.{keyId}.{nonce}.{ciphertext}.{tag}" (base64url parts).</summary>
public sealed class SecretProtector : ISecretProtector
{
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _active;

    public SecretProtector(SecretProtectorOptions options)
    {
        if (options.Keys.Count == 0) throw new InvalidOperationException("Security:Encryption:Keys must contain at least one key.");
        _keys = options.Keys.ToDictionary(k => k.Key, k =>
        {
            byte[] key;
            try { key = Convert.FromBase64String(k.Value); }
            catch (FormatException) { throw new InvalidOperationException($"Encryption key '{k.Key}' is not valid base64."); }
            if (key.Length != 32) throw new InvalidOperationException($"Encryption key '{k.Key}' must be 32 bytes (got {key.Length}).");
            return key;
        });
        _active = string.IsNullOrEmpty(options.ActiveKeyId) ? options.Keys.Keys.First() : options.ActiveKeyId;
        if (!_keys.ContainsKey(_active)) throw new InvalidOperationException($"Active encryption key '{_active}' is not in the key list.");
        if (_active.Contains('.')) throw new InvalidOperationException("Encryption key ids must not contain '.'.");
    }

    public string Protect(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_keys[_active], 16);
        aes.Encrypt(nonce, data, cipher, tag, Encoding.UTF8.GetBytes(_active));
        return $"v1.{_active}.{B64(nonce)}.{B64(cipher)}.{B64(tag)}";
    }

    public string Unprotect(string protectedValue)
    {
        var parts = protectedValue.Split('.');
        if (parts.Length != 5 || parts[0] != "v1") throw new CryptographicException("Value is not a protected secret.");
        if (!_keys.TryGetValue(parts[1], out var key)) throw new CryptographicException($"Encryption key '{parts[1]}' is not available.");
        var cipher = UnB64(parts[3]);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(UnB64(parts[2]), cipher, UnB64(parts[4]), plain, Encoding.UTF8.GetBytes(parts[1]));
        return Encoding.UTF8.GetString(plain);
    }

    public bool NeedsRotation(string protectedValue)
    {
        var parts = protectedValue.Split('.');
        return parts.Length == 5 && parts[1] != _active;
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>RFC 6238 time-based one-time passwords (HMAC-SHA1, 6 digits, 30-second step).</summary>
public static class Totp
{
    private const string Base32 = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string GenerateSecret(int bytes = 20)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(Base32[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] DecodeBase32(string s)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s.TrimEnd('=').ToUpperInvariant())
        {
            var v = Base32.IndexOf(c);
            if (v < 0) continue;
            buffer = (buffer << 5) | v; bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.ToArray();
    }

    public static string Compute(string base32Secret, DateTimeOffset time, int digits = 6)
    {
        var counter = time.ToUnixTimeSeconds() / 30;
        Span<byte> msg = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { msg[i] = (byte)(counter & 0xFF); counter >>= 8; }
        var hash = HMACSHA1.HashData(DecodeBase32(base32Secret), msg);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (code % (int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }

    /// <summary>Accepts the current step and one step either side to allow for clock drift.</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset now)
    {
        code = code.Trim().Replace(" ", "");
        if (code.Length != 6) return false;
        var ok = false;
        for (var drift = -1; drift <= 1; drift++)
            ok |= Tokens.FixedTimeEquals(Compute(base32Secret, now.AddSeconds(drift * 30)), code);
        return ok;
    }

    public static string ProvisioningUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";
}

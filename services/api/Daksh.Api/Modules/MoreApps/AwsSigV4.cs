using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Daksh.Api.Modules;

/// <summary>AWS Signature Version 4 (the signing AWS asks for on every API call). Checked against Amazon's published "get-vanilla" test vector.</summary>
public static class AwsSigV4
{
    public static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    public static string Sha256Hex(string s) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~') sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2"));
        return sb.ToString();
    }

    /// <summary>Returns the Authorization header value. <paramref name="headers"/> must already hold host and x-amz-date (and any other header to sign).</summary>
    public static string Authorization(string method, string path, IEnumerable<KeyValuePair<string, string>> query, IDictionary<string, string> headers, string payload,
        string accessKey, string secretKey, string region, string service, DateTimeOffset now)
    {
        var date = now.UtcDateTime.ToString("yyyyMMdd");
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var canonicalQuery = string.Join('&', query.Select(q => (Encode(q.Key), Encode(q.Value))).OrderBy(q => q.Item1, StringComparer.Ordinal).ThenBy(q => q.Item2, StringComparer.Ordinal).Select(q => $"{q.Item1}={q.Item2}"));
        var signed = headers.Select(h => (Name: h.Key.ToLowerInvariant(), Value: Regex.Replace(h.Value.Trim(), @"\s+", " "))).OrderBy(h => h.Name, StringComparer.Ordinal).ToList();
        var canonicalHeaders = string.Concat(signed.Select(h => $"{h.Name}:{h.Value}\n"));
        var signedHeaders = string.Join(';', signed.Select(h => h.Name));
        // Every service but S3 signs the path with its slashes kept and everything else encoded a second time.
        var canonicalPath = service == "s3" ? path : string.Join('/', path.Split('/').Select(seg => Encode(seg)));
        var canonicalRequest = $"{method}\n{canonicalPath}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{Sha256Hex(payload)}";
        var scope = $"{date}/{region}/{service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{stamp}\n{scope}\n{Sha256Hex(canonicalRequest)}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), date), region), service), "aws4_request");
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={Hex(Hmac(key, toSign))}";
    }
}

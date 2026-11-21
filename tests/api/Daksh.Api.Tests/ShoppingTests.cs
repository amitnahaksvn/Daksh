using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daksh.Api.Modules;
using Microsoft.Extensions.Configuration;

namespace Daksh.Api.Tests;

public class ShoppingTests
{
    private static string? MakeUrl(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var method = typeof(ShoppingModule).GetMethod("StreamUrl", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        return (string?)method.Invoke(null, [config, "u1", "amazon"]);
    }

    [Fact]
    public void Stream_token_is_signed_with_the_internal_key_and_expires()
    {
        var url = MakeUrl(new() { ["Integrations:Shopping:PublicStreamUrl"] = "wss://shop.example/stream", ["Integrations:Shopping:InternalServiceKey"] = "k3y" })!;
        Assert.StartsWith("wss://shop.example/stream?t=", url);
        var token = url[(url.IndexOf("?t=") + 3)..];
        var parts = token.Split('.');
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes("k3y"), Encoding.UTF8.GetBytes(parts[0]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, parts[1]);
        var padded = parts[0].Replace('-', '+').Replace('_', '/'); padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(padded));
        Assert.Equal("u1", doc.RootElement.GetProperty("u").GetString());
        Assert.Equal("amazon", doc.RootElement.GetProperty("s").GetString());
        var exp = doc.RootElement.GetProperty("e").GetInt64();
        Assert.InRange(exp - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 540, 620);
    }

    [Fact]
    public void No_stream_address_without_settings()
    {
        Assert.Null(MakeUrl(new() { ["Integrations:Shopping:InternalServiceKey"] = "k3y" }));
        Assert.Null(MakeUrl(new() { ["Integrations:Shopping:PublicStreamUrl"] = "wss://x/stream" }));
    }
}

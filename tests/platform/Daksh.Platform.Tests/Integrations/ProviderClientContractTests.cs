using System.Net;
using System.Text;
using Daksh.Platform.Integrations.Data;
using Daksh.Platform.Integrations.Payments;

namespace Daksh.Platform.Tests.Integrations;

public sealed class ProviderClientContractTests
{
    [Fact]
    public async Task AirtableListBases_DeserializesBasesCollection()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"bases":[{"id":"app123","name":"Workspace","permissionLevel":"create"}]}""", Encoding.UTF8, "application/json")
        }));
        var integration = new AirtableIntegration(client);

        var result = await integration.ListBasesAsync("pat-test");

        Assert.NotNull(result.Bases);
        Assert.Single(result.Bases);
        Assert.Equal("app123", result.Bases[0].Id);
        Assert.Equal("Workspace", result.Bases[0].Name);
    }

    [Fact]
    public async Task AirtableListRecords_DeserializesRecordsCollection()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"records":[{"id":"rec123","createdTime":"2026-01-01T00:00:00Z","fields":{"Name":"Test"}}],"offset":"next"}""", Encoding.UTF8, "application/json")
        }));
        var integration = new AirtableIntegration(client);

        var result = await integration.ListRecordsAsync("pat-test", "app123", "Table");

        Assert.NotNull(result.Records);
        Assert.Single(result.Records);
        Assert.Equal("rec123", result.Records[0].Id);
        Assert.Equal("next", result.Offset);
    }

    [Fact]
    public void RazorpayPaymentSignature_ValidatesExpectedHmac()
    {
        const string secret = "test-secret";
        const string orderId = "order_123";
        const string paymentId = "pay_123";
        using var hmac = new System.Security.Cryptography.HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(orderId + "|" + paymentId))).ToLowerInvariant();

        Assert.True(RazorpayIntegration.VerifyPaymentSignature(orderId, paymentId, signature, secret));
        Assert.False(RazorpayIntegration.VerifyPaymentSignature(orderId, paymentId, signature, "wrong-secret"));
    }

    [Fact]
    public async Task AirtableRejectsInvalidTableIdentifierBeforeSending()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("HTTP should not be called.")));
        var integration = new AirtableIntegration(client);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            integration.ListRecordsAsync("pat-test", "app123", "../records"));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}

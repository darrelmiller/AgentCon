using System.Net;
using BuyerAgent;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class SupplierBearerTokenTests : IDisposable
{
    private readonly string _tokenFile = Path.Combine(Path.GetTempPath(), $"buyer-token-{Guid.NewGuid():N}");

    public SupplierBearerTokenTests() => File.WriteAllText(_tokenFile, "header.payload.signature\n");

    public void Dispose() => File.Delete(_tokenFile);

    [Theory]
    [InlineData("https://agents.example.com/agentcon/barolo-supplier/a2a", "Bearer header.payload.signature")]
    [InlineData("https://other.example.com/a2a", null)]
    [InlineData("https://agents.example.com:8443/a2a", null)]
    public async Task SendsTheTokenOnlyToAllowlistedOrigins(string url, string? expected)
    {
        var (client, recorder) = Client(new() { SupplierBearerTokenFile = _tokenFile, SupplierBearerTokenOrigins = ["https://agents.example.com"] });
        await client.PostAsync(url, new StringContent("{}"));
        Assert.Equal(expected, recorder.Authorization);
    }

    [Fact]
    public async Task ReadsTheFileOnEveryRequest()
    {
        var (client, recorder) = Client(new() { SupplierBearerTokenFile = _tokenFile, SupplierBearerTokenOrigins = ["https://agents.example.com"] });
        await client.PostAsync("https://agents.example.com/a2a", new StringContent("{}"));
        File.WriteAllText(_tokenFile, "refreshed");
        await client.PostAsync("https://agents.example.com/a2a", new StringContent("{}"));
        Assert.Equal("Bearer refreshed", recorder.Authorization);
    }

    [Fact]
    public void RejectsNonHttpsOrigins()
    {
        var options = new BuyerOptions { SupplierBearerTokenFile = _tokenFile, SupplierBearerTokenOrigins = ["http://agents.example.com"] };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.SupplierBearerTokenOrigins = [];
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static (HttpClient, Recorder) Client(BuyerOptions options)
    {
        var recorder = new Recorder();
        return (new HttpClient(new SupplierBearerTokenHandler(options) { InnerHandler = recorder }), recorder);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}

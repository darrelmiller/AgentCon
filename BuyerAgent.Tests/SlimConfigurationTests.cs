using Agntcy.Slim;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class SlimConfigurationTests
{
    [Theory]
    [InlineData("""{"endpoint":"http://127.0.0.1:46357","tls":{"insecure":true}}""")]
    [InlineData("""{"endpoint":"http://127.0.0.1:46357","tls":{"insecure":true},"auth":{"type":"none"}}""")]
    public void UnauthenticatedClientConfigurationDecodesWithoutConnecting(string json)
    {
        var config = Slim.NewClientConfigFromJson(json);

        Assert.Equal("http://127.0.0.1:46357", config.Endpoint);
        Assert.Null(config.Oidc);
    }
}

using System.Text.Json;
using A2A;
using BuyerAgent;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class SupplierAuthorizationTests
{
    [Fact]
    public void RelaysWalletInstructionsWithoutClaimingAuthorization()
    {
        var task = TaskWithRequest("https://verifier.example/presentations/id/request");
        task.Status.Message!.Metadata!["agentcon.presentation"] = JsonSerializer.SerializeToElement(new
        {
            presentationUrl = "https://verifier.example/presentations/id"
        });
        var instructions = SupplierAuthorization.Instructions(task);
        Assert.Contains("openid4vp://?", instructions);
        Assert.Contains("https://verifier.example/presentations/id", instructions);
        Assert.Contains("your OID4VP wallet", instructions);
        Assert.Contains("Do not paste", instructions);
        Assert.Contains("supplier-task", instructions);
        Assert.Contains("no order has been confirmed", instructions);
    }

    [Fact]
    public void MissingMetadataProducesExplicitOperatorError()
    {
        var task = TaskWithRequest("https://verifier.example/request");
        task.Status.Message!.Metadata = null;
        Assert.Contains("did not provide", SupplierAuthorization.Instructions(task));
    }

    [Theory]
    [InlineData("http://verifier.example/request")]
    [InlineData("https://user:password@verifier.example/request")]
    [InlineData("javascript:alert(1)")]
    public void RejectsUnsafeRequestLinks(string uri) =>
        Assert.Throws<InvalidOperationException>(() => SupplierAuthorization.Instructions(TaskWithRequest(uri)));

    [Fact]
    public void LocalVerifierHttpRequiresExplicitOptInAndNeverAllowsRemoteHttp()
    {
        var local = TaskWithRequest("http://127.0.0.1:7777/request");
        Assert.Throws<InvalidOperationException>(() => SupplierAuthorization.Instructions(local));
        Assert.Contains("openid4vp://", SupplierAuthorization.Instructions(local, allowLoopbackHttp: true));
        Assert.Throws<InvalidOperationException>(() => SupplierAuthorization.Instructions(
            TaskWithRequest("http://verifier.example/request"), allowLoopbackHttp: true));
    }

    private static AgentTask TaskWithRequest(string uri) => new()
    {
        Id = "supplier-task", ContextId = "supplier-context",
        Status = new A2A.TaskStatus
        {
            State = TaskState.AuthRequired,
            Message = new Message
            {
                MessageId = "auth", Role = Role.Agent, Parts = [Part.FromText("Authorize")],
                Metadata = new()
                {
                    [BuyerCard.AuthorizationExtensionUri] = JsonSerializer.SerializeToElement(new
                    {
                        authorizationRequest = new { client_id = "redirect_uri:https://verifier.example/response", request_uri = uri }
                    })
                }
            }
        }
    };
}

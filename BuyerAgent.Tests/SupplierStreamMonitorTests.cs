using System.Net;
using System.Text;
using System.Text.Json;
using A2A;
using BuyerAgent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class SupplierStreamMonitorTests
{
    [Theory]
    [InlineData(false, null, TaskState.AuthRequired, false)]
    [InlineData(true, null, TaskState.AuthRequired, false)]
    [InlineData(false, TaskState.Working, TaskState.Working, false)]
    [InlineData(false, TaskState.InputRequired, TaskState.InputRequired, false)]
    [InlineData(false, TaskState.Completed, TaskState.Completed, false)]
    [InlineData(false, TaskState.Failed, TaskState.Failed, false)]
    [InlineData(false, TaskState.Canceled, TaskState.Canceled, false)]
    [InlineData(false, TaskState.Rejected, TaskState.Rejected, false)]
    [InlineData(false, TaskState.Completed, TaskState.Failed, true)]
    public async Task StreamLossPreservesStateButSellerEventsAreValidated(
        bool disconnect, TaskState? next, TaskState expected, bool invalidContext)
    {
        await using var app = BuyerApplication.Build([], builder => builder.WebHost.UseTestServer());
        await app.StartAsync();
        var seller = new AgentTask
        {
            Id = "seller", ContextId = "seller-context",
            Status = new A2A.TaskStatus
            {
                State = TaskState.AuthRequired,
                Message = new Message
                {
                    MessageId = "auth", Role = Role.Agent, Parts = [Part.FromText("Wallet approval pending")],
                    Metadata = new()
                    {
                        [BuyerCard.AuthorizationExtensionUri] = JsonSerializer.SerializeToElement(new
                        {
                            authorizationRequest = new { request_uri = "https://verifier.example/request" }
                        })
                    }
                }
            }
        };
        var events = Event(new StreamResponse { Task = seller });
        if (next is { } state)
            events += Event(new StreamResponse { StatusUpdate = new()
            {
                TaskId = seller.Id, ContextId = invalidContext ? "wrong-context" : seller.ContextId,
                Status = new() { State = state }
            } });
        using var network = new SellerNetwork(events, disconnect);
        using var http = new HttpClient(network);
        var options = new BuyerOptions();
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var supplier = new SupplierBinding("urn:test:seller", null, "Seller",
            new("http://localhost:6000/.well-known/agent-card.json"), new("http://localhost:6000/a2a"), "test")
        {
            Mode = SupplierCommunicationMode.NaturalLanguage, Streaming = true
        };
        var message = new Message { MessageId = "request", Role = Role.User, Parts = [Part.FromText("Purchase")] };
        var stream = new SupplierStream(gateway, supplier, message);
        var response = await stream.StartAsync(new() { Message = message }, CancellationToken.None);
        Assert.Equal(TaskState.AuthRequired, response.Task!.Status.State);
        var turn = new BuyerTurn(message, new BuyerConversation()) { SupplierStream = stream };
        turn.SupplierResponses.Add(response);
        var store = app.Services.GetRequiredService<ITaskStore>();
        await store.SaveTaskAsync("buyer", new AgentTask
        {
            Id = "buyer", ContextId = "buyer-context",
            Status = new A2A.TaskStatus { State = TaskState.AuthRequired, Message = seller.Status.Message }
        });
        var monitor = app.Services.GetRequiredService<SupplierStreamMonitor>();
        monitor.Watch("buyer", "buyer-context", turn, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (monitor.IsWatching("buyer")) await Task.Delay(10, timeout.Token);
        var saved = await store.GetTaskAsync("buyer");
        Assert.Equal(expected, saved!.Status.State);
        Assert.Equal(1, network.RequestCount);
        if (invalidContext) Assert.Contains("changed the bound task/context", saved.Status.Message!.Parts[0].Text);
        if (next is null)
        {
            Assert.Equal("Wallet approval pending", saved.Status.Message!.Parts[0].Text);
            Assert.True(saved.Status.Message.Metadata!.ContainsKey(BuyerCard.AuthorizationExtensionUri));
        }
        await app.StopAsync();
    }

    private static string Event(StreamResponse response) =>
        "data: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "request", result = response },
            A2AJsonUtilities.DefaultOptions) + "\n\n";

    private sealed class SellerNetwork(string events, bool disconnect) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new StreamContent(new SellerStream(Encoding.UTF8.GetBytes(events), disconnect));
            content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class SellerStream(byte[] bytes, bool disconnect) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (disconnect && Position == Length) throw new IOException("Simulated seller disconnect");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}

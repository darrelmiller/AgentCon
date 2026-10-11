using A2A;
using BuyerAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class BuyerServiceTests
{
    [Fact]
    public async Task DiscoversV1CardWithoutStartingInference()
    {
        await using var app = await StartAsync(new StubHarness((_, _) =>
            throw new InvalidOperationException("Inference should not run during discovery.")));
        using var http = app.GetTestClient();
        var card = await new A2ACardResolver(http.BaseAddress!, http).GetAgentCardAsync();
        Assert.Equal("AgentCon Wine Buyer", card.Name);
        Assert.Equal("1.0", Assert.Single(card.SupportedInterfaces).ProtocolVersion);
        Assert.Equal("JSONRPC", Assert.Single(card.SupportedInterfaces).ProtocolBinding);
        Assert.True(card.Capabilities.Streaming);
        Assert.Contains("application/json", card.DefaultInputModes);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task JsonRpcCompletesBuyerTaskAndPreservesStructuredInput()
    {
        Message? received = null;
        await using var app = await StartAsync(new StubHarness((turn, _) =>
        {
            received = turn.Message;
            return Task.FromResult("The requested quote is ready.");
        }));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var request = Request("Request a quote", data: """{"wine":"Barolo","cases":12}""");
        var response = await client.SendMessageAsync(request);
        var task = Assert.IsType<AgentTask>(response.Task);
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(12, received!.Parts[1].Data!.Value.GetProperty("cases").GetInt32());
        Assert.Contains(task.Artifacts!, artifact => artifact.Name == "buyer-response");
        var saved = await client.GetTaskAsync(new GetTaskRequest { Id = task.Id });
        Assert.Equal(TaskState.Completed, saved.Status.State);
    }

    [Fact]
    public async Task StreamsWorkingArtifactAndCompletionEvents()
    {
        await using var app = await StartAsync(new StubHarness((_, _) => Task.FromResult("Hello buyer")));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var events = new List<StreamResponse>();
        await foreach (var item in client.SendStreamingMessageAsync(Request("Hello")))
        {
            events.Add(item);
        }
        Assert.Contains(events, item => item.Task is not null);
        Assert.Contains(events, item => item.StatusUpdate?.Status.State == TaskState.Working);
        Assert.Contains(events, item => item.ArtifactUpdate?.Artifact.Name == "buyer-response");
        Assert.Equal(TaskState.Completed, events.Last().StatusUpdate!.Status.State);
    }

    [Fact]
    public async Task RemembersCompletedTurnsByContextButIsolatesOtherContexts()
    {
        var historySizes = new List<int>();
        await using var app = await StartAsync(new StubHarness((turn, _) =>
        {
            historySizes.Add(turn.Conversation.History.Count);
            return Task.FromResult("Remembered");
        }));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        await client.SendMessageAsync(Request("First", "conversation-a"));
        await client.SendMessageAsync(Request("Second", "conversation-a"));
        await client.SendMessageAsync(Request("Unrelated", "conversation-b"));
        Assert.Equal([0, 1, 0], historySizes);
    }

    [Theory]
    [InlineData(TaskState.AuthRequired, TaskState.AuthRequired)]
    [InlineData(TaskState.Working, TaskState.InputRequired)]
    [InlineData(TaskState.InputRequired, TaskState.InputRequired)]
    [InlineData(TaskState.Failed, TaskState.Failed)]
    [InlineData(TaskState.Rejected, TaskState.Failed)]
    public async Task DoesNotReportPendingOrFailedSupplierTasksAsSuccessful(TaskState supplierState, TaskState buyerState)
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [BuyerCard.AuthorizationExtensionUri] = JsonSerializer.SerializeToElement(new
            {
                authorizationRequest = new { client_id = "demo-verifier", request_uri = "https://verifier.example/request/123" }
            })
        };
        await using var app = await StartAsync(new StubHarness((turn, _) =>
        {
            turn.SupplierResponses.Add(new SendMessageResponse
            {
                Task = new AgentTask
                {
                    Id = "supplier-task",
                    ContextId = "supplier-context",
                    Status = new A2A.TaskStatus
                    {
                        State = supplierState,
                        Message = new Message
                        {
                            MessageId = "authorization",
                            Role = Role.Agent,
                            Parts = [Part.FromText("Age verification required")],
                            Metadata = metadata
                        }
                    },
                    Artifacts =
                    [
                        new Artifact
                        {
                            ArtifactId = "po",
                            Name = "purchase-order",
                            Parts = [Part.FromData(JsonSerializer.SerializeToElement(new { orderId = "demo-po" }))],
                            Metadata = new() { ["profile"] = JsonSerializer.SerializeToElement("demo-order") }
                        }
                    ]
                }
            });
            return Task.FromResult("Supplier response received.");
        }));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var response = await client.SendMessageAsync(Request("Buy demo wine"));
        Assert.Equal(buyerState, response.Task!.Status.State);
        var artifact = Assert.Single(response.Task.Artifacts!, item => item.Name == "purchase-order");
        Assert.Equal("demo-po", artifact.Parts[0].Data!.Value.GetProperty("orderId").GetString());
        Assert.Equal("demo-order", artifact.Metadata!["profile"].GetString());
        Assert.Contains(response.Task.Artifacts!, item => item.Name == "supplier-responses");
        if (supplierState == TaskState.AuthRequired)
        {
            Assert.Equal(metadata[BuyerCard.AuthorizationExtensionUri].GetRawText(),
                response.Task.Status.Message!.Metadata![BuyerCard.AuthorizationExtensionUri].GetRawText());
        }
    }

    [Fact]
    public async Task HarnessFailureProducesFailedTaskNotFabricatedSuccess()
    {
        await using var app = await StartAsync(new StubHarness((_, _) =>
            throw new InvalidOperationException("Copilot login required")));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var response = await client.SendMessageAsync(Request("Quote wine"));
        Assert.Equal(TaskState.Failed, response.Task!.Status.State);
        Assert.Contains("Copilot login required", response.Task.Status.Message!.Parts[0].Text);
    }

    [Fact]
    public async Task ToolFailureCannotBeHiddenBehindASuccessfulLlmReply()
    {
        await using var app = await StartAsync(new StubHarness((turn, _) =>
        {
            turn.ToolFailure = new HttpRequestException("Supplier unavailable");
            return Task.FromResult("The order is complete.");
        }));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var response = await client.SendMessageAsync(Request("Order demo wine"));
        Assert.Equal(TaskState.Failed, response.Task!.Status.State);
        Assert.Contains("Supplier unavailable", response.Task.Status.Message!.Parts[0].Text);
    }

    [Fact]
    public async Task CancellationAbortsActiveBuyerWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = await StartAsync(new StubHarness(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "Should not complete";
            }
            finally
            {
                stopped.TrySetResult();
            }
        }));
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var request = Request("Wait");
        request.Configuration = new SendMessageConfiguration { ReturnImmediately = true };
        var response = await client.SendMessageAsync(request);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var canceled = await client.CancelTaskAsync(new CancelTaskRequest { Id = response.Task!.Id });
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TimeoutEndsTaskAndDoesNotLeaveConversationLocked()
    {
        var count = 0;
        await using var app = await StartAsync(new StubHarness(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return "Recovered on next turn";
        }), "--Buyer:RequestTimeoutSeconds=1");
        using var http = app.GetTestClient();
        using var client = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var first = await client.SendMessageAsync(Request("Wait", "same-context"));
        Assert.Equal(TaskState.Failed, first.Task!.Status.State);
        var next = await client.SendMessageAsync(Request("Try another request", "same-context"));
        Assert.Equal(TaskState.Completed, next.Task!.Status.State);
    }

    [Fact]
    public async Task BlocksBrowserOriginRequests()
    {
        await using var app = await StartAsync(new StubHarness((_, _) => Task.FromResult("unused")));
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task ProductionServicesResolveCatalogBackedSupplierTools()
    {
        await using var app = BuyerApplication.Build([]);
        Assert.IsType<CopilotBuyerHarness>(app.Services.GetRequiredService<IBuyerHarness>());
        var gateway = app.Services.GetRequiredService<SupplierGateway>();
        var result = await gateway.SearchAsync("wine supplier", CancellationToken.None);
        var supplier = Assert.Single(result.Matches);
        Assert.Equal("urn:agentcon:agent:demo-wine-supplier", supplier.Identifier);
        Assert.Equal(new Uri("http://localhost:6000/.well-known/agent-card.json"), supplier.CardUrl);
        Assert.Equal(1, result.AgentEntriesScanned);
        Assert.Equal(1, result.ExcludedSelfEntries);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ObsoleteSupplierBaseUrlConfigurationFailsFast()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            BuyerApplication.Build(["--Buyer:SupplierBaseUrl=http://localhost:6000"]));
        Assert.Contains("Buyer:CatalogUrl", failure.Message);
    }

    [Fact]
    public async Task RelativeCatalogUrlResolvesAgainstContentRoot()
    {
        await using var app = await StartAsync(new StubHarness((_, _) => Task.FromResult("unused")),
            "--Buyer:CatalogUrl=..\\catalog.json");
        var options = app.Services.GetRequiredService<BuyerOptions>();
        var expected = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "catalog.json"));
        Assert.Equal(expected, options.GetCatalogUri().LocalPath);
    }

    internal static SendMessageRequest Request(string text, string? contextId = null, string? data = null) => new()
    {
        Message = new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = Role.User,
            ContextId = contextId,
            Parts = data is null
                ? [Part.FromText(text)]
                : [Part.FromText(text), Part.FromData(JsonSerializer.Deserialize<JsonElement>(data))]
        }
    };

    private static async Task<WebApplication> StartAsync(IBuyerHarness harness, params string[] configuration)
    {
        var app = BuyerApplication.Build(configuration, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.RemoveAll<IBuyerHarness>();
            builder.Services.AddSingleton(harness);
        });
        await app.StartAsync();
        return app;
    }

    private sealed class StubHarness(Func<BuyerTurn, CancellationToken, Task<string>> execute) : IBuyerHarness
    {
        public Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken) => execute(turn, cancellationToken);
    }
}

using A2A;
using BuyerAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class NativeBuyerTests
{
    private const string Identifier = "urn:test:native-wine";
    private const string Rfq = """{"kind":"rfq","schemaVersion":"1","demoOnly":true,"wine":"Barolo","vintage":2019,"cases":1,"deliveryCity":"Toronto","deliveryCountry":"CA"}""";
    private const string Order = """{"kind":"purchase-order","schemaVersion":"1","demoOnly":true,"quoteId":"q","purchaseOrderId":"po"}""";

    [Theory]
    [InlineData("none", TaskState.Completed)]
    [InlineData("receipt", TaskState.Failed)]
    [InlineData("ownership", TaskState.Failed)]
    public async Task StreamingWalletApprovalUpdatesBuyerWithoutAnotherMessageOrInference(string fault, TaskState expected)
    {
        using var network = new WineNetwork { Streaming = true, ReceiptQuoteId = fault == "receipt" ? "wrong" : "q",
            WrongStreamTask = fault == "ownership" };
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(TaskState.Completed, (await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq), timeout.Token)).Task!.Status.State);
        var pending = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase), timeout.Token)).Task!;
        Assert.Equal(TaskState.AuthRequired, pending.Status.State);
        Assert.Equal(2, network.Calls.Count);
        var conversation = app.Services.GetRequiredService<BuyerConversationStore>().Get("native-context");
        Assert.True(await conversation.Gate.WaitAsync(TimeSpan.FromSeconds(1), timeout.Token));
        conversation.Gate.Release();
        // Start a buyer subscription before approval; it must observe deterministic completion.
        var events = new List<StreamResponse>();
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = Task.Run(async () =>
        {
            await foreach (var item in buyer.SubscribeToTaskAsync(new() { Id = pending.Id }, timeout.Token))
            {
                events.Add(item);
                subscribed.TrySetResult();
            }
        }, timeout.Token);
        await subscribed.Task.WaitAsync(timeout.Token);
        network.Approve.TrySetResult();
        await read.WaitAsync(timeout.Token);
        var complete = await buyer.GetTaskAsync(new() { Id = pending.Id }, timeout.Token);
        Assert.Equal(expected, complete.Status.State);
        Assert.Equal(2, network.Calls.Count);
        Assert.Contains(events, item => item.StatusUpdate?.Status.State == expected);
        if (expected == TaskState.Completed)
        {
            Assert.Equal(0, complete.Status.Message!.Metadata![MessageInferenceCounter.MetadataKey].GetInt32());
            Assert.Equal("po", Assert.Single(complete.Artifacts!).Parts[0].Data!.Value.GetProperty("purchaseOrderId").GetString());
        }

    }

    [Fact]
    public async Task StreamingBuyerCancellationStopsTheListenerWithoutResendingPurchase()
    {
        using var network = new WineNetwork { Streaming = true };
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq), timeout.Token);
        var pending = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase), timeout.Token)).Task!;
        var canceled = await buyer.CancelTaskAsync(new() { Id = pending.Id }, timeout.Token);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        Assert.Equal(2, network.Calls.Count);
        Assert.Equal(TaskState.Canceled, (await buyer.GetTaskAsync(new() { Id = pending.Id }, timeout.Token)).Status.State);
    }

    [Fact]
    public async Task TypedQuotePurchaseAndSameTaskResumeDoNotInvokeBuyerHarness()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var quoted = await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq));
        Assert.Equal(TaskState.Completed, quoted.Task!.Status.State);
        Assert.Equal(0, quoted.Task.Status.Message!.Metadata![MessageInferenceCounter.MetadataKey].GetInt32());
        Assert.Equal(SupplierFormats.Quote, Assert.Single(quoted.Task.Artifacts!).Parts[0].MediaType);
        Assert.Equal(16900, quoted.Task.Artifacts![0].Parts[0].Data!.Value.GetProperty("totalMinor").GetInt32());
        Assert.All(network.Calls, call => Assert.Equal(SupplierFormats.Rfq, call.Parts[0].MediaType));

        var pending = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase))).Task!;
        Assert.Equal(TaskState.AuthRequired, pending.Status.State);
        Assert.Contains(BuyerCard.AuthorizationExtensionUri, pending.Status.Message!.Metadata!.Keys);
        Assert.Equal(0, pending.Status.Message.Metadata[MessageInferenceCounter.MetadataKey].GetInt32());
        Assert.Equal(0, pending.Status.Message.Metadata["agentcon.supplierLlmCalls"].GetInt32());
        Assert.Empty(pending.Artifacts ?? []);
        var waiting = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase, taskId: pending.Id))).Task!;
        Assert.Equal(TaskState.AuthRequired, waiting.Status.State);
        network.Verified = true;
        var completed = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase, taskId: pending.Id))).Task!;
        Assert.Equal(pending.Id, completed.Id);
        Assert.Equal(TaskState.Completed, completed.Status.State);
        Assert.Equal(SupplierFormats.Receipt, Assert.Single(completed.Artifacts!).Parts[0].MediaType);
        Assert.All(network.Calls.Skip(1), call =>
        {
            Assert.Equal("supplier-context", call.ContextId);
            Assert.Equal(SupplierFormats.Purchase, call.Parts[0].MediaType);
            Assert.Equal(0, call.Metadata![MessageInferenceCounter.MetadataKey].GetInt32());
        });
        Assert.Null(network.Calls[1].TaskId);
        Assert.Equal("purchase-task", network.Calls[2].TaskId);
        Assert.Equal("purchase-task", network.Calls[3].TaskId);
    }

    [Theory]
    [InlineData("application/json", Rfq)]
    [InlineData(SupplierFormats.Purchase, Rfq)]
    [InlineData(SupplierFormats.Rfq, """{"kind":"rfq","schemaVersion":"2","demoOnly":true}""")]
    [InlineData(SupplierFormats.Rfq, """{"kind":"rfq","schemaVersion":"1","demoOnly":true,"wine":"Barolo","vintage":2019,"cases":1.5,"deliveryCity":"Toronto"}""")]
    public async Task InvalidNativePayloadFailsBeforeSupplierOrInference(string type, string json)
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        Assert.Equal(TaskState.Failed, (await buyer.SendMessageAsync(Request(json, type))).Task!.Status.State);
        Assert.Empty(network.Calls);
    }

    [Fact]
    public async Task MixedPartsAndDisabledInboundTypesNeverReachInference()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var mixed = Request(Rfq, SupplierFormats.Rfq);
        mixed.Message.Parts.Add(Part.FromText("Buy instead"));
        Assert.Equal(TaskState.Failed, (await buyer.SendMessageAsync(mixed)).Task!.Status.State);
        app.Services.GetRequiredService<BuyerOptions>().NativeStructuredSupport = false;
        Assert.Equal(TaskState.Failed, (await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq))).Task!.Status.State);
        var card = await new A2ACardResolver(http.BaseAddress!, http).GetAgentCardAsync();
        Assert.Equal(["text/plain"], card.DefaultInputModes);
        Assert.Empty(network.Calls);
    }

    [Fact]
    public async Task PriceBudgetContextAndTaskBoundPayloadAreEnforced()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq));
        var budget = Request(Order, SupplierFormats.Purchase);
        budget.Message.Metadata!["agentcon.maxTotalUsd"] = JsonSerializer.SerializeToElement(1);
        Assert.Equal(TaskState.InputRequired, (await buyer.SendMessageAsync(budget)).Task!.Status.State);
        Assert.Single(network.Calls);
        var isolated = Request(Order, SupplierFormats.Purchase, contextId: "other");
        Assert.Equal(TaskState.Failed, (await buyer.SendMessageAsync(isolated)).Task!.Status.State);
        Assert.Single(network.Calls);
        var pending = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase))).Task!;
        Assert.Equal(TaskState.AuthRequired, pending.Status.State);
        var changed = Request(Order.Replace("\"po\"", "\"changed\""), SupplierFormats.Purchase, taskId: pending.Id);
        var rejected = (await buyer.SendMessageAsync(changed)).Task!;
        Assert.Equal(TaskState.AuthRequired, rejected.Status.State);
        Assert.Contains("Continuation rejected", rejected.Status.Message!.Parts[0].Text);
        Assert.Equal(2, network.Calls.Count);
        network.Verified = true;
        Assert.Equal(TaskState.Completed, (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase, taskId: pending.Id))).Task!.Status.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TextOnlySellerAutomaticallyReceivesTextAndNoSchemaFetch(bool native)
    {
        using var network = new WineNetwork { Structured = false };
        using var files = new TempCatalogs();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { NativeStructuredSupport = native, CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent(Identifier, "http://localhost:6002/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var selection = await gateway.SelectAsync(Identifier, null, CancellationToken.None);
        Assert.Equal(SupplierCommunicationMode.NaturalLanguage, selection.Binding.Mode);
        Assert.Empty(selection.Binding.Formats);
        if (native)
        {
            var turn = new BuyerTurn(Request(Rfq, SupplierFormats.Rfq).Message, new BuyerConversation());
            await new NativeBuyer(gateway).RunAsync(turn, CancellationToken.None);
            Assert.Equal(0, turn.Inference.Total);
            Assert.Equal(1, network.SellerTextCalls);
            Assert.Equal(PartContentCase.Text, Assert.Single(network.Calls).Parts[0].ContentCase);
        }
        else
            await gateway.SendAsync(selection.Binding, new Message { Role = Role.User, MessageId = "text",
                Parts = [gateway.CreatePart(selection.Binding, "Quote wine", null, null)] }, CancellationToken.None);
        Assert.Equal(0, network.SchemaFetches);
    }

    [Theory]
    [InlineData(false, SupplierCommunicationMode.LearnStructures, 1)]
    [InlineData(true, SupplierCommunicationMode.Native, 0)]
    public async Task AutomaticNegotiationUsesLearnedSchemasOrInstalledCodecs(bool native, SupplierCommunicationMode mode, int fetches)
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { NativeStructuredSupport = native, CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent(Identifier, "http://localhost:6002/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var snapshot = gateway.ForOptions(options.WithMode(SupplierCommunicationMode.Automatic));
        options.NativeStructuredSupport = !native;
        var selected = (await snapshot.SelectAsync(Identifier, null, CancellationToken.None)).Binding;
        Assert.Equal(mode, selected.Mode);
        Assert.Equal(fetches, network.SchemaFetches);
        network.Structured = false;
        var refreshed = await snapshot.RefreshModeAsync(selected, CancellationToken.None);
        Assert.Equal(SupplierCommunicationMode.NaturalLanguage, refreshed.Mode);
    }

    [Fact]
    public async Task TextInputStillInvokesHarnessWithNativeEnabled()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        var harness = new TextHarness();
        await using var app = await StartAsync(network, files, harness);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        await buyer.SendMessageAsync(BuyerServiceTests.Request("Quote wine"));
        Assert.Equal(1, harness.Calls);
        Assert.Empty(network.Calls);
    }

    [Fact]
    public async Task NativeOutputNegotiationConvertsArtifactsAndBlocksPurchasesWithoutTextStatus()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var rfq = Request(Rfq, SupplierFormats.Rfq);
        rfq.Configuration!.AcceptedOutputModes = ["text/plain"];
        var quote = (await buyer.SendMessageAsync(rfq)).Task!;
        Assert.Equal(PartContentCase.Text, Assert.Single(quote.Artifacts!).Parts[0].ContentCase);
        var po = Request(Order, SupplierFormats.Purchase);
        po.Configuration!.AcceptedOutputModes = [SupplierFormats.Receipt];
        Assert.Equal(TaskState.Failed, (await buyer.SendMessageAsync(po)).Task!.Status.State);
        Assert.Single(network.Calls);
        rfq.Configuration.AcceptedOutputModes = [SupplierFormats.Quote];
        var typedQuote = (await buyer.SendMessageAsync(rfq)).Task!;
        Assert.Equal(SupplierFormats.Quote, Assert.Single(typedQuote.Artifacts!).Parts[0].MediaType);
        Assert.Null(typedQuote.Status.Message);
    }

    [Fact]
    public async Task SupplierControlsRequireOriginAndConfiguredCatalogDestination()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        http.BaseAddress = new("http://localhost:5100");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/demo/supplier-structures",
            new { identifier = Identifier, enabled = false })).StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "http://localhost:5100");
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsJsonAsync("/demo/supplier-structures",
            new { identifier = Identifier, enabled = false })).StatusCode);
        app.Services.GetRequiredService<BuyerOptions>().SupplierControlToken = new string('a', 32);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/demo/supplier-structures",
            new { identifier = "http://attacker.example", enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/demo/supplier-structures",
            new { identifier = Identifier, enabled = false })).StatusCode);
        Assert.False(network.Structured);
        Assert.Empty(network.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRfqInputContinuationRetainsTheSupplierTask(bool streaming)
    {
        using var network = new WineNetwork { RequireRfqInput = true, Streaming = streaming };
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var first = (await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq))).Task!;
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        var next = (await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq, taskId: first.Id))).Task!;
        Assert.Equal(TaskState.Completed, next.Status.State);
        Assert.Equal("quote-task", network.Calls[1].TaskId);
        Assert.Equal("supplier-context", network.Calls[1].ContextId);
    }

    [Fact]
    public async Task TypedPurchaseRoutesToQuoteOwnerAfterSelectingAnotherCatalogSupplier()
    {
        using var network = new WineNetwork { Verified = true };
        using var files = new TempCatalogs();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { NativeStructuredSupport = true, CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent("urn:first", "http://localhost:6002/.well-known/agent-card.json"),
            TempCatalogs.Agent("urn:second", "http://localhost:6003/.well-known/agent-card.json")) };
        var native = new NativeBuyer(new SupplierGateway(http, options, new AiCatalogClient(http, options)));
        var conversation = new BuyerConversation();
        async Task Run(string json, string type, string identifier, string taskId)
        {
            var request = Request(json, type, taskId: taskId).Message;
            request.Metadata!["agentcon.supplierIdentifier"] = JsonSerializer.SerializeToElement(identifier);
            await native.RunAsync(new BuyerTurn(request, conversation), CancellationToken.None);
        }
        await Run(Rfq, SupplierFormats.Rfq, "urn:first", "rfq1");
        await Run(Rfq, SupplierFormats.Rfq, "urn:second", "rfq2");
        Assert.Equal("urn:second", conversation.SelectedSupplier!.Identifier);
        await Run(Order, SupplierFormats.Purchase, "urn:first", "purchase1");
        Assert.Equal([6002, 6003, 6002], network.Ports);
        Assert.Equal("supplier-context", network.Calls.Last().ContextId);

        var ambiguous = Request(Rfq, SupplierFormats.Rfq).Message;
        ambiguous.Metadata = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            native.RunAsync(new BuyerTurn(ambiguous, new BuyerConversation()), CancellationToken.None));
        Assert.Equal(3, network.Calls.Count);
    }

    [Fact]
    public async Task NativePurchaseRejectsUnboundReceiptAndConfiguredOutOfRangePrice()
    {
        using var network = new WineNetwork { Verified = true, ReceiptQuoteId = "tampered" };
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq));
        app.Services.GetRequiredService<BuyerOptions>().DemoMaxBottlePriceUsd = 10;
        Assert.Equal(TaskState.InputRequired, (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase))).Task!.Status.State);
        Assert.Single(network.Calls);
        app.Services.GetRequiredService<BuyerOptions>().DemoMaxBottlePriceUsd = 30;
        var failed = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase))).Task!;
        Assert.Equal(TaskState.Failed, failed.Status.State);
        Assert.Contains("receipt does not match", failed.Status.Message!.Parts[0].Text);
        Assert.Empty(failed.Artifacts ?? []);
    }

    [Fact]
    public async Task NativePurchaseCanUseAQuoteObservedByCopilotSupplierTools()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent(Identifier, "http://localhost:6002/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var conversation = new BuyerConversation();
        var textTurn = new BuyerTurn(BuyerServiceTests.Request("Quote wine").Message, conversation);
        var tools = new SupplierTurnTools(gateway, textTurn, CancellationToken.None);
        await tools.SelectAsync(Identifier);
        await tools.SendAsync(dataJson: Rfq, mediaType: SupplierFormats.Rfq);
        Assert.Single(conversation.NativeQuotes);
        options.NativeStructuredSupport = true;
        var nativeTurn = new BuyerTurn(Request(Order, SupplierFormats.Purchase, taskId: "native-after-text").Message, conversation);
        await new NativeBuyer(gateway).RunAsync(nativeTurn, CancellationToken.None);
        Assert.Equal(TaskState.AuthRequired, nativeTurn.SupplierResponses.Last().Task!.Status.State);
        Assert.Equal(0, nativeTurn.Inference.Total);
        Assert.Equal("supplier-context", network.Calls.Last().ContextId);
    }

    [Fact]
    public async Task QuoteAndPurchaseCanShareANonterminalSupplierAndBuyerTask()
    {
        using var network = new WineNetwork { QuoteAwaitsOrder = true };
        using var files = new TempCatalogs();
        await using var app = await StartAsync(network, files);
        using var http = app.GetTestClient();
        using var buyer = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var quoted = (await buyer.SendMessageAsync(Request(Rfq, SupplierFormats.Rfq))).Task!;
        Assert.Equal(TaskState.InputRequired, quoted.Status.State);
        var pending = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase, taskId: quoted.Id))).Task!;
        Assert.Equal(TaskState.AuthRequired, pending.Status.State);
        Assert.Equal("quote-task", network.Calls.Last().TaskId);
        network.Verified = true;
        var complete = (await buyer.SendMessageAsync(Request(Order, SupplierFormats.Purchase, taskId: quoted.Id))).Task!;
        Assert.Equal(TaskState.Completed, complete.Status.State);
        Assert.Equal(quoted.Id, complete.Id);
        Assert.Equal(SupplierFormats.Receipt, complete.Artifacts!.Last().Parts[0].MediaType);
    }

    [Fact]
    public async Task HistoricalTypedTaskReadsKeepTheirUnderstoodContractAfterSupplierToggle()
    {
        using var network = new WineNetwork();
        using var files = new TempCatalogs();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { NativeStructuredSupport = true, CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent(Identifier, "http://localhost:6002/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var conversation = new BuyerConversation();
        var initial = new BuyerTurn(Request(Rfq, SupplierFormats.Rfq, taskId: "initial").Message, conversation);
        await new NativeBuyer(gateway).RunAsync(initial, CancellationToken.None);
        network.Structured = false;
        options.NativeStructuredSupport = false;
        var read = new BuyerTurn(BuyerServiceTests.Request("Inspect quote").Message, conversation);
        var tools = new SupplierTurnTools(gateway, read, CancellationToken.None);
        await tools.GetTaskAsync("quote-task", Identifier);
        Assert.Equal(SupplierFormats.Quote, read.SupplierResponses.Single().Task!.Artifacts![0].Parts[0].MediaType);
        Assert.Equal(16900, read.SupplierResponses[0].Task!.Artifacts![0].Parts[0].Data!.Value.GetProperty("totalMinor").GetInt32());
        Assert.Single(network.Calls);
    }

    private static SendMessageRequest Request(string json, string type, string contextId = "native-context", string? taskId = null) => new()
    {
        Message = new Message { MessageId = Guid.NewGuid().ToString("N"), Role = Role.User, ContextId = contextId,
            TaskId = taskId, Parts = [new Part { Data = JsonSerializer.Deserialize<JsonElement>(json), MediaType = type }],
            Metadata = new() { ["agentcon.supplierIdentifier"] = JsonSerializer.SerializeToElement(Identifier) } },
        Configuration = new() { ReturnImmediately = false }
    };

    private static async Task<WebApplication> StartAsync(WineNetwork network, TempCatalogs files, IBuyerHarness? harness = null)
    {
        var catalog = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6002/.well-known/agent-card.json"));
        var app = BuyerApplication.Build(["--Buyer:NativeStructuredSupport=true", "--Buyer:ExchangeCaptureEnabled=true", $"--Buyer:CatalogUrl={catalog}"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.RemoveAll<IBuyerHarness>();
            builder.Services.AddSingleton<IBuyerHarness>(harness ?? new NoInference());
            builder.Services.AddSingleton(provider =>
            {
                var options = provider.GetRequiredService<BuyerOptions>();
                var http = new HttpClient(network, disposeHandler: false);
                return new SupplierGateway(http, options, new AiCatalogClient(http, options));
            });
            builder.Services.AddHttpClient<ExchangeDashboard>().ConfigurePrimaryHttpMessageHandler(() => network);
        });
        await app.StartAsync();
        return app;
    }

    private sealed class NoInference : IBuyerHarness
    {
        public Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Native request must not invoke buyer inference.");
    }
    private sealed class TextHarness : IBuyerHarness
    {
        public int Calls { get; private set; }
        public Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("Text was handled by the harness.");
        }
    }

    private sealed class WineNetwork : HttpMessageHandler
    {
        public bool Streaming { get; set; }
        public bool WrongStreamTask { get; set; }
        public TaskCompletionSource Approve { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _stop = new();
        public bool Structured { get; set; } = true;
        public bool Verified { get; set; }
        public bool RequireRfqInput { get; set; }
        public bool QuoteAwaitsOrder { get; set; }
        public string ReceiptQuoteId { get; set; } = "q";
        public int SchemaFetches { get; private set; }
        public int SellerTextCalls { get; private set; }
        public List<Message> Calls { get; } = [];
        public List<int> Ports { get; } = [];
        private readonly Dictionary<string, AgentTask> _tasks = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri!.AbsolutePath.StartsWith("/schemas"))
                {
                    SchemaFetches++;
                    using var stream = typeof(SupplierFormats).Assembly.GetManifestResourceStream("BuyerAgent.WineSchema.json")!;
                    return new(HttpStatusCode.OK) { Content = new StringContent(await new StreamReader(stream).ReadToEndAsync(cancellationToken),
                        System.Text.Encoding.UTF8, "application/schema+json") };
                }
                var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority);
                var card = CommunicationModeTests.Card(origin + "/schemas/wine-v1.schema.json");
                card.Capabilities.Streaming = Streaming;
                card.SupportedInterfaces[0].Url = origin + "/a2a";
                if (!Structured)
                {
                    card.DefaultInputModes = ["text/plain"];
                    card.DefaultOutputModes = ["text/plain"];
                    card.Capabilities.Extensions = [];
                }
                return FakeNetwork.Json(card);
            }
            if (request.RequestUri!.AbsolutePath == "/demo/structures")
            {
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal(new string('a', 32), request.Headers.Authorization.Parameter);
                Structured = (await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("enabled").GetBoolean();
                return FakeNetwork.Json(new { structuredDataParts = Structured });
            }
            using var rpc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (rpc.RootElement.GetProperty("method").GetString() == "GetTask")
                return FakeNetwork.Json(new { jsonrpc = "2.0", id = rpc.RootElement.GetProperty("id").Clone(),
                    result = _tasks[rpc.RootElement.GetProperty("params").GetProperty("id").GetString()!] });
            var send = rpc.RootElement.GetProperty("params").Deserialize<SendMessageRequest>(A2AJsonUtilities.DefaultOptions)!;
            Calls.Add(send.Message);
            Ports.Add(request.RequestUri.Port);
            var text = send.Message.Parts[0].ContentCase == PartContentCase.Text;
            if (text) SellerTextCalls++;
            var purchase = send.Message.Parts[0].MediaType == SupplierFormats.Purchase;
            var state = purchase && !Verified ? TaskState.AuthRequired : TaskState.Completed;
            if (!purchase && RequireRfqInput)
            {
                RequireRfqInput = false;
                state = TaskState.InputRequired;
            }
            if (!purchase && QuoteAwaitsOrder) state = TaskState.InputRequired;
            var quote = JsonSerializer.SerializeToElement(new WineQuote("quote", "1", true, "q", "Barolo", 2019, 1, 12,
                "Toronto", "USD", 14400, 2500, 16900, DateTimeOffset.UtcNow.AddMinutes(15), "CA"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var receipt = JsonSerializer.SerializeToElement(new WineReceipt("order-receipt", "1", true, "order", "po", ReceiptQuoteId,
                "simulated", "USD", 16900, "verified-demo-over-21", "not-performed", "not-performed"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            quote = JsonSerializer.Deserialize<JsonElement>(quote.GetRawText().Replace("\"totalMinor\":16900", "\"totalMinor\":16900.0"));
            receipt = JsonSerializer.Deserialize<JsonElement>(receipt.GetRawText().Replace("\"totalMinor\":16900", "\"totalMinor\":16900.0"));
            var reply = new Message { Role = Role.Agent, MessageId = "reply",
                Parts = [Part.FromText(purchase ? "Wallet required or purchase complete." :
                    "Quote q: 1 cases of 2019 Barolo (12 bottles/case) to Toronto, CA. USD 144.00/case + USD 25.00 demo delivery = USD 169.00.")],
                Metadata = new() { [MessageInferenceCounter.MetadataKey] = JsonSerializer.SerializeToElement(text ? 1 : 0) } };
            if (state == TaskState.AuthRequired)
                reply.Metadata[BuyerCard.AuthorizationExtensionUri] = JsonSerializer.SerializeToElement(new
                { authorizationRequest = new { client_id = "did:jwk:demo", request_uri = "https://verifier.example/request" } });
            var task = new AgentTask { Id = send.Message.TaskId ?? (purchase ? "purchase-task" : "quote-task"), ContextId = "supplier-context",
                Status = new A2A.TaskStatus { State = state, Message = reply },
                Artifacts = state == TaskState.AuthRequired || state == TaskState.InputRequired && !QuoteAwaitsOrder ? [] : [new Artifact { ArtifactId = purchase ? "receipt" : "quote",
                    Name = purchase ? "receipt" : "quote",
                    Parts = text ? reply.Parts : [new Part { Data = purchase ? receipt : quote,
                        MediaType = purchase ? SupplierFormats.Receipt : SupplierFormats.Quote }] }] };
            _tasks[task.Id] = task;
            if (rpc.RootElement.GetProperty("method").GetString() == "SendStreamingMessage")
            {
                string Frame(StreamResponse item) => "data: " + JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = rpc.RootElement.GetProperty("id").Clone(), result = item },
                    A2AJsonUtilities.DefaultOptions) + "\n\n";
                if (state != TaskState.AuthRequired)
                    return new(HttpStatusCode.OK) { Content = new StringContent(Frame(new() { Task = task }),
                        System.Text.Encoding.UTF8, "text/event-stream") };
                var pipe = new System.IO.Pipelines.Pipe();
                var rpcId = rpc.RootElement.GetProperty("id").Clone();
                string LaterFrame(StreamResponse item) => "data: " + JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = rpcId, result = item }, A2AJsonUtilities.DefaultOptions) + "\n\n";
                await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(Frame(new() { Task = task })), cancellationToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Approve.Task.WaitAsync(_stop.Token);
                        var artifact = new Artifact { ArtifactId = "receipt", Name = "receipt",
                            Parts = [new Part { Data = receipt, MediaType = SupplierFormats.Receipt }] };
                        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(LaterFrame(new()
                        {
                            ArtifactUpdate = new() { TaskId = WrongStreamTask ? "other-task" : task.Id,
                                ContextId = task.ContextId, Artifact = artifact, LastChunk = true }
                        })), _stop.Token);
                        await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(LaterFrame(new()
                        {
                            StatusUpdate = new() { TaskId = task.Id, ContextId = task.ContextId,
                                Status = new() { State = TaskState.Completed, Message = reply } }
                        })), _stop.Token);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                    finally { await pipe.Writer.CompleteAsync(); }
                });
                var content = new StreamContent(pipe.Reader.AsStream());
                content.Headers.ContentType = new("text/event-stream");
                return new(HttpStatusCode.OK) { Content = content };
            }
            return FakeNetwork.Json(new { jsonrpc = "2.0", id = rpc.RootElement.GetProperty("id").Clone(),
                result = new SendMessageResponse { Task = task } });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _stop.Cancel();
            base.Dispose(disposing);
        }
    }
}

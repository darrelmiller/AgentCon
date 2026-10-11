using A2A;
using BuyerAgent;
using System.Text.Json;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class SupplierTests : IDisposable
{
    private const string SupplierA = "urn:air:luca.example:agent:wine-supplier";
    private const string SupplierB = "urn:air:cellar.example:agent:wine-supplier";
    private readonly TempCatalogs _files = new();
    private readonly FakeNetwork _network = new FakeNetwork()
        .Supplier(6000, "Luca's Wine Supplier")
        .Supplier(6001, "Cellar Wine Supplier");

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task SearchListsAllMatchingSuppliersAndSelectsNothing()
    {
        var conversation = new BuyerConversation();
        var (tools, turn) = Tools(TwoSupplierCatalog(), conversation);
        using var result = JsonDocument.Parse(await tools.SearchAsync("wine supplier"));
        var matches = result.RootElement.GetProperty("matches").EnumerateArray()
            .Select(match => match.GetProperty("identifier").GetString()).ToList();
        Assert.Equal(2, matches.Count);
        Assert.Contains(SupplierA, matches);
        Assert.Contains(SupplierB, matches);
        Assert.Equal(1, result.RootElement.GetProperty("excludedSelfEntries").GetInt32());
        Assert.Contains("Multiple candidates", result.RootElement.GetProperty("note").GetString());
        Assert.True(result.RootElement.GetProperty("untrustedCatalogData").GetBoolean());
        Assert.Null(conversation.SelectedSupplier);
        Assert.Null(turn.ToolFailure);
        Assert.DoesNotContain(_network.Requests, request => request.Uri.Port == 6000 || request.Uri.Port == 6001);

        var (unselected, unselectedTurn) = Tools(TwoSupplierCatalog(), new BuyerConversation());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => unselected.SendAsync("Quote"));
        Assert.Contains("No supplier is selected", failure.Message);
        Assert.Same(failure, unselectedTurn.ToolFailure);
        Assert.Empty(_network.RpcCalls("SendMessage"));
    }

    [Fact]
    public async Task SelectedSupplierReceivesStructuredMessagesAndTaskPolling()
    {
        var conversation = new BuyerConversation();
        var (tools, turn) = Tools(TwoSupplierCatalog(), conversation, SupplierCommunicationMode.Native);
        using var selected = JsonDocument.Parse(await tools.SelectAsync(SupplierA));
        Assert.Equal("Luca's Wine Supplier", selected.RootElement.GetProperty("agentCard").GetProperty("name").GetString());
        Assert.Equal(SupplierA, selected.RootElement.GetProperty("selectedSupplier").GetProperty("identifier").GetString());
        Assert.Equal(SupplierA, conversation.SelectedSupplier!.Identifier);
        Assert.Equal(new Uri("http://localhost:6000/a2a"), conversation.SelectedSupplier.Endpoint);
        Assert.Contains(_network.Requests, request => request.Uri.AbsoluteUri == "http://localhost:6000/.well-known/agent-card.json");

        await tools.SendAsync("", """{"kind":"rfq","schemaVersion":"1","demoOnly":true,"wine":"Barolo","vintage":2019,"cases":12,"deliveryCity":"San Jose"}""",
            mediaType: SupplierFormats.Rfq);
        var sent = _network.Requests.Single(request => request.Method == "SendMessage");
        Assert.Equal(new Uri("http://localhost:6000/a2a"), sent.Uri);
        Assert.Equal(Role.User, sent.Message!.Role);
        Assert.Equal(12, sent.Message.Parts[0].Data!.Value.GetProperty("cases").GetInt32());
        Assert.Equal(2019, sent.Message.Parts[0].Data!.Value.GetProperty("vintage").GetInt32());
        Assert.Equal(SupplierFormats.Rfq, sent.Message.Parts[0].MediaType);
        Assert.True(conversation.SupplierTasks.IsOwnedBy("task-6000", conversation.SelectedSupplier));

        var task = await tools.GetTaskAsync("task-6000");
        Assert.Contains("task-6000", task);
        Assert.Equal(2, turn.SupplierResponses.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.GetTaskAsync("someone-elses-task"));
        var (more, _) = Tools(TwoSupplierCatalog(), conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => more.SendAsync("Continue", taskId: "someone-elses-task"));
        var (invalid, _) = Tools(TwoSupplierCatalog(), conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.SendAsync("Quote", "[]"));
        var (unknownContext, _) = Tools(TwoSupplierCatalog(), conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => unknownContext.SendAsync("Continue", contextId: "made-up-context"));
    }

    [Fact]
    public async Task SwitchingSuppliersCannotMisrouteEarlierTasksOrContexts()
    {
        var conversation = new BuyerConversation();
        var catalog = TwoSupplierCatalog();
        var (first, _) = Tools(catalog, conversation);
        await first.SelectAsync(SupplierA);
        await first.SendAsync("Quote Barolo");
        await first.SelectAsync(SupplierB);
        await first.SendAsync("Quote Barolo");
        Assert.Equal(SupplierB, conversation.SelectedSupplier!.Identifier);

        await first.GetTaskAsync("task-6000");
        await first.SendAsync("Accept the quote", contextId: "context-6000");
        await first.SendAsync("Anything else?", contextId: "context-6000");
        await first.GetTaskAsync("task-6001");

        Assert.Equal([6000, 6001, 6000], _network.RpcCalls("SendMessage").Select(uri => uri.Port).Take(3));
        Assert.Equal(6000, _network.RpcCalls("SendMessage").ElementAt(3).Port);
        Assert.Equal([6000, 6001], _network.RpcCalls("GetTask").Select(uri => uri.Port));
        var (mismatch, _) = Tools(catalog, conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mismatch.SendAsync("Continue", taskId: "task-6000", contextId: "context-6001"));
        var (wrongOwner, _) = Tools(catalog, conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrongOwner.GetTaskAsync("task-6000", supplierIdentifier: SupplierB));
        var (notSelected, _) = Tools(catalog, conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            notSelected.SendAsync("New quote", supplierIdentifier: SupplierA));
    }

    [Fact]
    public async Task CollidingTaskIdsRequireTheOwningSupplierIdentifier()
    {
        _network.SharedTaskId = "shared-task";
        var conversation = new BuyerConversation();
        var catalog = TwoSupplierCatalog();
        var (tools, _) = Tools(catalog, conversation);
        await tools.SelectAsync(SupplierA);
        await tools.SendAsync("Quote");
        await tools.SelectAsync(SupplierB);
        await tools.SendAsync("Quote");
        var (ambiguous, _) = Tools(catalog, conversation);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ambiguous.GetTaskAsync("shared-task"));
        Assert.Contains("multiple suppliers", failure.Message);
        var (explicitOwner, _) = Tools(catalog, conversation);
        await explicitOwner.GetTaskAsync("shared-task", supplierIdentifier: SupplierA);
        Assert.Equal(6000, Assert.Single(_network.RpcCalls("GetTask")).Port);
    }

    [Theory]
    [InlineData(TaskState.Completed)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Rejected)]
    [InlineData(TaskState.Canceled)]
    public async Task TerminalTasksCannotBeContinued(TaskState state)
    {
        var conversation = new BuyerConversation();
        var catalog = TwoSupplierCatalog();
        var (tools, _) = Tools(catalog, conversation);
        await tools.SelectAsync(SupplierA);
        await tools.SendAsync("Quote");
        conversation.SupplierTaskStates[(SupplierA, "task-6000")] = state;
        var (next, _) = Tools(catalog, conversation);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => next.SendAsync("Order", taskId: "task-6000"));
        Assert.Contains("terminal", error.Message);
        Assert.Single(_network.RpcCalls("SendMessage"));
        var (newTask, _) = Tools(catalog, conversation);
        await newTask.SendAsync("Order", contextId: "context-6000");
        Assert.Null(_network.Requests.Last().Message!.TaskId);
        Assert.Equal("context-6000", _network.Requests.Last().Message!.ContextId);
    }

    [Fact]
    public async Task InputRequiredTaskAcceptsRfqAndOrderContinuation()
    {
        var conversation = new BuyerConversation();
        var (tools, _) = Tools(TwoSupplierCatalog(), conversation);
        await tools.SelectAsync(SupplierA);
        await tools.SendAsync("Buy wine");
        conversation.SupplierTaskStates[(SupplierA, "task-6000")] = TaskState.InputRequired;
        await tools.SendAsync("Here are the RFQ details", taskId: "task-6000", contextId: "context-6000");
        Assert.Equal("task-6000", _network.Requests.Last().Message!.TaskId);
        conversation.SupplierTaskStates[(SupplierA, "task-6000")] = TaskState.InputRequired;
        await tools.SendAsync("Place the simulated order", taskId: "task-6000", contextId: "context-6000");
        Assert.Equal("task-6000", _network.Requests.Last().Message!.TaskId);
    }

    [Theory]
    [InlineData(14400, 175300, null, true)]
    [InlineData(9600, 117700, null, true)]
    [InlineData(36000, 434500, null, true)]
    [InlineData(9500, 116500, null, false)]
    [InlineData(36100, 435700, null, false)]
    [InlineData(14400, 175301, null, false)]
    [InlineData(14400, 175300, 1700, false)]
    [InlineData(14400, 175300, 1753, true)]
    public void DemoPriceCheckHonorsRangeArithmeticAndBudget(int casePrice, int total, int? budget, bool accepted)
    {
        using var http = new HttpClient(_network, disposeHandler: false);
        var options = new BuyerOptions();
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var assessment = JsonSerializer.SerializeToElement(gateway.AssessPrice("Barolo", 2019, "USD",
            12, 12, casePrice, 2500, total, budget));
        Assert.Equal(accepted, assessment.GetProperty("withinBallpark").GetBoolean());
        var unknown = JsonSerializer.SerializeToElement(gateway.AssessPrice("Chianti", 2019, "USD",
            12, 12, casePrice, 2500, total, budget));
        Assert.False(unknown.GetProperty("withinBallpark").GetBoolean());
    }

    [Fact]
    public async Task AssessmentRejectsInventedQuoteIdsAndRecordsObservedCheck()
    {
        var (tools, turn) = Tools(TwoSupplierCatalog(), new BuyerConversation());
        turn.SupplierResponses.Add(new SendMessageResponse { Message = new Message
        {
            MessageId = "observed-quote", Role = Role.Agent, Parts = [Part.FromData(JsonSerializer.SerializeToElement(new
            {
                quoteId = "quote-one", wine = "Barolo", vintage = 2019, currency = "USD", cases = 12,
                bottlesPerCase = 12, casePriceMinor = 14400, deliveryMinor = 2500, totalMinor = 175300
            }))]
        }});
        using var result = JsonDocument.Parse(await tools.AssessQuoteAsync("quote-one"));
        Assert.True(result.RootElement.GetProperty("withinBallpark").GetBoolean());
        Assert.Single(turn.QuoteAssessments);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.AssessQuoteAsync("invented-quote"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1700, false)]
    public async Task TextQuoteIsAssessedFromObservedPrices(int? budget, bool expected)
    {
        var (tools, turn) = Tools(TwoSupplierCatalog(), new BuyerConversation());
        turn.SupplierResponses.Add(new SendMessageResponse { Message = new Message
        {
            MessageId = "quote", Role = Role.Agent, Parts = [Part.FromText(
                "Quote demo-quote-one: 12 cases of 2019 Barolo (12 bottles/case) to San Jose. USD 144.00/case + USD 25.00 demo delivery = USD 1753.00. Valid 15 minutes.")]
        }});
        using var result = JsonDocument.Parse(await tools.AssessQuoteAsync("demo-quote-one", budget));
        Assert.Equal(expected, result.RootElement.GetProperty("withinBallpark").GetBoolean());
        Assert.Equal(!expected, turn.PriceApprovalRequired);
        if (!expected) await Assert.ThrowsAsync<InvalidOperationException>(() => tools.SendAsync("Order"));
    }

    [Fact]
    public async Task SelectionRequiresAnExactDiscoveredIdentifier()
    {
        var catalog = TwoSupplierCatalog();
        var (unknown, _) = Tools(catalog, new BuyerConversation());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.SelectAsync("http://localhost:6000"));
        Assert.Contains("No A2A Agent Card entry", failure.Message);
        var (prefix, _) = Tools(catalog, new BuyerConversation());
        await Assert.ThrowsAsync<InvalidOperationException>(() => prefix.SelectAsync("urn:air:luca.example"));
        var (self, _) = Tools(catalog, new BuyerConversation());
        await Assert.ThrowsAsync<InvalidOperationException>(() => self.SelectAsync("urn:agentcon:agent:buyer"));
        Assert.DoesNotContain(_network.Requests, request => request.Uri.Port is 5100 or 6000 or 6001);
    }

    [Fact]
    public async Task DuplicateIdentifiersAcrossCatalogsNeedAVersion()
    {
        var nested = _files.Write("nested.json",
            TempCatalogs.Agent(SupplierA, "http://localhost:6001/.well-known/agent-card.json", tags: ["wine"], version: "2.0"));
        var root = _files.Write("root.json",
            TempCatalogs.Agent(SupplierA, "http://localhost:6000/.well-known/agent-card.json", tags: ["wine"], version: "1.0"),
            TempCatalogs.Nested("urn:test:nested", new Uri(nested).AbsoluteUri));
        var (ambiguous, _) = Tools(root, new BuyerConversation());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ambiguous.SelectAsync(SupplierA));
        Assert.Contains("ambiguous", failure.Message);
        var conversation = new BuyerConversation();
        var (versioned, _) = Tools(root, conversation);
        await versioned.SelectAsync(SupplierA, "2.0");
        Assert.Equal(6001, conversation.SelectedSupplier!.Endpoint.Port);
    }

    [Theory]
    [InlineData("https://untrusted.example/a2a", "JSONRPC", "1.0")]
    [InlineData("http://localhost:6001/a2a", "JSONRPC", "1.0")]
    [InlineData("http://localhost:6000/a2a", "SLIMRPC", "1.0")]
    [InlineData("http://localhost:6000/a2a", "JSONRPC", "0.3")]
    [InlineData("ftp://localhost:6000/a2a", "JSONRPC", "1.0")]
    public void RejectsUnsupportedOrCrossOriginSupplierInterfaces(string endpoint, string binding, string version)
    {
        using var http = new HttpClient();
        var options = new BuyerOptions();
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var card = BuyerCard.Create(new BuyerOptions());
        card.SupportedInterfaces = [new AgentInterface { Url = endpoint, ProtocolBinding = binding, ProtocolVersion = version }];
        Assert.Throws<InvalidOperationException>(() =>
            gateway.SelectEndpoint(card, new Uri("http://localhost:6000/.well-known/agent-card.json")));
    }

    [Fact]
    public async Task CardWhoseInterfaceLeavesTheCatalogOriginCannotBeSelected()
    {
        var card = BuyerCard.Create(new BuyerOptions { ListenUrl = "http://localhost:6002" });
        card.SupportedInterfaces[0].Url = "http://127.0.0.1:6666/a2a";
        _network.Document("http://localhost:6002/.well-known/agent-card.json", card);
        var catalog = _files.Write("redirected.json",
            TempCatalogs.Agent("urn:test:redirected", "http://localhost:6002/.well-known/agent-card.json"));
        var conversation = new BuyerConversation();
        var (tools, _) = Tools(catalog, conversation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.SelectAsync("urn:test:redirected"));
        Assert.Null(conversation.SelectedSupplier);
    }

    [Fact]
    public async Task CardRedirectsAreNotFollowed()
    {
        _network.Route("http://localhost:6003/.well-known/agent-card.json", () =>
            new HttpResponseMessage(System.Net.HttpStatusCode.Found) { Headers = { Location = new Uri("http://localhost:6000/.well-known/agent-card.json") } });
        var catalog = _files.Write("redirect.json",
            TempCatalogs.Agent("urn:test:redirect", "http://localhost:6003/.well-known/agent-card.json"));
        var conversation = new BuyerConversation();
        var (tools, _) = Tools(catalog, conversation);
        await Assert.ThrowsAnyAsync<Exception>(() => tools.SelectAsync("urn:test:redirect"));
        Assert.Null(conversation.SelectedSupplier);
        Assert.DoesNotContain(_network.Requests, request => request.Uri.Port == 6000);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5100")]
    [InlineData("http://192.168.1.2:5100")]
    [InlineData("https://localhost:5100")]
    public void RejectsNonLoopbackBuyerHosting(string url)
    {
        Assert.Throws<InvalidOperationException>(() => new BuyerOptions { ListenUrl = url }.Validate());
    }

    [Fact]
    public void ConversationStoreIsBoundedAndKeepsOnlyRecentHistory()
    {
        var store = new BuyerConversationStore(new BuyerOptions { MaxConversations = 1 });
        var conversation = store.Get("one");
        Assert.Same(conversation, store.Get("one"));
        Assert.Throws<InvalidOperationException>(() => store.Get("two"));
        for (var i = 0; i < 20; i++)
        {
            conversation.Remember(new BuyerHistoryTurn(i.ToString(), "reply", []));
        }
        Assert.Equal(8, conversation.History.Count);
        Assert.Equal("12", conversation.History[0].Request);
    }

    private string TwoSupplierCatalog() => _files.Write("catalog.json",
        TempCatalogs.Agent("urn:agentcon:agent:buyer", "http://localhost:5100/.well-known/agent-card.json",
            "AgentCon Wine Buyer", "Wine buyer", ["wine", "buyer"]),
        TempCatalogs.Agent(SupplierA, "http://localhost:6000/.well-known/agent-card.json",
            "Luca's wines", "Italian wine supplier", ["wine", "supplier"]),
        TempCatalogs.Agent(SupplierB, "http://localhost:6001/.well-known/agent-card.json",
            "Cellar", "Wine merchant and supplier", ["wine"]),
        new { identifier = "urn:test:mcp", type = "application/mcp-server-card+json", url = "http://localhost:7000/mcp", tags = new[] { "wine" } });

    private (SupplierTurnTools Tools, BuyerTurn Turn) Tools(string catalogPath, BuyerConversation conversation,
        SupplierCommunicationMode mode = SupplierCommunicationMode.NaturalLanguage)
    {
        var options = new BuyerOptions { CatalogUrl = catalogPath, CommunicationMode = mode };
        var http = new HttpClient(_network, disposeHandler: false);
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var turn = new BuyerTurn(new Message { MessageId = "buyer", Role = Role.User, Parts = [Part.FromText("Quote")] }, conversation);
        return (new SupplierTurnTools(gateway, turn, CancellationToken.None), turn);
    }
}

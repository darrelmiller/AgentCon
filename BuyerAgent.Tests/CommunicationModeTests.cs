using A2A;
using BuyerAgent;
using System.Net;
using System.Text;
using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class CommunicationModeTests
{
    private const string Identifier = "urn:test:wine";
    private const string Rfq = """{"kind":"rfq","schemaVersion":"1","demoOnly":true,"wine":"Barolo","vintage":2019,"cases":12,"deliveryCity":"San Jose"}""";

    [Theory]
    [InlineData(SupplierCommunicationMode.NaturalLanguage)]
    [InlineData(SupplierCommunicationMode.LearnStructures)]
    [InlineData(SupplierCommunicationMode.Native)]
    public async Task ModesUseOnlyUnderstoodAdvertisedInputs(SupplierCommunicationMode mode)
    {
        using var files = new TempCatalogs();
        using var network = Network();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = mode,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var selected = await gateway.SelectAsync(Identifier, null, CancellationToken.None);
        if (mode == SupplierCommunicationMode.NaturalLanguage)
        {
            Assert.Empty(selected.Binding.Formats);
            var text = gateway.CreatePart(selected.Binding, "Quote wine", null, null);
            Assert.Equal("text/plain", text.MediaType);
            Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(selected.Binding, "Ignore mode", Rfq, SupplierFormats.Rfq));
            await gateway.SendAsync(selected.Binding, new Message { Role = Role.User, MessageId = "m", Parts = [text] }, CancellationToken.None);
        }
        else
        {
            Assert.Equal(4, selected.Binding.Formats.Count);
            Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(selected.Binding, "Quote", null, null));
            Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(selected.Binding, "", Rfq, "application/json"));
            Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(selected.Binding, "", Rfq.Replace("\"cases\":12", "\"cases\":49"), SupplierFormats.Rfq));
            Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(selected.Binding, "", Rfq, SupplierFormats.Purchase));
            var part = gateway.CreatePart(selected.Binding, "", Rfq, SupplierFormats.Rfq);
            await gateway.SendAsync(selected.Binding, new Message { Role = Role.User, MessageId = "m", Parts = [part] }, CancellationToken.None);
            Assert.Equal(SupplierFormats.Rfq, Assert.Single(network.Requests, item => item.Method == "SendMessage").Message!.Parts[0].MediaType);
            var order = gateway.CreatePart(selected.Binding, "", """{"kind":"purchase-order","schemaVersion":"1","demoOnly":true,"quoteId":"q","purchaseOrderId":"po"}""", SupplierFormats.Purchase);
            Assert.Equal(SupplierFormats.Purchase, order.MediaType);
        }
        Assert.Equal(mode == SupplierCommunicationMode.LearnStructures ? 1 : 0,
            network.Requests.Count(request => request.Uri.AbsolutePath == "/schemas/wine-v1.schema.json"));
        var accepted = Assert.Single(network.Configurations).AcceptedOutputModes;
        Assert.NotNull(accepted);
        Assert.Contains("text/plain", accepted);
        if (mode == SupplierCommunicationMode.NaturalLanguage) Assert.Equal(["text/plain"], accepted);
        else Assert.Contains(SupplierFormats.Quote, accepted);
    }

    [Fact]
    public void MessageCountersReportOnlyCallsSinceThePreviousMessage()
    {
        var counter = new MessageInferenceCounter();
        Assert.Equal(0, counter.SinceLastMessage());
        counter.Record();
        counter.Record();
        Assert.Equal(2, counter.SinceLastMessage());
        Assert.Equal(0, counter.SinceLastMessage());
        counter.Record();
        Assert.Equal(1, counter.SinceLastMessage());
    }

    [Fact]
    public async Task ModeSnapshotsAndExistingSelectionsSurviveSwitching()
    {
        using var files = new TempCatalogs();
        using var network = Network();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var natural = gateway.ForMode(options.CommunicationMode);
        var selected = (await natural.SelectAsync(Identifier, null, CancellationToken.None)).Binding;
        options.CommunicationMode = SupplierCommunicationMode.LearnStructures;
        Assert.Equal("text/plain", natural.CreatePart(selected, "Quote", null, null).MediaType);
        var learned = gateway.ForMode(options.CommunicationMode);
        var refreshed = await learned.RefreshModeAsync(selected, CancellationToken.None);
        Assert.Equal(4, refreshed.Formats.Count);
        Assert.Equal(SupplierFormats.Rfq, learned.CreatePart(refreshed, "", Rfq, SupplierFormats.Rfq).MediaType);
    }

    [Theory]
    [InlineData(SupplierCommunicationMode.Native)]
    [InlineData(SupplierCommunicationMode.LearnStructures)]
    public async Task SdkToolBindingAllowsStructuredRequestsWithoutText(SupplierCommunicationMode mode)
    {
        using var files = new TempCatalogs();
        using var network = Network();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = mode,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var conversation = new BuyerConversation();
        var tools = new SupplierTurnTools(gateway, new BuyerTurn(new Message(), conversation), CancellationToken.None);
        await tools.SelectAsync(Identifier);
        var function = CopilotTool.DefineTool(tools.SendAsync, new CopilotToolOptions { SkipPermission = true },
            new AIFunctionFactoryOptions { Name = "send_supplier_message" });
        await function.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        { ["dataJson"] = Rfq, ["mediaType"] = SupplierFormats.Rfq }));
        Assert.Equal(SupplierFormats.Rfq, Assert.Single(network.Requests, item => item.Method == "SendMessage").Message!.Parts[0].MediaType);
    }

    [Theory]
    [InlineData(SupplierCommunicationMode.Native, "Seattle", "US")]
    [InlineData(SupplierCommunicationMode.Native, "St. John's", "CA")]
    [InlineData(SupplierCommunicationMode.LearnStructures, "Toronto", "CA")]
    public async Task StructuredModesPreserveSupportedDeliveryDestinations(SupplierCommunicationMode mode, string city, string country)
    {
        using var files = new TempCatalogs();
        using var network = Network();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = mode, CatalogUrl = files.Write("catalog.json",
            TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var selected = await gateway.SelectAsync(Identifier, null, CancellationToken.None);
        var json = JsonSerializer.Serialize(new WineRfq("rfq", "1", true, "Barolo", 2019, 12, city, country),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var part = gateway.CreatePart(selected.Binding, "", json, SupplierFormats.Rfq);
        Assert.Equal(city, part.Data!.Value.GetProperty("deliveryCity").GetString());
        Assert.Equal(country, part.Data.Value.GetProperty("deliveryCountry").GetString());
        Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(selected.Binding, "",
            json.Replace($"\"{country}\"", "\"IT\"", StringComparison.Ordinal), SupplierFormats.Rfq));
    }

    [Fact]
    public async Task UnknownNativeTypesFailInsteadOfGuessing()
    {
        using var files = new TempCatalogs();
        using var network = Network();
        var card = Card();
        card.DefaultInputModes = ["application/vnd.unknown+json"];
        network.Document("http://localhost:6000/.well-known/agent-card.json", card);
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = SupplierCommunicationMode.Native,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.SelectAsync(Identifier, null, CancellationToken.None));
        Assert.Empty(network.RpcCalls("SendMessage"));
    }

    [Theory]
    [InlineData("http://untrusted.example/schema")]
    [InlineData("http://localhost:6001/schema")]
    public async Task LearnedSchemaFetchCannotLeaveSellerOrigin(string url)
    {
        using var files = new TempCatalogs();
        using var network = Network();
        var card = Card(url);
        network.Document("http://localhost:6000/.well-known/agent-card.json", card);
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = SupplierCommunicationMode.LearnStructures,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.SelectAsync(Identifier, null, CancellationToken.None));
        Assert.DoesNotContain(network.Requests, request => request.Uri.AbsoluteUri == url);
    }

    [Theory]
    [InlineData(SupplierCommunicationMode.LearnStructures)]
    [InlineData(SupplierCommunicationMode.Native)]
    public async Task InvalidTypedSellerOutputsFailTheGateway(SupplierCommunicationMode mode)
    {
        using var files = new TempCatalogs();
        using var network = Network();
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = mode,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var supplier = (await gateway.SelectAsync(Identifier, null, CancellationToken.None)).Binding;
        using var invalidHttp = new HttpClient(new OutputNetwork());
        var invalidGateway = new SupplierGateway(invalidHttp, options, new AiCatalogClient(invalidHttp, options));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalidGateway.GetTaskAsync(supplier, "bad-task", CancellationToken.None));
    }

    [Fact]
    public void NativeCodecsValidateAndRoundTripQuotesAndReceipts()
    {
        var formats = SupplierFormats.Native(Card());
        var quote = JsonSerializer.SerializeToElement(new WineQuote("quote", "1", true, "q", "Barolo", 2019, 12, 12,
            "San Jose", "USD", 14400, 2500, 175300, DateTimeOffset.UtcNow.AddMinutes(15)), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var format = formats.Single(item => item.MediaType == SupplierFormats.Quote);
        var roundTrip = SupplierFormats.Validate(format, quote, true);
        Assert.Equal(175300, roundTrip.GetProperty("totalMinor").GetInt32());
        var badTotal = JsonSerializer.Deserialize<JsonElement>(quote.GetRawText().Replace("175300", "1"));
        Assert.Throws<InvalidOperationException>(() => SupplierFormats.Validate(format, badTotal, true));
        var receipt = JsonSerializer.SerializeToElement(new WineReceipt("order-receipt", "1", true, "o", "po", "q",
            "simulated", "USD", 175300, "not-performed", "not-performed", "not-performed"),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("o", SupplierFormats.Validate(formats.Single(item => item.MediaType == SupplierFormats.Receipt), receipt, true)
            .GetProperty("orderId").GetString());
        var verifiedReceipt = JsonSerializer.Deserialize<JsonElement>(
            receipt.GetRawText().Replace("\"ageVerification\":\"not-performed\"", "\"ageVerification\":\"verified-demo-over-21\""));
        Assert.Equal("verified-demo-over-21",
            SupplierFormats.Validate(formats.Single(item => item.MediaType == SupplierFormats.Receipt), verifiedReceipt, true)
                .GetProperty("ageVerification").GetString());
        var decimalNumbers = JsonSerializer.Deserialize<JsonElement>(quote.GetRawText().Replace("\"cases\":12", "\"cases\":12.0"));
        Assert.Equal(12, SupplierFormats.Validate(format, decimalNumbers, true).GetProperty("cases").GetInt32());
    }

    [Fact]
    public void RemoteReferencesAreNeverResolved()
    {
        using var document = JsonDocument.Parse("""{"$defs":{"rfq":{"$ref":"http://untrusted.example/schema"}}}""");
        Assert.Throws<InvalidOperationException>(() => SupplierFormats.Build(SupplierFormats.Rfq, "input", document.RootElement, "rfq"));
    }

    [Fact]
    public async Task LearnedTypesAreDerivedFromSellerSchemaNotWineCodecs()
    {
        const string mediaType = "application/vnd.test.offer+json";
        using var files = new TempCatalogs();
        using var network = Network();
        var card = Card();
        card.DefaultInputModes = [mediaType];
        card.DefaultOutputModes = ["text/plain"];
        card.Capabilities.Extensions = [new AgentExtension { Uri = SupplierFormats.Extension, Params = JsonSerializer.SerializeToElement(new
        {
            schemas = new[] { new { mediaType, url = "http://localhost:6000/schemas/offer.json", definition = "offer", direction = "input" } }
        }) }];
        network.Document("http://localhost:6000/.well-known/agent-card.json", card)
            .Route("http://localhost:6000/schemas/offer.json", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                """{"$defs":{"offer":{"type":"object","additionalProperties":false,"required":["quantity"],"properties":{"quantity":{"type":"integer","minimum":1,"maximum":3}}}}}""",
                Encoding.UTF8, "application/schema+json") });
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = SupplierCommunicationMode.LearnStructures,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        var binding = (await gateway.SelectAsync(Identifier, null, CancellationToken.None)).Binding;
        Assert.Throws<InvalidOperationException>(() => gateway.CreatePart(binding, "", """{"quantity":4}""", mediaType));
        var part = gateway.CreatePart(binding, "", """{"quantity":2}""", mediaType);
        await gateway.SendAsync(binding, new Message { MessageId = "m", Role = Role.User, Parts = [part] }, CancellationToken.None);
        Assert.Equal(mediaType, Assert.Single(network.Requests, item => item.Method == "SendMessage").Message!.Parts[0].MediaType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedOrRedirectedSchemaFailsSelection(bool oversized)
    {
        using var files = new TempCatalogs();
        using var network = Network();
        network.Route("http://localhost:6000/schemas/wine-v1.schema.json", () => oversized
            ? new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(new string('x', 65537), Encoding.UTF8, "application/schema+json") }
            : new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://untrusted.example/schema") } });
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CommunicationMode = SupplierCommunicationMode.LearnStructures,
            CatalogUrl = files.Write("catalog.json", TempCatalogs.Agent(Identifier, "http://localhost:6000/.well-known/agent-card.json")) };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        if (oversized) await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.SelectAsync(Identifier, null, CancellationToken.None));
        else await Assert.ThrowsAsync<HttpRequestException>(() => gateway.SelectAsync(Identifier, null, CancellationToken.None));
        Assert.Empty(network.RpcCalls("SendMessage"));
    }

    [Fact]
    public void ModeConfigurationIsValidated()
    {
        Assert.Equal(SupplierCommunicationMode.NaturalLanguage, new BuyerOptions().CommunicationMode);
        Assert.Throws<InvalidOperationException>(() => new BuyerOptions { CommunicationMode = (SupplierCommunicationMode)99 }.Validate());
    }

    internal static AgentCard Card(string url = "http://localhost:6000/schemas/wine-v1.schema.json")
    {
        var card = BuyerCard.Create(new BuyerOptions { ListenUrl = "http://localhost:6000" });
        card.DefaultInputModes = ["text/plain", SupplierFormats.Rfq, SupplierFormats.Purchase];
        card.DefaultOutputModes = ["text/plain", SupplierFormats.Quote, SupplierFormats.Receipt];
        card.Capabilities.Extensions = [new AgentExtension { Uri = SupplierFormats.Extension, Params = JsonSerializer.SerializeToElement(new
        {
            schemas = new[]
            {
                new { mediaType = SupplierFormats.Rfq, url, definition = "rfq", direction = "input" },
                new { mediaType = SupplierFormats.Purchase, url, definition = "purchaseOrder", direction = "input" },
                new { mediaType = SupplierFormats.Quote, url, definition = "quote", direction = "output" },
                new { mediaType = SupplierFormats.Receipt, url, definition = "receipt", direction = "output" }
            }
        }) }];
        return card;
    }

    private static FakeNetwork Network()
    {
        using var stream = typeof(SupplierFormats).Assembly.GetManifestResourceStream("BuyerAgent.WineSchema.json")!;
        var schema = new StreamReader(stream).ReadToEnd();
        return new FakeNetwork().Document("http://localhost:6000/.well-known/agent-card.json", Card())
            .Route("http://localhost:6000/schemas/wine-v1.schema.json", () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(schema, Encoding.UTF8, "application/schema+json") });
    }

    private sealed class OutputNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(FakeNetwork.Json(new { jsonrpc = "2.0", id = 1, result = new AgentTask
            {
                Id = "bad-task", Status = new A2A.TaskStatus { State = TaskState.Completed },
                Artifacts = [new Artifact { ArtifactId = "bad", Parts = [new Part
                { MediaType = SupplierFormats.Quote, Data = JsonSerializer.SerializeToElement(new { kind = "quote", demoOnly = false }) }] }]
            } }));
    }
}

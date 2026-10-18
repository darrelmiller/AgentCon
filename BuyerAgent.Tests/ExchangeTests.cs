using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using A2A;
using BuyerAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class ExchangeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ActualSdkWireBodiesAreCapturedWithoutHeadersAndTasksAreObserved()
    {
        var options = new BuyerOptions { ExchangeCaptureEnabled = true };
        var store = new ExchangeStore(options);
        string? wireRequest = null;
        var wireResponse = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = "test", result = new
            {
                task = new AgentTask
                {
                    Id = "seller-task", ContextId = "seller-context",
                    Status = new A2A.TaskStatus { State = TaskState.Completed },
                    Artifacts = [new Artifact { ArtifactId = "quote", Name = "Quote",
                        Parts = [Part.FromData(JsonSerializer.SerializeToElement(new { kind = "quote", totalMinor = 175300 }))] }]
                }
            }
        }, A2AJsonUtilities.DefaultOptions);
        using var client = Client(store, async (request, ct) =>
        {
            wireRequest = await request.Content!.ReadAsStringAsync(ct);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wireResponse, Encoding.UTF8, "application/json") };
            response.Headers.Add("Set-Cookie", "SECRET-RESPONSE");
            return response;
        });
        client.DefaultRequestHeaders.Authorization = new("Bearer", "SECRET-REQUEST");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ExchangeTests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("ExchangeTests");
        using var activity = source.StartActivity("wire");
        using var sdk = new A2AClient(new Uri("http://localhost:6000/a2a"), client);
        var result = await sdk.SendMessageAsync(BuyerServiceTests.Request("Quote wine", "buyer-context", """{"kind":"rfq"}"""));
        Assert.Equal(TaskState.Completed, result.Task!.Status.State);
        var snapshot = Snapshot(store);
        var exchange = Assert.Single(snapshot.GetProperty("exchanges").EnumerateArray());
        Assert.Equal(wireRequest, exchange.GetProperty("request").GetString());
        Assert.Equal(wireResponse, exchange.GetProperty("response").GetString());
        Assert.Equal(activity!.TraceId.ToHexString(), exchange.GetProperty("traceId").GetString());
        Assert.Equal("complete", exchange.GetProperty("status").GetString());
        Assert.Equal(200, exchange.GetProperty("httpStatus").GetInt32());
        Assert.DoesNotContain("SECRET", snapshot.ToString());
        var task = Assert.Single(snapshot.GetProperty("tasks").EnumerateArray());
        Assert.Equal("seller-task", task.GetProperty("id").GetString());
        Assert.Equal("TASK_STATE_COMPLETED", task.GetProperty("state").GetString());
    }

    [Fact]
    public async Task HttpJsonSdkCallsAreCapturedIncludingBodylessTaskReadsAndObservedPerInterface()
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        var task = new AgentTask
        {
            Id = "seller-task", ContextId = "seller-context",
            Status = new A2A.TaskStatus { State = TaskState.InputRequired, Message = new Message
            {
                Role = Role.Agent, MessageId = "reply", TaskId = "seller-task", Parts = [Part.FromText("How many cases?")]
            } }
        };
        using var client = Client(store, (request, _) =>
        {
            object body = request.Method == HttpMethod.Get
                ? new AgentTask { Id = "seller-task", ContextId = "seller-context", Status = new A2A.TaskStatus { State = TaskState.Completed } }
                : new SendMessageResponse { Task = task };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body, A2AJsonUtilities.DefaultOptions), Encoding.UTF8, "application/json")
            });
        });
        using var sdk = new A2AHttpJsonClient(new Uri("http://localhost:6000/a2a/rest"), client);
        await sdk.SendMessageAsync(BuyerServiceTests.Request("Quote wine", "buyer-context", """{"kind":"rfq"}"""));
        await sdk.GetTaskAsync(new GetTaskRequest { Id = "seller-task" });
        var snapshot = Snapshot(store);
        var exchanges = snapshot.GetProperty("exchanges").EnumerateArray().ToArray();
        Assert.Equal(["SendMessage", "GetTask"], exchanges.Select(entry => entry.GetProperty("restOperation").GetString()));
        Assert.StartsWith("GET /a2a/rest/tasks/seller-task", exchanges[1].GetProperty("request").GetString());
        var observed = Assert.Single(snapshot.GetProperty("tasks").EnumerateArray());
        Assert.Equal("http://localhost:6000/a2a/rest", observed.GetProperty("endpoint").GetString());
        Assert.Equal("TASK_STATE_COMPLETED", observed.GetProperty("state").GetString());
        Assert.Null(ExchangeStore.RestOperation(HttpMethod.Get, new Uri("http://localhost:6000/.well-known/agent-card.json")));
        Assert.Null(ExchangeStore.RestOperation(HttpMethod.Post, new Uri("http://localhost:6000/a2a")));
        Assert.Equal("CancelTask", ExchangeStore.RestOperation(HttpMethod.Post, new Uri("http://localhost:6000/a2a/rest/tasks/t:cancel")));
    }

    [Fact]
    public async Task CaptureIsOptInAndDiscoveryIsNotCaptured()
    {
        foreach (var enabled in new[] { false, true })
        {
            var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = enabled });
            using var http = Client(store, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{}") }));
            using var card = await http.GetAsync("http://localhost:6000/.well-known/agent-card.json");
            if (!enabled)
            {
                using var response = await http.PostAsync("http://localhost:6000/a2a", new StringContent("PRIVATE"));
            }
            Assert.Empty(Snapshot(store).GetProperty("exchanges").EnumerateArray());
        }
    }

    [Fact]
    public async Task TruncationBoundsMemoryWithoutChangingRequestOrResponseDelivery()
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        var body = new string('x', ExchangeStore.MaxBodyBytes + 2000);
        using var http = Client(store, async (request, ct) =>
        {
            Assert.Equal(body, await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        });
        using var response = await http.PostAsync("http://localhost:6000/a2a", new StringContent(body));
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        var exchange = Assert.Single(Snapshot(store).GetProperty("exchanges").EnumerateArray());
        Assert.Equal(ExchangeStore.MaxBodyBytes, exchange.GetProperty("request").GetString()!.Length);
        Assert.Equal(ExchangeStore.MaxBodyBytes, exchange.GetProperty("response").GetString()!.Length);
        Assert.True(exchange.GetProperty("requestTruncated").GetBoolean());
        Assert.True(exchange.GetProperty("responseTruncated").GetBoolean());
    }

    [Fact]
    public async Task StreamingEventsAreVisibleBeforeCompletionWithoutEagerBuffering()
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        var pipe = new Pipe();
        using var http = Client(store, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(pipe.Reader.AsStream())
            { Headers = { ContentType = new("text/event-stream") } }
        }));
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:6000/a2a") { Content = new StringContent("{}") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var stream = await response.Content.ReadAsStreamAsync();
        foreach (var state in new[] { "WORKING", "COMPLETED" })
        {
            var frame = Encoding.UTF8.GetBytes($"data: {{\"result\":{{\"statusUpdate\":{{\"taskId\":\"stream-task\",\"contextId\":\"stream-context\",\"status\":{{\"state\":\"TASK_STATE_{state}\"}}}}}}}}\r\n\r\n");
            await pipe.Writer.WriteAsync(frame);
            var delivered = new byte[frame.Length];
            await stream.ReadExactlyAsync(delivered);
            Assert.Equal(frame, delivered);
            var snapshot = Snapshot(store);
            Assert.Contains($"TASK_STATE_{state}", snapshot.GetProperty("exchanges")[0].GetProperty("response").GetString());
            Assert.Equal($"TASK_STATE_{state}", snapshot.GetProperty("tasks")[0].GetProperty("state").GetString());
            Assert.Equal("receiving", snapshot.GetProperty("exchanges")[0].GetProperty("status").GetString());
        }
        await pipe.Writer.CompleteAsync();
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        Assert.Equal("complete", Snapshot(store).GetProperty("exchanges")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task PartialConsumptionAndTransportFailuresAreExplicit()
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        using (var http = Client(store, (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("body") })))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost:6000/a2a");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
        using (var http = Client(store, (_, _) => throw new HttpRequestException("SECRET FAILURE")))
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync("http://localhost:6000/a2a", new StringContent("{}")));
        }
        var exchanges = Snapshot(store).GetProperty("exchanges");
        Assert.Equal("response not fully consumed", exchanges[0].GetProperty("status").GetString());
        Assert.Equal("transport failed", exchanges[1].GetProperty("status").GetString());
        Assert.DoesNotContain("SECRET", exchanges.ToString());
    }

    [Theory]
    [InlineData("""{"params":42}""")]
    [InlineData("""{"params":{"message":null}}""")]
    [InlineData("""{"result":{"task":42}}""")]
    [InlineData("not JSON")]
    public async Task MalformedWireBodiesRemainVisibleAndAreNotReinterpreted(string body)
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        using var http = Client(store, async (request, ct) =>
        {
            Assert.Equal(body, await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(body) };
        });
        using var response = await http.PostAsync("http://localhost:6000/a2a", new StringContent(body));
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        var exchange = Snapshot(store).GetProperty("exchanges")[0];
        Assert.Equal(body, exchange.GetProperty("response").GetString());
        Assert.Equal(400, exchange.GetProperty("httpStatus").GetInt32());
    }

    [Fact]
    public void AuthRequiredSellerTaskExposesOnlyHttpsPresentationPageUntilAuthorized()
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        var endpoint = new Uri("http://localhost:6000/a2a");
        string Pending(string url) => """{"result":{"task":{"id":"po","contextId":"c","status":{"state":"TASK_STATE_AUTH_REQUIRED","message":{"metadata":{"agentcon.presentation":{"presentationUrl":"URL"}}}}}}}""".Replace("URL", url);
        JsonElement Task() => Assert.Single(Snapshot(store).GetProperty("tasks").EnumerateArray());

        store.Body(store.Begin(endpoint), true, Pending("https://issuer.example/presentations/abc"), false, false);
        Assert.Equal("https://issuer.example/presentations/abc", Task().GetProperty("presentationUrl").GetString());

        store.Body(store.Begin(endpoint), true, """{"result":{"task":{"id":"po","contextId":"c","status":{"state":"TASK_STATE_COMPLETED"}}}}""", false, false);
        Assert.Equal(JsonValueKind.Null, Task().GetProperty("presentationUrl").ValueKind);

        store.Clear();
        store.Body(store.Begin(endpoint), true, Pending("http://issuer.example/presentations/abc"), false, false);
        Assert.Equal(JsonValueKind.Null, Task().GetProperty("presentationUrl").ValueKind);
    }

    [Fact]
    public void EvictionAndClearPreventLateExchangesFromRepopulatingContent()
    {
        var store = new ExchangeStore(new BuyerOptions { ExchangeCaptureEnabled = true });
        var endpoint = new Uri("http://localhost:6000/a2a");
        for (var i = 0; i < ExchangeStore.MaxExchanges + 3; i++) store.Begin(endpoint);
        Assert.Equal(ExchangeStore.MaxExchanges, Snapshot(store).GetProperty("exchanges").GetArrayLength());
        Assert.Equal(3, Snapshot(store).GetProperty("droppedExchanges").GetInt32());
        var key = store.Begin(endpoint);
        store.BuyerTask("buyer-task", "context", "TASK_STATE_WORKING");
        store.Clear();
        store.Body(key, true, """{"result":{"task":{"id":"late","status":{"state":"TASK_STATE_COMPLETED"}}}}""", false, false);
        store.Status(key, "complete");
        Assert.Empty(Snapshot(store).GetProperty("exchanges").EnumerateArray());
        Assert.Empty(Snapshot(store).GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task DashboardIsDisabledByDefaultAndRejectsCrossOriginAccess()
    {
        await using (var disabled = await App(false))
        {
            using var http = disabled.GetTestClient();
            http.BaseAddress = new("http://localhost:5100");
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/demo")).StatusCode);
        }
        await using var app = await App(true);
        using var client = app.GetTestClient();
        client.BaseAddress = new("http://localhost:5100");
        using var page = await client.GetAsync("/demo");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Message inspector", html);
        Assert.DoesNotContain("Demo content capture is ON.", html);
        Assert.DoesNotContain("class=\"notice\"", html);
        Assert.Contains("aria-describedby=\"capture-tip\"", html);
        Assert.Contains("aria-describedby=\"native-tip\"", html);
        Assert.Contains("aria-describedby=\"structures-tip\"", html);
        Assert.Contains("role=\"tooltip\"", html);
        Assert.Contains("tip:focus-within", html);
        Assert.Contains("const openTasks", html);
        Assert.Contains("LLM calls:", html);
        Assert.DoesNotContain("{{NONCE}}", html);
        client.DefaultRequestHeaders.Add("Origin", "http://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/demo/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/demo/clear", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/demo/clear", null)).StatusCode);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:5100");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/demo/clear", null)).StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Host = "attacker.example:5100";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/demo/state")).StatusCode);
    }

    [Fact]
    public async Task BuyerTasksReflectTheRealHandlerLifecycle()
    {
        await using var app = await App(true);
        using var http = app.GetTestClient();
        using var sdk = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var response = await sdk.SendMessageAsync(BuyerServiceTests.Request("No inference", "buyer-context"));
        var task = Assert.Single(Snapshot(app.Services.GetRequiredService<ExchangeStore>()).GetProperty("tasks").EnumerateArray());
        Assert.Equal(response.Task!.Id, task.GetProperty("id").GetString());
        Assert.Equal("TASK_STATE_COMPLETED", task.GetProperty("state").GetString());
    }

    [Fact]
    public async Task BuyerTaskIsVisibleWhileWorkIsStillRunning()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = BuyerApplication.Build(["--Buyer:ExchangeCaptureEnabled=true"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.RemoveAll<IBuyerHarness>();
            builder.Services.AddSingleton<IBuyerHarness>(new WaitingHarness(started, finish));
        });
        await app.StartAsync();
        using var http = app.GetTestClient();
        using var sdk = new A2AClient(new Uri(http.BaseAddress!, "/a2a"), http);
        var send = sdk.SendMessageAsync(BuyerServiceTests.Request("Wait", "active-context"));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var task = Assert.Single(Snapshot(app.Services.GetRequiredService<ExchangeStore>()).GetProperty("tasks").EnumerateArray());
            Assert.Equal("TASK_STATE_WORKING", task.GetProperty("state").GetString());
            Assert.Equal("active-context", task.GetProperty("contextId").GetString());
        }
        finally { finish.TrySetResult(); }
        Assert.Equal(TaskState.Completed, (await send).Task!.Status.State);
    }

    private static JsonElement Snapshot(ExchangeStore store) => JsonSerializer.SerializeToElement(store.Snapshot(), JsonOptions);

    [Fact]
    public async Task DashboardChangesNativeSupportAndLiveCardWithoutRestarting()
    {
        await using var app = await App(true);
        using var http = app.GetTestClient();
        http.BaseAddress = new("http://localhost:5100");
        var before = await new A2ACardResolver(http.BaseAddress, http).GetAgentCardAsync();
        Assert.Equal(["text/plain"], before.DefaultInputModes);
        Assert.DoesNotContain("application/json", before.DefaultOutputModes);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/demo/native", new { enabled = true })).StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "http://localhost:5100");
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/demo/mode", new { mode = "Native" })).StatusCode);
        foreach (var invalid in new[] { "{}", """{"enabled":"true"}""", """{"enabled":true,"mode":"Native"}""" })
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/demo/native",
                new StringContent(invalid, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/demo/native", new { enabled = true })).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        var card = await new A2ACardResolver(http.BaseAddress, http).GetAgentCardAsync();
        Assert.Contains(SupplierFormats.Quote, card.DefaultOutputModes);
        Assert.Contains(SupplierFormats.Rfq, card.DefaultInputModes);
        Assert.Contains(SupplierFormats.Purchase, card.Skills[0].InputModes!);
        Assert.Contains(card.Capabilities.Extensions!, item => item.Uri == SupplierFormats.Extension);
        http.DefaultRequestHeaders.Add("Origin", "http://localhost:5100");
        await http.PostAsJsonAsync("/demo/native", new { enabled = false });
        http.DefaultRequestHeaders.Remove("Origin");
        var off = await new A2ACardResolver(http.BaseAddress, http).GetAgentCardAsync();
        Assert.Equal(["text/plain"], off.DefaultInputModes);
        Assert.DoesNotContain(off.Capabilities.Extensions!, item => item.Uri == SupplierFormats.Extension);
        using var sdk = new A2AClient(new Uri(http.BaseAddress, "/a2a"), http);
        var task = (await sdk.SendMessageAsync(BuyerServiceTests.Request("hello"))).Task!;
        Assert.All(task.Artifacts!.SelectMany(artifact => artifact.Parts), part => Assert.Equal(PartContentCase.Text, part.ContentCase));
    }

    private static HttpClient Client(ExchangeStore store, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ExchangeCaptureHandler(store) { InnerHandler = new Network(send) });

    [Fact]
    public async Task CardPopupReadsOnlyCatalogSellersAndPreservesFullJson()
    {
        using var files = new TempCatalogs();
        using var network = new FakeNetwork().Document("http://localhost:6000/.well-known/agent-card.json",
            new { name = "<img src=x onerror=alert(1)>", customExtension = new { schemaUrl = "/schemas/wine-v1.schema.json" } });
        var catalog = files.Write("catalog.json", TempCatalogs.Agent("urn:test:seller", "http://localhost:6000/.well-known/agent-card.json"));
        await using var app = BuyerApplication.Build(["--Buyer:ExchangeCaptureEnabled=true", $"--Buyer:CatalogUrl={catalog}"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpClient<ExchangeDashboard>().ConfigurePrimaryHttpMessageHandler(() => network);
        });
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.BaseAddress = new("http://localhost:5100");
        using var buyer = await http.GetAsync("/demo/agent-card");
        Assert.Equal("no-store", buyer.Headers.CacheControl!.ToString());
        var buyerCard = await buyer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("text/plain", buyerCard.GetProperty("defaultOutputModes")[0].GetString());
        var seller = await http.GetFromJsonAsync<JsonElement>("/demo/agent-card?identifier=urn:test:seller");
        Assert.Equal("<img src=x onerror=alert(1)>", seller.GetProperty("name").GetString());
        Assert.Equal("/schemas/wine-v1.schema.json", seller.GetProperty("customExtension").GetProperty("schemaUrl").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/demo/agent-card?identifier=http://attacker.example")).StatusCode);
        Assert.Single(network.Requests);
        using var catalogResponse = await http.GetAsync("/demo/catalog");
        Assert.Equal(HttpStatusCode.OK, catalogResponse.StatusCode);
        Assert.Equal("no-store", catalogResponse.Headers.CacheControl!.ToString());
        var catalogJson = await catalogResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0", catalogJson.GetProperty("specVersion").GetString());
        Assert.Equal("urn:test:seller", catalogJson.GetProperty("entries")[0].GetProperty("identifier").GetString());
        Assert.Single(network.Requests);
        network.Raw("http://localhost:6000/.well-known/agent-card.json", new string('x', 65537));
        Assert.Equal(HttpStatusCode.BadGateway, (await http.GetAsync("/demo/agent-card?identifier=urn:test:seller")).StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/demo/agent-card")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/demo/catalog")).StatusCode);
    }

    [Fact]
    public async Task DashboardReadsAgentTotalsAndClearResetsBothAgentsWithoutResettingSuppliers()
    {
        using var files = new TempCatalogs();
        using var network = new FakeNetwork().Raw("http://localhost:6000/health",
            """{"structuredDataParts":true,"llm":{"instanceId":"supplier","calls":3,"responseTimeMs":1200,"timedCalls":3,"complete":true}}""");
        var catalog = files.Write("catalog.json", TempCatalogs.Agent("urn:test:seller", "http://localhost:6000/.well-known/agent-card.json"));
        await using var app = BuyerApplication.Build(["--Buyer:ExchangeCaptureEnabled=true", $"--Buyer:CatalogUrl={catalog}"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddHttpClient<ExchangeDashboard>().ConfigurePrimaryHttpMessageHandler(() => network);
        });
        await app.StartAsync();
        var store = app.Services.GetRequiredService<ExchangeStore>();
        store.BuyerLlm.Call();
        store.BuyerLlm.Duration(250);
        using var http = app.GetTestClient();
        http.BaseAddress = new("http://localhost:5100");
        var state = await http.GetFromJsonAsync<JsonElement>("/demo/state");
        Assert.Equal(1, state.GetProperty("buyer").GetProperty("llm").GetProperty("calls").GetInt64());
        Assert.Equal(250, state.GetProperty("buyer").GetProperty("llm").GetProperty("responseTimeMs").GetDouble());
        Assert.Equal(3, state.GetProperty("sellers")[0].GetProperty("llm").GetProperty("calls").GetInt64());
        http.DefaultRequestHeaders.Add("Origin", "http://localhost:5100");
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("/demo/clear", null)).StatusCode);
        state = await http.GetFromJsonAsync<JsonElement>("/demo/state");
        Assert.Equal(0, state.GetProperty("buyer").GetProperty("llm").GetProperty("calls").GetInt64());
        Assert.Equal(0, state.GetProperty("sellers")[0].GetProperty("llm").GetProperty("calls").GetInt64());
        network.Raw("http://localhost:6000/health",
            """{"llm":{"instanceId":"supplier","calls":5,"responseTimeMs":2200,"timedCalls":5,"complete":true}}""");
        state = await http.GetFromJsonAsync<JsonElement>("/demo/state");
        var seller = state.GetProperty("sellers")[0].GetProperty("llm");
        Assert.Equal(2, seller.GetProperty("calls").GetInt64());
        Assert.Equal(1000, seller.GetProperty("responseTimeMs").GetDouble());
        Assert.All(network.Requests, request => Assert.Null(request.Method));
    }

    private sealed class Network(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static async Task<WebApplication> App(bool enabled)
    {
        var app = BuyerApplication.Build([$"--Buyer:ExchangeCaptureEnabled={enabled}"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.RemoveAll<IBuyerHarness>();
            builder.Services.AddSingleton<IBuyerHarness, NoInference>();
        });
        await app.StartAsync();
        return app;
    }

    private sealed class NoInference : IBuyerHarness
    {
        public Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken) => Task.FromResult("Demo reply");
    }

    private sealed class WaitingHarness(TaskCompletionSource started, TaskCompletionSource finish) : IBuyerHarness
    {
        public async Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken)
        {
            started.SetResult();
            await finish.Task.WaitAsync(cancellationToken);
            return "Finished";
        }
    }
}

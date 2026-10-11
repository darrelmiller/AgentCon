using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using BuyerAgent;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public void DisablingCliTelemetryPreservesInheritedRuntimeEnvironment()
    {
        var environment = CopilotRuntime.CreateEnvironment();
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), environment["PATH"]);
        Assert.Equal("false", environment["COPILOT_OTEL_ENABLED"]);
        Assert.Equal("false", environment["OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"]);
    }

    [Fact]
    public void AgentAndActualUsageHaveConventionalShapesAndNoContent()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = Listen(spans);
        ActivityContext parent;
        using (var agent = BuyerTelemetry.Agent("task-test", "context-test"))
        {
            parent = agent!.Context;
            var usage = new CopilotUsageTrace(parent, "BuyerAgent");
            var fixture = JsonSerializer.SerializeToElement(new
            {
                type = "assistant.usage",
                timestamp = "2026-10-10T20:00:00Z",
                data = new { model = "observed-model", duration = 123, inputTokens = 42,
                    outputTokens = 7, content = "PRIVATE", prompt = "PRIVATE" }
            });
            var sdkEvent = fixture.Deserialize<GitHub.Copilot.AssistantUsageEvent>(CopilotUsageTrace.JsonOptions)!;
            usage.Record(sdkEvent);
            usage.Record(JsonSerializer.SerializeToElement(new
            {
                type = "assistant.usage", data = new { model = "other-model" }
            }));
            usage.Record(JsonSerializer.SerializeToElement(new { type = "assistant.message", data = new { content = "PRIVATE" } }));
        }
        var agentSpan = Assert.Single(spans, s => s.DisplayName == "invoke_agent BuyerAgent" && s.TraceId == parent.TraceId);
        Assert.Equal(ActivityKind.Internal, agentSpan.Kind);
        Assert.Equal("invoke_agent", agentSpan.GetTagItem("gen_ai.operation.name"));
        var chat = Assert.Single(spans, s => s.DisplayName == "chat observed-model");
        Assert.Equal(ActivityKind.Client, chat.Kind);
        Assert.Equal(parent.TraceId, chat.TraceId);
        Assert.Equal(parent.SpanId, chat.ParentSpanId);
        Assert.Equal(42L, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(7L, chat.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(TimeSpan.FromMilliseconds(123), chat.Duration);
        Assert.Null(Assert.Single(spans, s => s.DisplayName == "chat other-model").GetTagItem("gen_ai.usage.input_tokens"));
        Assert.DoesNotContain(spans.SelectMany(s => s.TagObjects), tag => tag.Value?.ToString()?.Contains("PRIVATE") == true);
    }

    [Theory]
    [InlineData(false, "System.InvalidOperationException")]
    [InlineData(true, "canceled")]
    public void ErrorsHaveStatusAndSafeTypeOnly(bool canceled, string expected)
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = Listen(spans);
        ActivityTraceId trace;
        using (var activity = BuyerTelemetry.Agent("task", "context"))
        {
            trace = activity!.TraceId;
            BuyerTelemetry.Error(activity, canceled ? new OperationCanceledException("PRIVATE") : new InvalidOperationException("PRIVATE"));
        }
        var span = Assert.Single(spans, s => s.TraceId == trace);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(expected, span.GetTagItem("error.type"));
        Assert.Null(span.StatusDescription);
        Assert.Empty(span.Events);
    }

    [Fact]
    public async Task ToolsRetainCapturedParentWhenSdkCallbackHasNoAmbientActivity()
    {
        var spans = new ConcurrentBag<Activity>();
        using var listener = Listen(spans);
        using var network = new FakeNetwork().Raw("http://localhost:7000/catalog.json", TempCatalogs.Catalog());
        using var http = new HttpClient(network);
        var options = new BuyerOptions { CatalogUrl = "http://localhost:7000/catalog.json" };
        var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
        ActivityContext parent;
        SupplierTurnTools tools;
        using (var agent = BuyerTelemetry.Agent("tools", "tools"))
        {
            parent = agent!.Context;
            tools = new SupplierTurnTools(gateway,
                new BuyerTurn(new A2A.Message(), new BuyerConversation()), CancellationToken.None);
        }
        await tools.SearchAsync("PRIVATE");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.SelectAsync("missing"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.SendAsync("PRIVATE"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.GetTaskAsync("missing"));
        var toolSpans = spans.Where(s => s.GetTagItem("gen_ai.operation.name")?.ToString() == "execute_tool" &&
            s.TraceId == parent.TraceId).ToArray();
        Assert.Equal(4, toolSpans.Length);
        Assert.All(toolSpans, s =>
        {
            Assert.Equal(ActivityKind.Internal, s.Kind);
            Assert.Equal(parent.SpanId, s.ParentSpanId);
            Assert.Equal("BuyerAgent", s.GetTagItem("gen_ai.agent.name"));
        });
        Assert.Equal(3, toolSpans.Count(s => s.Status == ActivityStatusCode.Error));
        Assert.Contains(spans, s => s.DisplayName == "catalog.fetch" && s.TraceId == parent.TraceId);
        Assert.DoesNotContain(toolSpans.SelectMany(s => s.TagObjects), tag => tag.Value?.ToString()?.Contains("PRIVATE") == true);
    }

    internal static ActivityListener Listen(ConcurrentBag<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BuyerTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

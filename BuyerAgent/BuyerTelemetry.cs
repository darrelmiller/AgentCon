using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace BuyerAgent;

public static class BuyerTelemetry
{
    public const string SourceName = "AgentCon.Buyer";
    public static readonly ActivitySource Source = new(SourceName,
        typeof(BuyerTelemetry).Assembly.GetName().Version?.ToString());

    public static void Configure(WebApplicationBuilder builder)
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;
        builder.Services.AddSingleton<OtelDiagnostics>();
        builder.Services.AddHostedService<OtelDiagnostics>(services => services.GetRequiredService<OtelDiagnostics>());
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "agentcon-buyer",
                serviceVersion: typeof(BuyerTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion))
            .WithTracing(tracing =>
            {
                tracing.AddSource(SourceName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")) ||
                    !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")))
                {
                    tracing.AddOtlpExporter();
                }
                else
                {
                    Console.Error.WriteLine("Buyer OTLP export deliberately disabled: no OTEL_EXPORTER_OTLP_ENDPOINT configured.");
                }
            });
    }

    public static Activity? Agent(string taskId, string contextId)
    {
        var activity = Source.StartActivity("invoke_agent BuyerAgent", ActivityKind.Internal,
            Activity.Current?.Context ?? default, tags:
        [
            new("gen_ai.operation.name", "invoke_agent"),
            new("gen_ai.agent.name", "BuyerAgent"),
            new("gen_ai.provider.name", "github.copilot"),
            new("agentcon.a2a.task.id", taskId),
            new("agentcon.a2a.context.id", contextId)
        ]);
        return activity;
    }

    public static void Error(Activity? activity, Exception exception) =>
        Error(activity, exception is OperationCanceledException ? "canceled" :
            exception is TimeoutException ? "timeout" : exception.GetType().FullName!);

    public static void Error(Activity? activity, string type)
    {
        activity?.SetStatus(ActivityStatusCode.Error);
        activity?.SetTag("error.type", type);
    }
}

// The exporter reports failures via EventSource, not the application's ILogger.
public sealed class OtelDiagnostics : EventListener, IHostedService
{
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
            EnableEvents(source, EventLevel.Warning);
    }

    protected override void OnEventWritten(EventWrittenEventArgs data)
    {
        // Do not print payloads: exporter exceptions can contain endpoints or headers.
        Console.Error.WriteLine($"OpenTelemetry diagnostic: {data.EventSource.Name}/{data.EventName} ({data.Level}). Check collector connectivity/configuration.");
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Creates one chat span per actual SDK assistant.usage event, not per agent-loop wrapper.</summary>
public sealed class CopilotUsageTrace(ActivityContext parent, string agentName)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Record(GitHub.Copilot.AssistantUsageEvent evt) => Record(
        JsonSerializer.SerializeToElement(new
        {
            type = "assistant.usage",
            timestamp = evt.Timestamp,
            data = evt.Data
        }, JsonOptions));

    public void Record(JsonElement evt)
    {
        if (!evt.TryGetProperty("type", out var type) || type.GetString() != "assistant.usage" ||
            !evt.TryGetProperty("data", out var data)) return;
        var model = data.TryGetProperty("model", out var modelValue) ? modelValue.GetString() : null;
        var end = evt.TryGetProperty("timestamp", out var timestamp) &&
                  timestamp.TryGetDateTimeOffset(out var reported) ? reported : DateTimeOffset.UtcNow;
        var start = data.TryGetProperty("duration", out var duration) && duration.TryGetInt64(out var ms) && ms >= 0
            ? end.AddMilliseconds(-ms) : end;
        using var activity = BuyerTelemetry.Source.StartActivity(
            model is null ? "chat" : $"chat {model}", ActivityKind.Client, parent, tags:
            [
                new("gen_ai.operation.name", "chat"),
                new("gen_ai.agent.name", agentName),
                new("gen_ai.provider.name", "github.copilot"),
                new("gen_ai.request.model", model),
                new("gen_ai.response.model", model),
                new("agentcon.inference.observation", "sdk.assistant.usage")
            ], startTime: start);
        if (activity is null) return;
        foreach (var (field, attribute) in new[]
        {
            ("inputTokens", "gen_ai.usage.input_tokens"),
            ("outputTokens", "gen_ai.usage.output_tokens"),
            ("cacheReadTokens", "gen_ai.usage.cache_read.input_tokens"),
            ("cacheWriteTokens", "gen_ai.usage.cache_write.input_tokens"),
            ("reasoningTokens", "gen_ai.usage.reasoning.output_tokens")
        })
        {
            if (data.TryGetProperty(field, out var value) && value.TryGetInt64(out var tokens) && tokens >= 0)
                activity.SetTag(attribute, tokens);
        }
        if (data.TryGetProperty("finishReason", out var finish) && finish.GetString() is { } reason)
        {
            activity.SetTag("gen_ai.response.finish_reasons", new[] { reason });
            if (reason == "error") BuyerTelemetry.Error(activity, "inference_error");
        }
        activity.SetEndTime(end.UtcDateTime);
    }
}

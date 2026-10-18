using System.Text.Json.Serialization;

namespace BuyerAgent;

public sealed record AgentLlmTotals(
    [property: JsonRequired] string InstanceId,
    [property: JsonRequired] long Calls,
    [property: JsonRequired] double ResponseTimeMs,
    [property: JsonRequired] long TimedCalls,
    [property: JsonRequired] bool Complete = true);

public sealed class AgentLlmMetrics
{
    private readonly object _gate = new();
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private long _calls;
    private long _timedCalls;
    private double _responseTimeMs;

    public void Call()
    {
        lock (_gate) _calls++;
    }

    public void Duration(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            _responseTimeMs += milliseconds;
            _timedCalls++;
        }
    }

    public void Usage(GitHub.Copilot.AssistantUsageEvent usage)
    {
        var data = System.Text.Json.JsonSerializer.SerializeToElement(usage.Data, CopilotUsageTrace.JsonOptions);
        if (data.TryGetProperty("duration", out var duration) &&
            duration.ValueKind == System.Text.Json.JsonValueKind.Number &&
            duration.TryGetDouble(out var milliseconds))
            Duration(milliseconds);
    }

    public AgentLlmTotals Snapshot()
    {
        lock (_gate) return new(_instanceId, _calls, _responseTimeMs, _timedCalls);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _calls = 0;
            _timedCalls = 0;
            _responseTimeMs = 0;
        }
    }
}

using System.Diagnostics;
using System.Text.Json;

namespace BuyerAgent;

public sealed record ExchangeEntry(
    long Id, DateTimeOffset StartedAt, string Endpoint, string? TraceId, string? SpanId,
    string Request, string Response, bool RequestTruncated, bool ResponseTruncated,
    int? HttpStatus, string Status, string? ErrorType, double? DurationMs)
{
    public DateTimeOffset? ResponseStartedAt { get; init; }
    /// <summary>Set when the exchange is part of one "send to all suppliers" multicast, shared by its exchanges.</summary>
    public string? Group { get; init; }
    public string? GroupLabel { get; init; }
}

public sealed record ObservedTask(
    string Agent, string Endpoint, string Id, string? ContextId, string State, DateTimeOffset UpdatedAt,
    string? PresentationUrl = null);

public sealed class ExchangeStore(BuyerOptions options)
{
    public const int MaxExchanges = 100;
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxTasks = 128;
    private readonly object _gate = new();
    private readonly List<ExchangeEntry> _exchanges = [];
    private readonly List<ObservedTask> _tasks = [];
    private long _sequence;
    private long _generation;
    private int _dropped;

    public bool Enabled => options.ExchangeCaptureEnabled;

    /// <summary>The multicast in progress on this async flow; exchanges begun under it are grouped in the dashboard.</summary>
    public static readonly AsyncLocal<(string Id, string Label)?> CurrentGroup = new();

    public (long Id, long Generation) Begin(Uri endpoint)
    {
        lock (_gate)
        {
            var id = ++_sequence;
            _exchanges.Add(new(id, DateTimeOffset.UtcNow, endpoint.GetLeftPart(UriPartial.Path),
                Activity.Current?.TraceId.ToHexString(), Activity.Current?.SpanId.ToHexString(),
                "", "", false, false, null, "sending", null, null)
                { Group = CurrentGroup.Value?.Id, GroupLabel = CurrentGroup.Value?.Label });
            if (_exchanges.Count > MaxExchanges)
            {
                _exchanges.RemoveAt(0);
                _dropped++;
            }
            return (id, _generation);
        }
    }

    public void Body((long Id, long Generation) key, bool response, string body, bool truncated, bool sse)
    {
        lock (_gate)
        {
            var index = Find(key);
            if (index < 0) return;
            var entry = _exchanges[index];
            _exchanges[index] = response
                ? entry with { Response = body, ResponseTruncated = truncated }
                : entry with { Request = body, RequestTruncated = truncated };
            if (response && sse)
            {
                foreach (var frame in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n").SkipLast(1))
                {
                    var data = string.Join("\n", frame.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                        .Select(line => line[5..].TrimStart(' ')));
                    ObserveJson(data, entry.Endpoint);
                }
            }
            else if (!truncated)
            {
                ObserveJson(body, entry.Endpoint);
            }
        }
    }

    public void Status((long Id, long Generation) key, string status, int? httpStatus = null,
        string? errorType = null, double? durationMs = null)
    {
        lock (_gate)
        {
            var index = Find(key);
            if (index < 0) return;
            var entry = _exchanges[index];
            _exchanges[index] = entry with
            {
                Status = status, HttpStatus = httpStatus ?? entry.HttpStatus,
                ResponseStartedAt = status == "receiving" || errorType is not null && entry.ResponseStartedAt is null
                    ? DateTimeOffset.UtcNow : entry.ResponseStartedAt,
                ErrorType = errorType, DurationMs = durationMs
            };
        }
    }

    public void BuyerTask(string id, string contextId, string state)
    {
        if (!Enabled) return;
        lock (_gate) Task("buyer", options.ListenUrl.TrimEnd('/') + "/a2a", id, contextId, state);
    }

    public object Snapshot()
    {
        lock (_gate) return new
        {
            enabled = Enabled, maxExchanges = MaxExchanges, maxBodyBytes = MaxBodyBytes,
            droppedExchanges = _dropped, generation = _generation,
            exchanges = _exchanges.ToArray(), tasks = _tasks.ToArray()
        };
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _exchanges.Clear();
            _tasks.Clear();
            _dropped = 0;
        }
    }

    private int Find((long Id, long Generation) key) =>
        key.Generation == _generation ? _exchanges.FindIndex(entry => entry.Id == key.Id) : -1;

    private void ObserveJson(string body, string endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("params", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("taskId", out var continuation) &&
                continuation.ValueKind == JsonValueKind.String)
                Task("seller", endpoint, continuation.GetString()!, Text(message, "contextId"), "continuation sent");
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("task", out var task)) result = task;
                else if (result.TryGetProperty("statusUpdate", out var update)) result = update;
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object &&
                    Text(status, "state") is { } state &&
                    (Text(result, "id") ?? Text(result, "taskId")) is { } id)
                    Task("seller", endpoint, id, Text(result, "contextId"), state, PresentationUrl(status));
            }
        }
        catch (JsonException)
        {
            // Partial stream chunks and non-JSON error responses remain visible as raw wire content.
        }
    }

    // The supplier's wallet QR page for an auth-required purchase (status.message.metadata["agentcon.presentation"]).
    private string? PresentationUrl(JsonElement status)
    {
        if (!status.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("agentcon.presentation", out var presentation) ||
            presentation.ValueKind != JsonValueKind.Object ||
            Text(presentation, "presentationUrl") is not { } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0)
            return null;
        var allowed = uri.Scheme == Uri.UriSchemeHttps ||
            options.AllowLoopbackPresentationHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        return allowed ? uri.AbsoluteUri : null;
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private void Task(string agent, string endpoint, string id, string? context, string state, string? presentationUrl = null)
    {
        var index = _tasks.FindIndex(task => task.Agent == agent && task.Endpoint == endpoint && task.Id == id);
        var previous = index >= 0 ? _tasks[index] : null;
        // Keep the QR page only while the task still waits for wallet authorization.
        var page = state.Contains("AUTH_REQUIRED", StringComparison.OrdinalIgnoreCase)
            ? presentationUrl ?? previous?.PresentationUrl : null;
        if (previous?.State == state && previous.ContextId == context && previous.PresentationUrl == page) return;
        if (index >= 0) _tasks.RemoveAt(index);
        _tasks.Add(new(agent, endpoint, id, context ?? previous?.ContextId, state, DateTimeOffset.UtcNow, page));
        if (_tasks.Count > MaxTasks) _tasks.RemoveAt(0);
    }
}

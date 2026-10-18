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
    /// <summary>The A2A operation of an HTTP+JSON call, derived from its method and path; null for JSON-RPC and SLIMRPC.</summary>
    public string? RestOperation { get; init; }
}

public sealed record ObservedTask(
    string Agent, string Endpoint, string Id, string? ContextId, string State, DateTimeOffset UpdatedAt,
    string? PresentationUrl = null)
{
    public DateTimeOffset StartedAt { get; init; } = UpdatedAt;
    public string? StatusMessage { get; init; }
}

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
    public AgentLlmMetrics BuyerLlm { get; } = new();
    internal SemaphoreSlim DashboardGate { get; } = new(1, 1);
    private bool _llmCleared;
    private readonly HashSet<string> _supplierResetPending = [];
    private readonly Dictionary<string, (AgentLlmTotals Last, AgentLlmTotals Total)> _supplierLlm = [];

    public AgentLlmTotals SupplierLlm(string identifier, AgentLlmTotals sample)
    {
        lock (_gate)
        {
            if (!_supplierLlm.TryGetValue(identifier, out var previous) || _supplierResetPending.Remove(identifier))
            {
                var initial = _llmCleared ? sample with { Calls = 0, ResponseTimeMs = 0, TimedCalls = 0 } : sample;
                _supplierLlm[identifier] = (sample, initial);
                return initial;
            }
            var sameInstance = previous.Last.InstanceId == sample.InstanceId;
            if (sameInstance)
                sample = sample with
                {
                    Calls = Math.Max(sample.Calls, previous.Last.Calls),
                    ResponseTimeMs = Math.Max(sample.ResponseTimeMs, previous.Last.ResponseTimeMs),
                    TimedCalls = Math.Max(sample.TimedCalls, previous.Last.TimedCalls)
                };
            var total = sample with
            {
                Calls = previous.Total.Calls + Math.Max(0, sample.Calls - (sameInstance ? previous.Last.Calls : 0)),
                ResponseTimeMs = previous.Total.ResponseTimeMs + Math.Max(0,
                    sample.ResponseTimeMs - (sameInstance ? previous.Last.ResponseTimeMs : 0)),
                TimedCalls = previous.Total.TimedCalls + Math.Max(0, sample.TimedCalls - (sameInstance ? previous.Last.TimedCalls : 0)),
                Complete = previous.Total.Complete && sample.Complete
            };
            _supplierLlm[identifier] = (sample, total);
            return total;
        }
    }

    public bool Enabled => options.ExchangeCaptureEnabled;

    /// <summary>The multicast in progress on this async flow; exchanges begun under it are grouped in the dashboard.</summary>
    public static readonly AsyncLocal<(string Id, string Label)?> CurrentGroup = new();

    /// <summary>Maps an A2A HTTP+JSON request to its operation name, or null when it is not one.</summary>
    public static string? RestOperation(HttpMethod method, Uri uri)
    {
        var path = uri.AbsolutePath;
        if (method == HttpMethod.Post && (path.EndsWith("/message:send", StringComparison.Ordinal) || path.EndsWith("/message/send", StringComparison.Ordinal)))
            return "SendMessage";
        if (method == HttpMethod.Post && (path.EndsWith("/message:stream", StringComparison.Ordinal) || path.EndsWith("/message/stream", StringComparison.Ordinal)))
            return "SendStreamingMessage";
        var tasks = path.LastIndexOf("/tasks", StringComparison.Ordinal);
        if (tasks < 0) return null;
        var rest = path[(tasks + "/tasks".Length)..];
        if (rest.Length == 0) return method == HttpMethod.Get ? "ListTasks" : null;
        if (rest[0] != '/' || rest.IndexOf('/', 1) >= 0) return null;
        if (method == HttpMethod.Post && rest.EndsWith(":cancel", StringComparison.Ordinal)) return "CancelTask";
        if (rest.EndsWith(":subscribe", StringComparison.Ordinal) && (method == HttpMethod.Post || method == HttpMethod.Get)) return "SubscribeToTask";
        return method == HttpMethod.Get ? "GetTask" : null;
    }

    // HTTP+JSON paths carry the operation; tasks are owned by the interface base URL so observations dedupe across calls.
    private static string TaskEndpoint(ExchangeEntry entry)
    {
        if (entry.RestOperation is null) return entry.Endpoint;
        var cut = Math.Max(entry.Endpoint.LastIndexOf("/message", StringComparison.Ordinal),
            entry.Endpoint.LastIndexOf("/tasks", StringComparison.Ordinal));
        return cut > 0 ? entry.Endpoint[..cut] : entry.Endpoint;
    }

    public (long Id, long Generation) Begin(Uri endpoint, string? restOperation = null)
    {
        lock (_gate)
        {
            var id = ++_sequence;
            _exchanges.Add(new(id, DateTimeOffset.UtcNow, endpoint.GetLeftPart(UriPartial.Path),
                Activity.Current?.TraceId.ToHexString(), Activity.Current?.SpanId.ToHexString(),
                "", "", false, false, null, "sending", null, null)
                { Group = CurrentGroup.Value?.Id, GroupLabel = CurrentGroup.Value?.Label, RestOperation = restOperation });
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
                    ObserveJson(data, TaskEndpoint(entry), entry.RestOperation is not null, response);
                }
            }
            else if (!truncated)
            {
                ObserveJson(body, TaskEndpoint(entry), entry.RestOperation is not null, response);
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

    public void BuyerTask(string id, string contextId, string state, A2A.Message? message = null)
    {
        if (!Enabled) return;
        lock (_gate) Task("buyer", options.ListenUrl.TrimEnd('/') + "/a2a", id, contextId, state,
            statusMessage: message is null ? null : string.Join("\n",
                message.Parts.Where(part => part.Text is not null).Select(part => part.Text)));
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

    public void Clear(IReadOnlySet<string>? refreshedSuppliers = null)
    {
        lock (_gate)
        {
            _generation++;
            _exchanges.Clear();
            _tasks.Clear();
            _dropped = 0;
            BuyerLlm.Clear();
            _llmCleared = true;
            foreach (var (identifier, counters) in _supplierLlm.ToArray())
            {
                _supplierLlm[identifier] = (counters.Last, counters.Total with
                {
                    Calls = 0, ResponseTimeMs = 0, TimedCalls = 0, Complete = true
                });
                if (refreshedSuppliers is not null && !refreshedSuppliers.Contains(identifier))
                    _supplierResetPending.Add(identifier);
            }
        }
    }

    private int Find((long Id, long Generation) key) =>
        key.Generation == _generation ? _exchanges.FindIndex(entry => entry.Id == key.Id) : -1;

    // JSON-RPC wraps requests in params and responses in result; HTTP+JSON sends both at the top level.
    private void ObserveJson(string body, string endpoint, bool rest, bool response)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var parameters = root;
            if (!response && (rest || root.TryGetProperty("params", out parameters)) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("taskId", out var continuation) &&
                continuation.ValueKind == JsonValueKind.String)
                Task("seller", endpoint, continuation.GetString()!, Text(message, "contextId"), "continuation sent");
            var result = root;
            if (response && (rest || root.TryGetProperty("result", out result)) && result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("task", out var task)) result = task;
                else if (result.TryGetProperty("statusUpdate", out var update)) result = update;
                if (result.ValueKind == JsonValueKind.Object &&
                    result.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object &&
                    Text(status, "state") is { } state &&
                    (Text(result, "id") ?? Text(result, "taskId")) is { } id)
                    Task("seller", endpoint, id, Text(result, "contextId"), state, PresentationUrl(status), StatusText(status));
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

    private static string? StatusText(JsonElement status) =>
        status.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array
            ? string.Join("\n", parts.EnumerateArray().Where(part => part.ValueKind == JsonValueKind.Object)
                .Select(part => Text(part, "text")).Where(text => text is not null))
            : null;

    private void Task(string agent, string endpoint, string id, string? context, string state,
        string? presentationUrl = null, string? statusMessage = null)
    {
        var index = _tasks.FindIndex(task => task.Agent == agent && task.Endpoint == endpoint && task.Id == id);
        var previous = index >= 0 ? _tasks[index] : null;
        // Keep the QR page only while the task still waits for wallet authorization.
        var page = state.Contains("AUTH_REQUIRED", StringComparison.OrdinalIgnoreCase)
            ? presentationUrl ?? previous?.PresentationUrl : null;
        if (previous?.State == state && previous.ContextId == context && previous.PresentationUrl == page &&
            previous.StatusMessage == statusMessage) return;
        var now = DateTimeOffset.UtcNow;
        var observed = new ObservedTask(agent, endpoint, id, context ?? previous?.ContextId, state, now, page)
        {
            StartedAt = previous?.StartedAt ?? now,
            StatusMessage = statusMessage
        };
        if (index >= 0) _tasks[index] = observed;
        else _tasks.Add(observed);
        if (_tasks.Count > MaxTasks) _tasks.RemoveAt(0);
    }
}

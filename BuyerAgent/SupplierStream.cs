using A2A;
using System.Text.Json;

namespace BuyerAgent;

/// <summary>Owns one seller connection across the foreground turn and authorization wait.</summary>
public sealed class SupplierStream(HttpClient http, SupplierGateway gateway, SupplierBinding supplier, Message sent) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(15));
    private A2AClient? _client;
    private IAsyncEnumerator<StreamResponse>? _reader;
    private AgentTask? _task;
    public SupplierBinding Supplier => supplier;
    public bool CancellationRequested { get; private set; }

    public async Task<SendMessageResponse> StartAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _lifetime.Cancel());
        _client = new A2AClient(supplier.Endpoint, http);
        _reader = _client.SendStreamingMessageAsync(request, _lifetime.Token).GetAsyncEnumerator(_lifetime.Token);
        while (await _reader.MoveNextAsync())
        {
            var response = Apply(_reader.Current);
            if (response.Message is not null || response.Task?.Status.State is
                TaskState.Completed or TaskState.Failed or TaskState.Rejected or TaskState.Canceled or
                TaskState.AuthRequired or TaskState.InputRequired)
                return response;
        }
        throw new InvalidOperationException("Seller stream ended without an actionable result; inspect its task before retrying.");
    }

    public async Task<SendMessageResponse?> NextAsync(CancellationToken cancellationToken)
    {
        if (_reader is null) throw new InvalidOperationException("Seller stream has not started.");
        using var registration = cancellationToken.Register(() => _lifetime.Cancel());
        return await _reader.MoveNextAsync() ? Apply(_reader.Current) : null;
    }

    private SendMessageResponse Apply(StreamResponse item)
    {
        if (item.Message is { } message)
        {
            if (sent.TaskId is not null || sent.ContextId is not null && message.ContextId != sent.ContextId)
                throw new InvalidOperationException("Seller stream changed the bound task/context.");
            gateway.ValidateOutputs(supplier, message.Parts);
            return new() { Message = message };
        }
        var id = item.Task?.Id ?? item.StatusUpdate?.TaskId ?? item.ArtifactUpdate?.TaskId;
        var context = item.Task?.ContextId ?? item.StatusUpdate?.ContextId ?? item.ArtifactUpdate?.ContextId;
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(context) ||
            sent.TaskId is not null && sent.TaskId != id || sent.ContextId is not null && sent.ContextId != context ||
            _task is not null && (_task.Id != id || _task.ContextId != context))
            throw new InvalidOperationException("Seller stream changed the bound task/context.");
        _task = TaskProjection.Apply(_task, item)
            ?? throw new InvalidOperationException("Seller stream event did not produce a task.");
        gateway.ValidateOutputs(supplier, (_task.Artifacts ?? []).SelectMany(artifact => artifact.Parts)
            .Concat(_task.Status.Message?.Parts ?? []));
        // Projection mutates its task; earlier handoff/quote snapshots must remain immutable.
        return new() { Task = JsonSerializer.Deserialize<AgentTask>(
            JsonSerializer.Serialize(_task, A2AJsonUtilities.DefaultOptions), A2AJsonUtilities.DefaultOptions)! };
    }

    public void Cancel()
    {
        CancellationRequested = true;
        _lifetime.Cancel();
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_reader is not null) await _reader.DisposeAsync();
        _client?.Dispose();
        _lifetime.Dispose();
    }
}

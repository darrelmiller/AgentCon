using A2A;
using System.Text.Json;

namespace BuyerAgent;

/// <summary>Owns one seller connection across the foreground turn and authorization wait.</summary>
public sealed class SupplierStream(SupplierGateway gateway, SupplierBinding supplier, Message sent) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromMinutes(15));
    private static readonly TimeSpan AuthorizationPollInterval = TimeSpan.FromSeconds(3);
    private IA2AClient? _client;
    private IDisposable? _ownedClient;
    private IAsyncEnumerator<StreamResponse>? _reader;
    private AgentTask? _task;
    public SupplierBinding Supplier => supplier;
    public bool CancellationRequested { get; private set; }

    public async Task<SendMessageResponse> StartAsync(SendMessageRequest request, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => _lifetime.Cancel());
        var (client, owned) = gateway.OpenClient(supplier);
        _client = client;
        _ownedClient = owned;
        _reader = client.SendStreamingMessageAsync(request, _lifetime.Token).GetAsyncEnumerator(_lifetime.Token);
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
        if (await _reader.MoveNextAsync()) return Apply(_reader.Current);
        // A seller may end its stream at auth-required (A2A streams may close on an interrupted state; over SLIMRPC
        // they do), and with it the seller's own wait for the wallet. Resend the same purchase bound to the seller's
        // task: the seller checks the verifier and resumes the purchase it stored, never new order data from this
        // message, and answers auth-required again until the wallet approves.
        while (_task is { Status.State: TaskState.AuthRequired } waiting)
        {
            await Task.Delay(AuthorizationPollInterval, _lifetime.Token);
            var resume = new Message
            {
                MessageId = Guid.NewGuid().ToString("N"), Role = Role.User, Parts = sent.Parts,
                TaskId = waiting.Id, ContextId = waiting.ContextId, Metadata = sent.Metadata, Extensions = sent.Extensions
            };
            var reply = await _client!.SendMessageAsync(new SendMessageRequest { Message = resume }, _lifetime.Token);
            if (reply.Task is not { } current) return Apply(new StreamResponse { Message = reply.Message });
            if (current.Status.State != TaskState.AuthRequired) return Apply(new StreamResponse { Task = current });
        }
        return null;
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
        _ownedClient?.Dispose();
        _lifetime.Dispose();
    }
}

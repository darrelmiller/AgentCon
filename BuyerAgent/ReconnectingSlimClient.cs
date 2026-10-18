using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using A2A;

namespace BuyerAgent;

/// <summary>
/// A SLIMRPC client to one agent that survives the agent restarting. A SLIM client keeps one persistent session to its
/// agent and only replaces it once the session closes locally; when the agent's process is replaced (a redeployed
/// pod), calls go into the dead session until their deadline. So every call takes the current client from
/// <paramref name="current"/>, and any failure hands it to <paramref name="reset"/>, which drops it so the next call
/// opens a new session. A stream whose first event doesn't arrive within <paramref name="firstEvent"/> is treated
/// the same way and sent once more on a new session: an A2A agent answers a stream with the task at once, so no
/// first event means the request never reached it.
/// </summary>
public sealed class ReconnectingSlimClient(
    string remote, Func<IA2AClient> current, Func<IA2AClient, Exception, Task> reset, TimeSpan firstEvent,
    ILogger logger) : IA2AClient
{
    /// <summary>
    /// Whether a failure can mean the session is dead. An A2A error the agent sent back (task not found, invalid
    /// params...) proves the session works, and resetting would also cut the client's other calls.
    /// </summary>
    public static bool SessionMayBeDead(Exception exception) =>
        exception is not A2AException { ErrorCode: not A2AErrorCode.InternalError };

    private Task ResetAsync(IA2AClient client, Exception exception) =>
        SessionMayBeDead(exception) ? reset(client, exception) : Task.CompletedTask;

    private async Task<T> UnaryAsync<T>(Func<IA2AClient, Task<T>> call)
    {
        var client = current();
        try
        {
            return await call(client).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Including cancellation: a call into a dead session only ends when its caller gives up.
            await ResetAsync(client, exception).ConfigureAwait(false);
            throw;
        }
    }

    private async IAsyncEnumerable<StreamResponse> StreamAsync(
        Func<IA2AClient, CancellationToken, IAsyncEnumerable<StreamResponse>> call,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var client = current();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var reader = call(client, lifetime.Token).GetAsyncEnumerator(lifetime.Token);
            Exception? failure = null;
            var silent = false;
            var hasFirst = false;
            try
            {
                var move = reader.MoveNextAsync().AsTask();
                if (await Task.WhenAny(move, Task.Delay(firstEvent, cancellationToken)).ConfigureAwait(false) == move)
                    hasFirst = await move.ConfigureAwait(false);
                else
                {
                    silent = true;
                    lifetime.Cancel();
                    _ = move.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            if (silent || failure is not null)
            {
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
                await ResetAsync(client, failure ?? new TimeoutException()).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (silent && attempt == 1)
                {
                    logger.LogWarning("SLIM agent {Remote} sent nothing within {Seconds} s; resending on a new session",
                        remote, firstEvent.TotalSeconds);
                    continue;
                }
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                throw new TimeoutException($"SLIM agent {remote} did not answer within {firstEvent.TotalSeconds:0} s, twice.");
            }
            try
            {
                if (!hasFirst) yield break;
                yield return reader.Current;
                while (true)
                {
                    bool more;
                    try
                    {
                        more = await reader.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The agent already has the request: reset the session, but never resend.
                        await ResetAsync(client, exception).ConfigureAwait(false);
                        throw;
                    }
                    if (!more) yield break;
                    yield return reader.Current;
                }
            }
            finally
            {
                await DisposeQuietlyAsync(reader).ConfigureAwait(false);
            }
        }
    }

    private static async Task DisposeQuietlyAsync(IAsyncEnumerator<StreamResponse> reader)
    {
        try { await reader.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* the session is being replaced */ }
    }

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.SendMessageAsync(request, cancellationToken));

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
        StreamAsync((client, token) => client.SendStreamingMessageAsync(request, token), cancellationToken);

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.GetTaskAsync(request, cancellationToken));

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.ListTasksAsync(request, cancellationToken));

    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.CancelTaskAsync(request, cancellationToken));

    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default) =>
        StreamAsync((client, token) => client.SubscribeToTaskAsync(request, token), cancellationToken);

    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
        TaskPushNotificationConfig config, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.CreateTaskPushNotificationConfigAsync(config, cancellationToken));

    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
        GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.GetTaskPushNotificationConfigAsync(request, cancellationToken));

    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
        ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.ListTaskPushNotificationConfigsAsync(request, cancellationToken));

    public async Task DeleteTaskPushNotificationConfigAsync(
        DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
        await UnaryAsync(async client => { await client.DeleteTaskPushNotificationConfigAsync(request, cancellationToken); return true; });

    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default) =>
        UnaryAsync(client => client.GetExtendedAgentCardAsync(request, cancellationToken));
}

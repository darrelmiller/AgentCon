using A2A;
using BuyerAgent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class ReconnectingSlimClientTests
{
    private static readonly TimeSpan FirstEvent = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task SilentStreamIsResentOnceOnANewSession()
    {
        var dead = new FakeClient { Silent = true };
        var fresh = new FakeClient();
        var (client, resets) = Reconnecting(dead, fresh);
        var events = new List<StreamResponse>();
        await foreach (var item in client.SendStreamingMessageAsync(new() { Message = new() { MessageId = "m" } }))
            events.Add(item);
        Assert.Equal([dead], resets);
        Assert.Equal(1, dead.Streams);
        Assert.Equal(1, fresh.Streams);
        Assert.Equal(TaskState.Completed, Assert.Single(events).Task!.Status.State);
    }

    [Fact]
    public async Task StreamThatFailsAfterItsFirstEventResetsButIsNotResent()
    {
        var failing = new FakeClient { FailAfterFirst = true };
        var fresh = new FakeClient();
        var (client, resets) = Reconnecting(failing, fresh);
        var events = new List<StreamResponse>();
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var item in client.SendStreamingMessageAsync(new() { Message = new() { MessageId = "m" } }))
                events.Add(item);
        });
        Assert.Single(events);
        Assert.Equal([failing], resets);
        Assert.Equal(0, fresh.Streams);
    }

    [Fact]
    public async Task TransportFailuresResetAndAgentErrorsDoNot()
    {
        var timedOut = new FakeClient { Unary = new TimeoutException("deadline") };
        var (client, resets) = Reconnecting(timedOut);
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetTaskAsync(new() { Id = "t" }));
        Assert.Equal([timedOut], resets);

        var notFound = new FakeClient { Unary = new A2AException("task not found", A2AErrorCode.TaskNotFound) };
        (client, resets) = Reconnecting(notFound);
        await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(new() { Id = "t" }));
        Assert.Empty(resets);
    }

    [Fact]
    public async Task StreamSilentTwiceFailsWithoutAThirdAttempt()
    {
        var first = new FakeClient { Silent = true };
        var second = new FakeClient { Silent = true };
        var (client, resets) = Reconnecting(first, second, new FakeClient());
        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(new() { Message = new() { MessageId = "m" } })) { }
        });
        Assert.Equal([first, second], resets);
    }

    private static (ReconnectingSlimClient, List<IA2AClient>) Reconnecting(params FakeClient[] clients)
    {
        var resets = new List<IA2AClient>();
        var queue = new Queue<FakeClient>(clients);
        IA2AClient? current = null;
        var client = new ReconnectingSlimClient("agntcy/test/seller",
            () => current ??= queue.Dequeue(),
            (stale, _) =>
            {
                resets.Add(stale);
                if (ReferenceEquals(stale, current)) current = null;
                return Task.CompletedTask;
            }, FirstEvent, NullLogger.Instance);
        return (client, resets);
    }

    private sealed class FakeClient : IA2AClient
    {
        public bool Silent { get; init; }
        public bool FailAfterFirst { get; init; }
        public Exception? Unary { get; init; }
        public int Streams { get; private set; }

        public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Streams++;
            if (Silent) await Task.Delay(Timeout.Infinite, cancellationToken);
            yield return new() { Task = new() { Id = "t", ContextId = "c",
                Status = new() { State = FailAfterFirst ? TaskState.Working : TaskState.Completed } } };
            if (FailAfterFirst) throw new TimeoutException("session lost");
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default) =>
            Unary is { } failure ? Task.FromException<AgentTask>(failure) : Task.FromResult(new AgentTask { Id = request.Id, ContextId = "c" });

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

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

    [Fact]
    public async Task OneShotCallIntoADeadSessionTimesOutAndResets()
    {
        var dead = new FakeClient { HangUnary = true };
        var (client, resets) = Reconnecting(dead, new FakeClient());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetTaskAsync(new() { Id = "t" }));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal([dead], resets);
        Assert.Equal("t", (await client.GetTaskAsync(new() { Id = "t" })).Id);
    }

    [Fact]
    public async Task CallsAreTrackedInFlightUntilTheyEnd()
    {
        var inFlight = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        var slow = new FakeClient { Gate = gate.Task };
        var client = new ReconnectingSlimClient("agntcy/test/seller", () => slow, (_, _) => Task.CompletedTask,
            FirstEvent, TimeSpan.FromSeconds(5), () =>
            {
                peak = Math.Max(peak, Interlocked.Increment(ref inFlight));
                return new Scope(() => Interlocked.Decrement(ref inFlight));
            }, NullLogger.Instance);
        var call = client.GetTaskAsync(new() { Id = "t" });
        Assert.Equal(1, inFlight);
        gate.SetResult();
        await call;
        await foreach (var _ in client.SendStreamingMessageAsync(new() { Message = new() { MessageId = "m" } }))
            Assert.Equal(1, inFlight);
        Assert.Equal(0, inFlight);
        Assert.Equal(1, peak);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
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
            }, FirstEvent, TimeSpan.FromMilliseconds(300), () => new Scope(() => { }), NullLogger.Instance);
        return (client, resets);
    }

    private sealed class FakeClient : IA2AClient
    {
        public bool Silent { get; init; }
        public bool FailAfterFirst { get; init; }
        public Exception? Unary { get; init; }
        public bool HangUnary { get; init; }
        public Task? Gate { get; init; }
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

        public async Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default)
        {
            if (HangUnary) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (Gate is { } gate) await gate;
            if (Unary is { } failure) throw failure;
            return new AgentTask { Id = request.Id, ContextId = "c" };
        }

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

using A2A;
using System.Text.Json;

namespace BuyerAgent;

public sealed class BuyerHandler(
    IBuyerHarness harness, BuyerConversationStore conversations, BuyerOptions options, ILogger<BuyerHandler> logger) : IAgentHandler
{
    private readonly Dictionary<string, ActiveExecution> _activeTasks = new(StringComparer.Ordinal);
    private readonly object _activeLock = new();

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Agent(context.TaskId, context.ContextId);
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        if (!context.IsContinuation)
        {
            await updater.SubmitAsync(cancellationToken: cancellationToken);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        var execution = new ActiveExecution(deadline);
        lock (_activeLock)
        {
            _activeTasks.Add(context.TaskId, execution);
        }
        BuyerConversation? conversation = null;
        BuyerTurn? turn = null;
        var entered = false;
        var reply = "Buyer did not complete this request.";
        try
        {
            if (context.Message.Parts.Count == 0 ||
                context.Message.Parts.Any(part => part.ContentCase is not (PartContentCase.Text or PartContentCase.Data)))
            {
                throw new ArgumentException("The buyer accepts text and JSON data parts only.");
            }
            conversation = conversations.Get(context.ContextId);
            await conversation.Gate.WaitAsync(deadline.Token);
            entered = true;
            turn = new BuyerTurn(context.Message, conversation);
            await updater.StartWorkAsync(cancellationToken: deadline.Token);
            reply = await harness.RunAsync(turn, deadline.Token);
            await ForwardSupplierArtifactsAsync(turn, updater, eventQueue, deadline.Token);
            if (turn.ToolFailure is { } toolFailure)
            {
                throw new InvalidOperationException($"Supplier tool failed: {toolFailure.Message}", toolFailure);
            }

            var supplierTask = turn.SupplierResponses.LastOrDefault()?.Task;
            var message = CreateReply(reply, context);
            if (supplierTask?.Status.State == TaskState.AuthRequired)
            {
                message.Metadata = supplierTask.Status.Message?.Metadata;
                await updater.RequireAuthAsync(message, cancellationToken: deadline.Token);
            }
            else if (supplierTask?.Status.State is TaskState.InputRequired or TaskState.Submitted or TaskState.Working)
            {
                await updater.RequireInputAsync(message, cancellationToken: deadline.Token);
            }
            else if (supplierTask?.Status.State is TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
            {
                BuyerTelemetry.Error(activity, "supplier_task_failed");
                await updater.FailAsync(message, cancellationToken: deadline.Token);
            }
            else
            {
                await updater.AddArtifactAsync([Part.FromText(reply)], name: "buyer-response", cancellationToken: deadline.Token);
                await updater.CompleteAsync(message, cancellationToken: deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            BuyerTelemetry.Error(activity, execution.CancellationRequested ? "canceled" : "timeout");
            if (execution.CancellationRequested)
            {
                reply = "Buyer task canceled. Supplier tasks are not automatically canceled; inspect them before retrying an order.";
                await updater.CancelAsync(cancellationToken: cancellationToken);
                return;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            reply = "Buyer execution was canceled or exceeded its configured timeout. A supplier order may already have been accepted; inspect its task before retrying.";
            logger.LogWarning("Buyer task {TaskId} canceled or timed out", context.TaskId);
            await updater.FailAsync(CreateReply(reply, context), cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            BuyerTelemetry.Error(activity, exception);
            reply = $"Buyer request failed: {exception.Message}";
            logger.LogError(exception, "Buyer task {TaskId} failed", context.TaskId);
            await updater.FailAsync(CreateReply(reply, context), cancellationToken: cancellationToken);
        }
        finally
        {
            lock (_activeLock)
            {
                _activeTasks.Remove(context.TaskId);
            }
            if (entered && conversation is not null && turn is not null)
            {
                conversation.Remember(new BuyerHistoryTurn(
                    JsonSerializer.Serialize(context.Message, A2AJsonUtilities.DefaultOptions),
                    reply,
                    turn.SupplierResponses.Select(response =>
                        JsonSerializer.Serialize(response, A2AJsonUtilities.DefaultOptions)).ToArray()));
                conversation.Gate.Release();
            }
        }
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Source.StartActivity("buyer.cancel");
        activity?.SetTag("agentcon.a2a.task.id", context.TaskId);
        lock (_activeLock)
        {
            if (_activeTasks.TryGetValue(context.TaskId, out var active))
            {
                active.CancellationRequested = true;
                active.Deadline.Cancel();
            }

        }
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        await updater.CancelAsync(cancellationToken: cancellationToken);
    }

    private sealed class ActiveExecution(CancellationTokenSource deadline)
    {
        public CancellationTokenSource Deadline { get; } = deadline;
        public volatile bool CancellationRequested;
    }

    private static Message CreateReply(string text, RequestContext context) => new()
    {
        MessageId = Guid.NewGuid().ToString("N"),
        Role = Role.Agent,
        ContextId = context.ContextId,
        TaskId = context.TaskId,
        Parts = [Part.FromText(text)]
    };

    private static async Task ForwardSupplierArtifactsAsync(
        BuyerTurn turn, TaskUpdater updater, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var artifacts = turn.SupplierResponses
            .Where(response => response.Task is not null)
            .SelectMany(response => response.Task!.Artifacts ?? [])
            .GroupBy(artifact => artifact.ArtifactId)
            .Select(group => group.Last());
        foreach (var artifact in artifacts)
        {
            await eventQueue.EnqueueArtifactUpdateAsync(new TaskArtifactUpdateEvent
            {
                TaskId = updater.TaskId,
                ContextId = updater.ContextId,
                Artifact = new Artifact
                {
                    ArtifactId = Guid.NewGuid().ToString("N"),
                    Name = artifact.Name,
                    Description = artifact.Description,
                    Parts = artifact.Parts,
                    Metadata = artifact.Metadata
                },
                LastChunk = true
            }, cancellationToken);
        }
        if (turn.SupplierResponses.Count > 0)
        {
            await updater.AddArtifactAsync(
                [Part.FromData(JsonSerializer.SerializeToElement(turn.SupplierResponses, A2AJsonUtilities.DefaultOptions))],
                name: "supplier-responses",
                description: "Original supplier A2A responses, including task IDs and authorization metadata.",
                cancellationToken: cancellationToken);
        }
    }
}

using A2A;
using System.Text.Json;

namespace BuyerAgent;

public sealed class BuyerHandler(
    IBuyerHarness harness, BuyerConversationStore conversations, BuyerOptions options, ILogger<BuyerHandler> logger,
    ExchangeStore exchanges, ITaskStore taskStore, ChannelEventNotifier notifier) : IAgentHandler
{
    private readonly Dictionary<string, ActiveExecution> _activeTasks = new(StringComparer.Ordinal);
    private readonly object _activeLock = new();

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Agent(context.TaskId, context.ContextId);
        var mode = options.CommunicationMode;
        void Observe(string state) => exchanges.BuyerTask(context.TaskId, context.ContextId, state);
        Observe("TASK_STATE_SUBMITTED");
        var updater = new TaskUpdater(eventQueue, context.TaskId, context.ContextId);
        if (!context.IsContinuation)
        {
            await updater.SubmitAsync(cancellationToken: cancellationToken);
        }
        else
        {
            // preview2 materializes synchronous replies only after an initial Task event.
            await eventQueue.EnqueueTaskAsync(context.Task!, cancellationToken);
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
                context.Message.Parts.Any(part => part.ContentCase != PartContentCase.Text ||
                    part.MediaType is not (null or "text/plain")))
            {
                throw new ArgumentException("The buyer accepts text/plain input only.");
            }
            conversation = conversations.Get(context.ContextId);
            await conversation.Gate.WaitAsync(deadline.Token);
            entered = true;
            turn = new BuyerTurn(context.Message, conversation) { CommunicationMode = mode };
            await updater.StartWorkAsync(cancellationToken: deadline.Token);
            Observe("TASK_STATE_WORKING");
            reply = await harness.RunAsync(turn, deadline.Token);
            var forwardedArtifacts = await ForwardSupplierArtifactsAsync(turn, updater, eventQueue, deadline.Token);
            if (turn.ToolFailure is { } toolFailure)
            {
                throw new InvalidOperationException($"Supplier tool failed: {toolFailure.Message}", toolFailure);
            }

            var supplierTask = turn.SupplierResponses.LastOrDefault()?.Task;
            var message = CreateReply(reply, context);
            if (turn.QuoteAssessments.Count > 0)
                message.Metadata = new() { ["agentcon.quoteAssessments"] = JsonSerializer.SerializeToElement(turn.QuoteAssessments) };
            if (supplierTask?.Status.State == TaskState.AuthRequired)
            {
                message.Metadata ??= new();
                foreach (var item in supplierTask.Status.Message?.Metadata ?? [])
                    message.Metadata[item.Key] = item.Value;
                var instructions = SupplierAuthorization.Instructions(supplierTask);
                message.Parts = [Part.FromText(instructions)];
                reply = instructions;
                await updater.RequireAuthAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_AUTH_REQUIRED");
            }
            else if (turn.PriceApprovalRequired ||
                supplierTask?.Status.State is TaskState.InputRequired or TaskState.Submitted or TaskState.Working)
            {
                await updater.RequireInputAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_INPUT_REQUIRED");
            }
            else if (supplierTask?.Status.State is TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
            {
                BuyerTelemetry.Error(activity, "supplier_task_failed");
                await updater.FailAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_FAILED");
            }
            else
            {
                if (!forwardedArtifacts)
                    await updater.AddArtifactAsync([Part.FromText(reply)], name: "buyer-response", cancellationToken: deadline.Token);
                await updater.CompleteAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_COMPLETED");
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            BuyerTelemetry.Error(activity, execution.CancellationRequested ? "canceled" : "timeout");
            Observe(execution.CancellationRequested || cancellationToken.IsCancellationRequested
                ? "TASK_STATE_CANCELED" : "TASK_STATE_FAILED");
            if (execution.CancellationRequested)
            {
                reply = "Buyer task canceled. Supplier tasks are not automatically canceled; inspect them before retrying an order.";
                await updater.CancelAsync(cancellationToken: cancellationToken);
                return;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                // A disconnected synchronous caller stops the SDK event reader. Persist
                // cancellation independently so GetTask cannot remain permanently working.
                using (await notifier.AcquireTaskLockAsync(context.TaskId, CancellationToken.None))
                {
                    var stored = await taskStore.GetTaskAsync(context.TaskId, CancellationToken.None);
                    if (stored is not null && stored.Status.State is TaskState.Submitted or TaskState.Working)
                    {
                        stored.Status = new A2A.TaskStatus
                        {
                            State = TaskState.Canceled,
                            Message = CreateReply("Caller disconnected; buyer execution canceled. Inspect supplier tasks before retrying.", context),
                            Timestamp = DateTimeOffset.UtcNow
                        };
                        await taskStore.SaveTaskAsync(context.TaskId, stored, CancellationToken.None);
                    }
                }
                throw;
            }
            reply = "Buyer execution was canceled or exceeded its configured timeout. A supplier order may already have been accepted; inspect its task before retrying.";
            logger.LogWarning("Buyer task {TaskId} canceled or timed out", context.TaskId);
            await updater.FailAsync(CreateReply(reply, context), cancellationToken: cancellationToken);
        }
        catch (Exception exception)
        {
            BuyerTelemetry.Error(activity, exception);
            Observe("TASK_STATE_FAILED");
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
        exchanges.BuyerTask(context.TaskId, context.ContextId, "TASK_STATE_CANCELED");
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

    private static async Task<bool> ForwardSupplierArtifactsAsync(
        BuyerTurn turn, TaskUpdater updater, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        var artifacts = turn.SupplierResponses
            .Where(response => response.Task is not null)
            .SelectMany(response => response.Task!.Artifacts ?? [])
            .GroupBy(artifact => artifact.ArtifactId)
            .Select(group => group.Last());
        var forwarded = false;
        foreach (var artifact in artifacts)
        {
            if (turn.CommunicationMode == SupplierCommunicationMode.NaturalLanguage &&
                artifact.Parts.Any(part => part.ContentCase != PartContentCase.Text))
                continue;
            forwarded = true;
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
        return forwarded;
    }
}

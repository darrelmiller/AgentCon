using A2A;
using System.Text.Json;

namespace BuyerAgent;

public sealed class BuyerHandler(
    IBuyerHarness harness, BuyerConversationStore conversations, BuyerOptions options, ILogger<BuyerHandler> logger,
    ExchangeStore exchanges, ITaskStore taskStore, ChannelEventNotifier notifier, SupplierGateway gateway,
    SupplierStreamMonitor streams) : IAgentHandler
{
    private readonly Dictionary<string, ActiveExecution> _activeTasks = new(StringComparer.Ordinal);
    private readonly object _activeLock = new();

    public async Task ExecuteAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Agent(context.TaskId, context.ContextId);
        var mode = options.CommunicationMode;
        var snapshot = options.WithMode(mode);
        void Observe(string state, Message? message = null) => exchanges.BuyerTask(context.TaskId, context.ContextId, state, message);
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
            if (streams.IsWatching(context.TaskId))
            {
                var existing = conversations.Get(context.ContextId);
                await existing.Gate.WaitAsync(cancellationToken);
                try
                {
                    if (existing.NativePurchases.TryGetValue(context.TaskId, out var pending) &&
                        context.Message.Parts.Any(part => part.ContentCase == PartContentCase.Data))
                    {
                        var valid = snapshot.NativeStructuredSupport && context.Message.Parts.Count == 1 &&
                            context.Message.Parts[0].MediaType == SupplierFormats.Purchase;
                        if (valid)
                        {
                            var format = SupplierFormats.Installed().Single(item => item.MediaType == SupplierFormats.Purchase);
                            try
                            {
                                valid = SupplierFormats.Validate(format, context.Message.Parts[0].Data!.Value, true).GetRawText() == pending.Payload;
                            }
                            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
                            {
                                logger.LogWarning("Invalid continuation for monitored native purchase {TaskId}", context.TaskId);
                                valid = false;
                            }
                            foreach (var key in new[] { "agentcon.supplierIdentifier", "agentcon.supplierVersion" })
                                if (context.Message.Metadata?.TryGetValue(key, out var value) == true &&
                                    (value.ValueKind != JsonValueKind.String || value.GetString() !=
                                        (key.EndsWith("Identifier", StringComparison.Ordinal) ? pending.Quote.Supplier.Identifier : pending.Quote.Supplier.Version)))
                                    valid = false;
                            if (context.Message.Metadata?.TryGetValue("agentcon.maxTotalUsd", out var budget) == true &&
                                (budget.ValueKind != JsonValueKind.Number || !budget.TryGetDecimal(out var amount) ||
                                    pending.MaxTotalUsd != amount))
                                valid = false;
                        }
                        if (!valid)
                        {
                            var rejected = CreateReply("Continuation rejected: the original immutable purchase is still waiting for wallet approval. No new order was sent.", context);
                            rejected.Metadata = context.Task!.Status.Message?.Metadata;
                            await updater.RequireAuthAsync(rejected, cancellationToken: cancellationToken);
                            Observe("TASK_STATE_AUTH_REQUIRED", rejected);
                        }
                    }
                }
                finally { existing.Gate.Release(); }
                return;
            }
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
            var typed = context.Message.Parts.Count == 1 &&
                context.Message.Parts[0].ContentCase == PartContentCase.Data && snapshot.NativeStructuredSupport;
            if (!typed && (context.Message.Parts.Count == 0 ||
                context.Message.Parts.Any(part => part.ContentCase != PartContentCase.Text ||
                    part.MediaType is not (null or "text/plain"))))
            {
                throw new ArgumentException("The buyer accepts text/plain input only.");
            }
            var accepted = context.Configuration?.AcceptedOutputModes;
            var textAccepted = accepted is null || accepted.Contains("text/plain");
            if (!textAccepted && (!typed || context.Message.Parts[0].MediaType != SupplierFormats.Rfq ||
                !accepted!.Contains(SupplierFormats.Quote)))
                throw new ArgumentException("No compatible output modes. Text/plain status is required for text requests and purchase authorization.");
            conversation = conversations.Get(context.ContextId);
            await conversation.Gate.WaitAsync(deadline.Token);
            entered = true;
            turn = new BuyerTurn(context.Message, conversation) { CommunicationMode = mode, OptionsSnapshot = snapshot };
            turn.Message.TaskId = context.TaskId;
            await updater.StartWorkAsync(cancellationToken: deadline.Token);
            Observe("TASK_STATE_WORKING");
            reply = typed
                ? await new NativeBuyer(gateway.ForOptions(snapshot)).RunAsync(turn, deadline.Token)
                : await harness.RunAsync(turn, deadline.Token);
            var forwardedArtifacts = await ForwardSupplierArtifactsAsync(turn, updater, eventQueue, accepted, reply, deadline.Token);
            if (turn.ToolFailure is { } toolFailure)
            {
                throw new InvalidOperationException($"Supplier tool failed: {toolFailure.Message}", toolFailure);
            }

            var supplierTask = turn.SupplierResponses.LastOrDefault()?.Task;
            // After a multicast, the turn fails only if every supplier failed; otherwise report a successful reply.
            if (turn.LastBroadcast is { Count: > 0 } broadcast && broadcast.Contains(turn.SupplierResponses.LastOrDefault()))
                supplierTask = broadcast.Select(response => response.Task)
                    .FirstOrDefault(task => task?.Status.State is not (TaskState.Failed or TaskState.Rejected or TaskState.Canceled))
                    ?? supplierTask;
            var message = CreateReply(reply, context);
            message.Metadata = new()
            {
                [MessageInferenceCounter.MetadataKey] = JsonSerializer.SerializeToElement(turn.Inference.Total),
                ["agentcon.nativeExecution"] = JsonSerializer.SerializeToElement(typed)
            };
            if (turn.QuoteAssessments.Count > 0)
                message.Metadata["agentcon.quoteAssessments"] = JsonSerializer.SerializeToElement(turn.QuoteAssessments);
            if (supplierTask?.Status.State == TaskState.AuthRequired)
            {
                message.Metadata ??= new();
                foreach (var item in supplierTask.Status.Message?.Metadata ?? [])
                    if (item.Key == MessageInferenceCounter.MetadataKey)
                        message.Metadata["agentcon.supplierLlmCalls"] = item.Value;
                    else message.Metadata[item.Key] = item.Value;
                var instructions = SupplierAuthorization.Instructions(supplierTask, snapshot.AllowLoopbackPresentationHttp,
                    turn.SupplierStream is not null);
                if (turn.NativeContinuationError is { } error) instructions = error + "\n" + instructions;
                message.Parts = [Part.FromText(instructions)];
                reply = instructions;
                await updater.RequireAuthAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_AUTH_REQUIRED", message);
            }
            else if (turn.PriceApprovalRequired ||
                supplierTask?.Status.State is TaskState.InputRequired or TaskState.Submitted or TaskState.Working)
            {
                await updater.RequireInputAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_INPUT_REQUIRED", message);
            }
            else if (supplierTask?.Status.State is TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
            {
                BuyerTelemetry.Error(activity, "supplier_task_failed");
                await updater.FailAsync(message, cancellationToken: deadline.Token);
                Observe("TASK_STATE_FAILED", message);
            }
            else
            {
                // The reply is the task's output: keep it in artifacts and leave the status message as a short summary.
                if (!forwardedArtifacts || !typed && textAccepted)
                    await updater.AddArtifactAsync([Part.FromText(reply)], name: "buyer-response", cancellationToken: deadline.Token);
                message.Parts = [Part.FromText("Completed; results are in the task artifacts.")];
                await updater.CompleteAsync(textAccepted ? message : null, cancellationToken: deadline.Token);
                Observe("TASK_STATE_COMPLETED", textAccepted ? message : null);
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
                        Observe("TASK_STATE_CANCELED", stored.Status.Message);
                        await taskStore.SaveTaskAsync(context.TaskId, stored, CancellationToken.None);
                    }
                }
                throw;
            }
            reply = "Buyer execution was canceled or exceeded its configured timeout. A supplier order may already have been accepted; inspect its task before retrying.";
            logger.LogWarning("Buyer task {TaskId} canceled or timed out", context.TaskId);
            var message = CreateReply(reply, context);
            await updater.FailAsync(message, cancellationToken: cancellationToken);
            Observe("TASK_STATE_FAILED", message);
        }
        catch (Exception exception)
        {
            BuyerTelemetry.Error(activity, exception);
            reply = $"Buyer request failed: {exception.Message}";
            logger.LogError(exception, "Buyer task {TaskId} failed", context.TaskId);
            var message = CreateReply(reply, context);
            await updater.FailAsync(message, cancellationToken: cancellationToken);
            Observe("TASK_STATE_FAILED", message);
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
                if (turn.SupplierStream is { } stream)
                {
                    if (turn.ToolFailure is null && !turn.PriceApprovalRequired)
                        streams.Watch(context.TaskId, context.ContextId, turn, context.Configuration?.AcceptedOutputModes);
                    else await stream.DisposeAsync();
                }
            }
        }
    }

    public async Task CancelAsync(RequestContext context, AgentEventQueue eventQueue, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Source.StartActivity("buyer.cancel");
        streams.Cancel(context.TaskId);
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
        BuyerTurn turn, TaskUpdater updater, AgentEventQueue eventQueue, IReadOnlyList<string>? accepted,
        string reply, CancellationToken cancellationToken)
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
            var parts = artifact.Parts;
            if (accepted is not null && parts.Any(part => !accepted.Contains(part.MediaType ??
                (part.ContentCase == PartContentCase.Text ? "text/plain" : ""))))
            {
                if (!accepted.Contains("text/plain")) continue;
                parts = [Part.FromText(reply)];
            }
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
                    Parts = parts,
                    Metadata = artifact.Metadata
                },
                LastChunk = true
            }, cancellationToken);
        }
        return forwarded;
    }
}

using A2A;
using System.Collections.Concurrent;
using System.Text.Json;

namespace BuyerAgent;

public sealed class SupplierStreamMonitor(
    ITaskStore tasks, ChannelEventNotifier notifier, ExchangeStore exchanges, SupplierGateway gateway,
    ILogger<SupplierStreamMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, (SupplierStream Stream, Task Work)> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    public bool IsWatching(string taskId) => _pending.ContainsKey(taskId);
    public void Cancel(string taskId)
    {
        if (_pending.TryGetValue(taskId, out var pending)) pending.Stream.Cancel();
    }

    public void Watch(string taskId, string contextId, BuyerTurn turn, IReadOnlyList<string>? accepted)
    {
        var stream = turn.SupplierStream!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = RunAsync(start.Task, taskId, contextId, turn, accepted, stream);
        if (!_pending.TryAdd(taskId, (stream, work)))
        {
            stream.Cancel();
            start.SetCanceled();
            throw new InvalidOperationException("Buyer task already owns a seller stream.");
        }
        start.SetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        _shutdown.Cancel();
        foreach (var pending in _pending.Values) pending.Stream.Cancel();
        await Task.WhenAll(_pending.Values.Select(pending => pending.Work));
    }

    private async Task RunAsync(Task start, string taskId, string contextId, BuyerTurn turn,
        IReadOnlyList<string>? accepted, SupplierStream stream)
    {
        try
        {
            await start;
            while (true)
            {
                using (await notifier.AcquireTaskLockAsync(taskId, _shutdown.Token))
                {
                    var handoff = await tasks.GetTaskAsync(taskId, _shutdown.Token);
                    if (handoff?.Status.State is TaskState.AuthRequired or TaskState.InputRequired) break;
                    if (handoff?.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Canceled or TaskState.Rejected) return;
                }
                await Task.Delay(10, _shutdown.Token);
            }
            while (await stream.NextAsync(_shutdown.Token) is { Task: { } seller })
            {
                await turn.Conversation.Gate.WaitAsync(_shutdown.Token);
                try
                {
                    turn.Conversation.SupplierTaskStates[(stream.Supplier.Identifier, seller.Id)] = seller.Status.State;
                    if (seller.Status.State == TaskState.AuthRequired) continue;
                    if (seller.Status.State == TaskState.Completed && turn.NativeExecution &&
                        turn.Conversation.NativePurchases.TryGetValue(taskId, out var purchase))
                    {
                        turn.SupplierResponses.Clear();
                        turn.SupplierResponses.Add(purchase.Quote.Response);
                        var tools = new SupplierTurnTools(gateway.ForOptions(turn.OptionsSnapshot!), turn, _shutdown.Token);
                        var order = JsonSerializer.Deserialize<WinePurchaseOrder>(purchase.Payload,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                        var assessment = await tools.AssessQuoteAsync(order.QuoteId, purchase.MaxTotalUsd);
                        if (turn.PriceApprovalRequired)
                            throw new InvalidOperationException("Completed seller purchase failed original buyer price/budget checks; inspect the seller receipt.");
                        NativeBuyer.ValidateReceipt(new() { Task = seller }, order, assessment,
                            stream.Supplier.Mode == SupplierCommunicationMode.NaturalLanguage);
                    }
                    using (await notifier.AcquireTaskLockAsync(taskId, _shutdown.Token))
                    {
                        var buyer = await tasks.GetTaskAsync(taskId, _shutdown.Token);
                        if (buyer is null || buyer.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
                            return;
                        if (seller.Status.State == TaskState.Completed)
                        {
                            foreach (var artifact in seller.Artifacts ?? [])
                            {
                                var parts = artifact.Parts;
                                if (accepted is not null && parts.Any(part => !accepted.Contains(part.MediaType ?? "text/plain")))
                                    parts = [.. parts.Select(part => part.Text is not null ? Part.FromText(part.Text)
                                        : part.Data is { } data ? Part.FromText(data.GetRawText()) : Part.FromText("Seller artifact omitted."))];
                                var update = new StreamResponse { ArtifactUpdate = new()
                                {
                                    TaskId = taskId, ContextId = contextId, LastChunk = true,
                                    Artifact = new() { ArtifactId = Guid.NewGuid().ToString("N"), Name = artifact.Name,
                                        Description = artifact.Description, Parts = parts, Metadata = artifact.Metadata }
                                } };
                                buyer = TaskProjection.Apply(buyer, update)
                                    ?? throw new InvalidOperationException("Buyer artifact projection failed.");
                                await tasks.SaveTaskAsync(taskId, buyer, _shutdown.Token);
                                notifier.Notify(taskId, update);
                            }
                        }
                        var state = seller.Status.State;
                        var message = new Message
                        {
                            MessageId = Guid.NewGuid().ToString("N"), Role = Role.Agent, TaskId = taskId, ContextId = contextId,
                            Parts = seller.Status.Message?.Parts ?? [Part.FromText($"Seller task {state}.")],
                            Metadata = new()
                            {
                                [MessageInferenceCounter.MetadataKey] = JsonSerializer.SerializeToElement(0),
                                ["agentcon.nativeExecution"] = JsonSerializer.SerializeToElement(turn.NativeExecution),
                                ["agentcon.supplierTaskId"] = JsonSerializer.SerializeToElement(seller.Id)
                            }
                        };
                        var status = new StreamResponse { StatusUpdate = new()
                        {
                            TaskId = taskId, ContextId = contextId,
                            Status = new() { State = state, Message = message, Timestamp = DateTimeOffset.UtcNow }
                        } };
                        buyer = TaskProjection.Apply(buyer, status)
                            ?? throw new InvalidOperationException("Buyer status projection failed.");
                        await tasks.SaveTaskAsync(taskId, buyer, _shutdown.Token);
                        notifier.Notify(taskId, status);
                        exchanges.BuyerTask(taskId, contextId,
                            JsonSerializer.SerializeToElement(state, A2AJsonUtilities.DefaultOptions).GetString()!, message);
                    }
                    if (seller.Status.State is TaskState.Completed or TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
                    {
                        turn.Conversation.Remember(new(
                            JsonSerializer.Serialize(turn.Message, A2AJsonUtilities.DefaultOptions),
                            seller.Status.Message?.Parts.FirstOrDefault()?.Text ?? $"Seller task {seller.Status.State}.",
                            [JsonSerializer.Serialize(new SendMessageResponse { Task = seller }, A2AJsonUtilities.DefaultOptions)]));
                        return;
                    }
                }
                finally { turn.Conversation.Gate.Release(); }
            }
            logger.LogInformation("Seller stream for buyer task {TaskId} closed; retaining the last task state. " +
                "Inspect or resume the original seller task; no purchase was resent.", taskId);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested || stream.CancellationRequested) { }
        catch (SupplierStreamReadException exception)
        {
            logger.LogWarning(exception, "Seller stream for buyer task {TaskId} disconnected; retaining the last task state. " +
                "Inspect or resume the original seller task; no purchase was resent.", taskId);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Seller stream for buyer task {TaskId} stopped", taskId);
            using (await notifier.AcquireTaskLockAsync(taskId, CancellationToken.None))
            {
                var buyer = await tasks.GetTaskAsync(taskId, CancellationToken.None);
                if (buyer is not null && buyer.Status.State is not (TaskState.Completed or TaskState.Failed or TaskState.Rejected or TaskState.Canceled))
                {
                    var update = new StreamResponse { StatusUpdate = new()
                    {
                        TaskId = taskId, ContextId = contextId, Status = new()
                        {
                            State = TaskState.Failed, Timestamp = DateTimeOffset.UtcNow,
                            Message = new() { MessageId = Guid.NewGuid().ToString("N"), Role = Role.Agent,
                                TaskId = taskId, ContextId = contextId,
                                Parts = [Part.FromText("Seller stream stopped: " + exception.Message +
                                    " Inspect the original seller task before retrying; no purchase was resent.")] }
                        }
                    } };
                    buyer = TaskProjection.Apply(buyer, update)
                        ?? throw new InvalidOperationException("Buyer error projection failed.");
                    await tasks.SaveTaskAsync(taskId, buyer, CancellationToken.None);
                    notifier.Notify(taskId, update);
                    exchanges.BuyerTask(taskId, contextId, "TASK_STATE_FAILED", update.StatusUpdate.Status.Message);
                }
            }
        }
        finally
        {
            try { await stream.DisposeAsync(); }
            finally { _pending.TryRemove(taskId, out _); }
        }
    }
}

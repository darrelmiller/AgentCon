using A2A;
using System.Text.Json;

namespace BuyerAgent;

/// <summary>Installed wine-v1 execution. No model runtime is reachable from this path.</summary>
public sealed class NativeBuyer(SupplierGateway gateway)
{
    public async Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken)
    {
        turn.NativeExecution = true;
        var part = turn.Message.Parts.Single();
        var format = SupplierFormats.Installed().SingleOrDefault(item =>
            item.Direction == "input" && item.MediaType == part.MediaType)
            ?? throw new ArgumentException("No installed inbound codec for this media type.");
        var data = SupplierFormats.Validate(format, part.Data!.Value, true);
        var tools = new SupplierTurnTools(gateway.ForMode(SupplierCommunicationMode.Automatic), turn, cancellationToken);
        var identifier = MetadataString(turn.Message, "agentcon.supplierIdentifier");
        var version = MetadataString(turn.Message, "agentcon.supplierVersion");
        if (part.MediaType == SupplierFormats.Rfq)
        {
            if (turn.Conversation.NativePurchases.ContainsKey(turn.Message.TaskId ?? ""))
                throw new InvalidOperationException("A pending purchase task cannot be replaced with an RFQ.");
            if (turn.Conversation.NativeQuotes.Count >= 128)
                throw new InvalidOperationException("Native quote capacity reached; restart to clear in-memory records.");
            turn.Conversation.NativeRfqTasks.TryGetValue(turn.Message.TaskId ?? "", out var rfqTask);
            if (turn.Conversation.NativeRfqTasks.Count >= 128 && rfqTask is null)
                throw new InvalidOperationException("Native RFQ task capacity reached; inspect supplier tasks.");
            if (rfqTask is not null)
            {
                if (identifier is not null && identifier != rfqTask.Supplier.Identifier ||
                    version is not null && version != rfqTask.Supplier.Version)
                    throw new InvalidOperationException("RFQ continuation must retain the issuing catalog supplier.");
                identifier = rfqTask.Supplier.Identifier;
            }
            var rfq = data.Deserialize<WineRfq>(JsonOptions)!;
            if (rfqTask is null && identifier is null)
            {
                // Explicit metadata wins; otherwise route by the requested wine before falling back to the conversation.
                var wineMatches = await gateway.MatchProductAsync(rfq.Wine, cancellationToken);
                if (wineMatches.Count == 1) identifier = wineMatches[0].Identifier;
                identifier ??= turn.Conversation.SelectedSupplier?.Identifier;
                if (identifier is null)
                {
                    var matches = wineMatches.Count > 1 ? wineMatches : (await gateway.SearchAsync(null, cancellationToken)).Matches;
                    if (matches.Count != 1)
                        throw new InvalidOperationException(
                            $"No single catalog supplier matches wine {JsonSerializer.Serialize(rfq.Wine)}; set agentcon.supplierIdentifier metadata to one of: " +
                            (matches.Count == 0 ? "(no catalog suppliers)" : string.Join(", ", matches.Select(match => match.Identifier))) + ".");
                    identifier = matches[0].Identifier;
                }
            }
            if (rfqTask is null) await tools.SelectAsync(identifier!, version);
            var text = $"Quote {rfq.Cases} cases of {rfq.Vintage} {rfq.Wine} delivered to " +
                $"{JsonSerializer.Serialize(rfq.DeliveryCity)}, {rfq.DeliveryCountry}. Demo only; do not purchase.";
            await tools.SendNativeAsync(text, data.GetRawText(), part.MediaType!, rfqTask?.SupplierTaskId,
                rfqTask?.SupplierContextId, identifier);
            var response = turn.SupplierResponses.Last();
            if (response.Task is { Status.State: TaskState.InputRequired or TaskState.AuthRequired or TaskState.Submitted or TaskState.Working } active)
            {
                turn.Conversation.NativeRfqTasks[turn.Message.TaskId!] = new(
                    turn.Conversation.SupplierTasks.Resolve(active.Id, identifier, "task"), active.Id, active.ContextId);
            }
            return Summary(response);
        }

        var order = data.Deserialize<WinePurchaseOrder>(JsonOptions)!;
        NativeQuote quote;
        var budget = MetadataBudget(turn.Message);
        string? supplierTaskId = null;
        if (turn.Conversation.NativePurchases.TryGetValue(turn.Message.TaskId ?? "", out var pending))
        {
            if (pending.Payload != data.GetRawText() ||
                identifier is not null && identifier != pending.Quote.Supplier.Identifier ||
                version is not null && version != pending.Quote.Supplier.Version)
            {
                turn.NativeContinuationError = "Continuation rejected: retain the immutable task-bound purchase and supplier. No supplier request was sent.";
                turn.SupplierResponses.Add(pending.Response);
                return turn.NativeContinuationError;
            }
            quote = pending.Quote;
            supplierTaskId = pending.SupplierTaskId;
            if (pending.MaxTotalUsd is { } originalBudget)
                budget = budget is { } currentBudget ? Math.Min(originalBudget, currentBudget) : originalBudget;
        }
        else
        {
            if (turn.Conversation.NativePurchases.Values.Any(item => item.Payload == data.GetRawText()))
                throw new InvalidOperationException("This purchase is already task-bound; continue the original buyer task.");
            if (turn.Conversation.NativePurchases.Count >= 128)
                throw new InvalidOperationException("Native pending purchase capacity reached; inspect existing tasks.");
            var quotes = turn.Conversation.NativeQuotes.Where(item => item.Key.QuoteId == order.QuoteId &&
                (identifier is null || item.Key.Supplier == identifier)).Select(item => item.Value).ToArray();
            if (quotes.Length != 1)
                throw new InvalidOperationException("Purchase requires a unique quote observed in this buyer context; specify its catalog supplier identifier.");
            quote = quotes[0];
            if (quote.Response.Task is { Status.State: TaskState.InputRequired } quoteTask)
                supplierTaskId = quoteTask.Id;
            else if (quote.Response.Task?.Status.State is TaskState.Submitted or TaskState.Working)
            {
                turn.PriceApprovalRequired = true;
                return "Supplier quote task is still working; inspect it before purchasing.";
            }
        }
        if (version is not null && version != quote.Supplier.Version)
            throw new InvalidOperationException("Purchase must retain the quote's catalog supplier version.");
        // Reassess observed prices, including on authorization continuation; caller data cannot replace them.
        var quoteFormat = SupplierFormats.Installed().Single(format => format.MediaType == SupplierFormats.Quote);
        foreach (var quotePart in Parts(quote.Response).Where(part => part.MediaType == SupplierFormats.Quote && part.Data is not null))
            quotePart.Data = SupplierFormats.Validate(quoteFormat, quotePart.Data!.Value, true);
        turn.SupplierResponses.Add(quote.Response);
        var assessment = await tools.AssessQuoteAsync(order.QuoteId, budget);
        turn.SupplierResponses.Clear();
        if (turn.PriceApprovalRequired) return $"Purchase not sent: {assessment}";
        await tools.SendNativeAsync(
            $"Place demo-only purchase order {JsonSerializer.Serialize(order.PurchaseOrderId)} for quote {JsonSerializer.Serialize(order.QuoteId)}.",
            data.GetRawText(), part.MediaType!, supplierTaskId, quote.ContextId, quote.Supplier.Identifier);
        var purchaseResponse = turn.SupplierResponses.Last();
        if (purchaseResponse.Task?.Status.State == TaskState.Completed)
            ValidateReceipt(purchaseResponse, order, assessment,
                turn.Conversation.SupplierTasks.Resolve(purchaseResponse.Task.Id, quote.Supplier.Identifier, "task").Mode ==
                    SupplierCommunicationMode.NaturalLanguage);
        if (purchaseResponse.Task is { Status.State: TaskState.AuthRequired or TaskState.InputRequired or TaskState.Submitted or TaskState.Working } purchaseTask)
        {
            turn.Conversation.NativePurchases[turn.Message.TaskId!] = new(data.GetRawText(), quote, purchaseTask.Id, budget, purchaseResponse);
        }
        return Summary(purchaseResponse);
    }

    internal static void ValidateReceipt(SendMessageResponse purchaseResponse, WinePurchaseOrder order, string assessment,
        bool allowTextReceipt = false)
    {
        var receipts = Parts(purchaseResponse).Where(item => item.MediaType == SupplierFormats.Receipt && item.Data is not null).ToArray();
        if (allowTextReceipt && receipts.Length == 0 &&
            Parts(purchaseResponse).Any(part => part.Text == $"Purchase order {order.PurchaseOrderId} recorded."))
            return;
        if (receipts.Length != 1)
            throw new InvalidOperationException("Completed native purchase requires exactly one verified receipt.");
        foreach (var receipt in receipts)
        {
            var value = receipt.Data!.Value.Deserialize<WineReceipt>(JsonOptions)!;
            using var assessmentData = JsonDocument.Parse(assessment);
            if (value.QuoteId != order.QuoteId || value.PurchaseOrderId != order.PurchaseOrderId ||
                value.TotalMinor / 100m != assessmentData.RootElement.GetProperty("totalUsd").GetDecimal() ||
                value.AgeVerification != "verified-demo-over-21")
                throw new InvalidOperationException("Supplier receipt does not match the assessed, verified purchase; inspect the original supplier task before retrying.");
        }
    }

    private static string? MetadataString(Message message, string key)
    {
        if (message.Metadata?.TryGetValue(key, out var value) != true) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"{key} must be a nonempty catalog value.");
        return value.GetString();
    }

    private static decimal? MetadataBudget(Message message)
    {
        if (message.Metadata?.TryGetValue("agentcon.maxTotalUsd", out var value) != true) return null;
        if (!value.TryGetDecimal(out var budget) || budget <= 0)
            throw new ArgumentException("agentcon.maxTotalUsd must be a positive number.");
        return budget;
    }

    private static IEnumerable<Part> Parts(SendMessageResponse response) =>
        (response.Task?.Artifacts ?? []).SelectMany(item => item.Parts)
            .Concat(response.Task?.Status.Message?.Parts ?? []).Concat(response.Message?.Parts ?? []);
    private static string Summary(SendMessageResponse response) =>
        response.Task?.Status.Message?.Parts.FirstOrDefault(item => item.Text is not null)?.Text
        ?? response.Message?.Parts.FirstOrDefault(item => item.Text is not null)?.Text
        ?? $"Supplier task state: {response.Task?.Status.State}.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

using A2A;
using System.Text.Json;

namespace BuyerAgent;

/// <summary>A supplier selected from an AI Catalog entry, with its validated A2A JSON-RPC endpoint.</summary>
public sealed record SupplierBinding(
    string Identifier, string? Version, string? DisplayName, Uri CardUrl, Uri Endpoint, string Catalog)
{
    public IReadOnlyList<string> InputModes { get; init; } = ["text/plain"];
    public IReadOnlyList<string> OutputModes { get; init; } = ["text/plain"];
    public IReadOnlyList<SupplierFormat> Formats { get; init; } = [];
    public SupplierCommunicationMode Mode { get; init; }
    public bool Streaming { get; init; }
    public object Describe() => new
    {
        identifier = Identifier,
        version = Version,
        displayName = DisplayName,
        cardUrl = CardUrl.AbsoluteUri,
        endpoint = Endpoint.AbsoluteUri,
        catalog = Catalog,
        inputModes = InputModes,
        communicationMode = Mode.ToString(),
        streaming = Streaming,
        understoodStructures = Formats.Select(format => new { format.MediaType, format.Direction, format.Schema })
    };
}

public sealed record SupplierSelection(SupplierBinding Binding, AgentCard Card);

public sealed class SupplierGateway(HttpClient httpClient, BuyerOptions options, AiCatalogClient catalog)
{
    public object AssessPrice(string wine, int vintage, string currency, int cases, int bottlesPerCase,
        decimal casePriceMinor, decimal deliveryMinor, decimal totalMinor, decimal? maxTotalUsd)
    {
        if (cases is < 1 or > 48 || bottlesPerCase is < 1 or > 100 ||
            casePriceMinor <= 0 || deliveryMinor < 0 || totalMinor <= 0)
            throw new ArgumentException("Quote quantities and amounts must be positive and bounded; delivery cannot be negative.");
        if (totalMinor != cases * casePriceMinor + deliveryMinor)
            return new { withinBallpark = false, reason = "Quote total does not match quantity, case price and delivery." };
        if (!wine.Equals("Barolo", StringComparison.OrdinalIgnoreCase) || vintage != 2019 || currency != "USD")
            return new { withinBallpark = false, reason = "No configured demo price reference for this wine, vintage or currency. Ask the caller before ordering." };
        var bottlePrice = casePriceMinor / bottlesPerCase / 100;
        var allowed = bottlePrice >= options.DemoMinBottlePriceUsd && bottlePrice <= options.DemoMaxBottlePriceUsd &&
            (maxTotalUsd is null || totalMinor / 100 <= maxTotalUsd);
        return new { withinBallpark = allowed, bottlePriceUsd = bottlePrice, totalUsd = totalMinor / 100,
            reference = "Configured 2019 Barolo demo price range.",
            minBottlePriceUsd = options.DemoMinBottlePriceUsd, maxBottlePriceUsd = options.DemoMaxBottlePriceUsd,
            reason = allowed ? "Within demo price range and supplied budget." : "Outside demo price range or caller budget; stop and ask the caller." };
    }
    public SupplierGateway ForMode(SupplierCommunicationMode mode) => new(httpClient, options.WithMode(mode), catalog);
    public SupplierGateway ForOptions(BuyerOptions snapshot) => new(httpClient, snapshot, catalog);
    public async Task<SupplierBinding> RefreshModeAsync(SupplierBinding supplier, CancellationToken cancellationToken)
    {
        var refreshed = (await SelectAsync(supplier.Identifier, supplier.Version, cancellationToken)).Binding;
        if (refreshed.Endpoint != supplier.Endpoint || refreshed.CardUrl != supplier.CardUrl)
            throw new InvalidOperationException("Catalog supplier destination changed; existing ownership cannot be redirected.");
        return refreshed;
    }
    public Task<CatalogSearchResult> SearchAsync(string? query, CancellationToken cancellationToken) =>
        catalog.SearchAsync(query, cancellationToken);

    public async Task<SupplierSelection> SelectAsync(string identifier, string? version, CancellationToken cancellationToken)
    {
        var entry = await catalog.FindAsync(identifier, version, cancellationToken);
        var card = await ResolveCardAsync(entry.CardUrl, cancellationToken);
        var endpoint = SelectEndpoint(card, entry.CardUrl);
        var mode = options.CommunicationMode;
        if (mode == SupplierCommunicationMode.Automatic)
            mode = card.DefaultInputModes.Any(type => type != "application/json" && type.EndsWith("+json", StringComparison.Ordinal))
                ? options.NativeStructuredSupport ? SupplierCommunicationMode.Native : SupplierCommunicationMode.LearnStructures
                : SupplierCommunicationMode.NaturalLanguage;
        var formats = mode switch
        {
            SupplierCommunicationMode.LearnStructures => await LearnAsync(card, entry.CardUrl, cancellationToken),
            SupplierCommunicationMode.Native => SupplierFormats.Native(card),
            _ => []
        };
        if (mode == SupplierCommunicationMode.NaturalLanguage &&
            !card.DefaultInputModes.Contains("text/plain"))
            throw new InvalidOperationException("NaturalLanguage mode requires a seller accepting text/plain.");
        if (mode != SupplierCommunicationMode.NaturalLanguage &&
            !formats.Any(format => format.Direction == "input"))
            throw new InvalidOperationException("Seller has no input structures understood in the configured communication mode.");
        return new SupplierSelection(
            new SupplierBinding(entry.Identifier, entry.Version, entry.DisplayName ?? card.Name, entry.CardUrl, endpoint, entry.Catalog)
            { InputModes = card.DefaultInputModes, OutputModes = card.DefaultOutputModes, Formats = formats,
                Mode = mode, Streaming = card.Capabilities.Streaming == true },
            card);
    }

    private async Task<IReadOnlyList<SupplierFormat>> LearnAsync(AgentCard card, Uri cardUrl, CancellationToken cancellationToken)
    {
        var extension = card.Capabilities.Extensions?.SingleOrDefault(item => item.Uri == SupplierFormats.Extension)
            ?? throw new InvalidOperationException("Seller does not advertise the schema-discovery extension.");
        var parameters = JsonSerializer.SerializeToElement(extension.Params);
        if (!parameters.TryGetProperty("schemas", out var schemas) || schemas.ValueKind != JsonValueKind.Array ||
            schemas.GetArrayLength() > 8)
            throw new InvalidOperationException("Seller schema discovery must contain at most eight schema entries.");
        var result = new List<SupplierFormat>();
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var descriptor in schemas.EnumerateArray())
        {
            var mediaType = descriptor.GetProperty("mediaType").GetString()!;
            var direction = descriptor.GetProperty("direction").GetString()!;
            if (direction is not ("input" or "output") ||
                !(direction == "input" ? card.DefaultInputModes : card.DefaultOutputModes).Contains(mediaType) ||
                mediaType == "application/json" || !mediaType.EndsWith("+json", StringComparison.Ordinal))
                throw new InvalidOperationException("Schema must describe an advertised typed JSON media type.");
            if (result.Any(format => format.MediaType == mediaType && format.Direction == direction))
                throw new InvalidOperationException("Seller advertised duplicate schema descriptors.");
            var url = new Uri(descriptor.GetProperty("url").GetString()!, UriKind.Absolute);
            options.ValidateRemoteUri(url, "Schema");
            if (url.GetLeftPart(UriPartial.Authority) != cardUrl.GetLeftPart(UriPartial.Authority))
                throw new InvalidOperationException("Schema URL must remain on the seller Agent Card origin.");
            if (!documents.TryGetValue(url.AbsoluteUri, out var document))
            {
                using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != "application/schema+json")
                    throw new InvalidOperationException("Schema endpoint must return application/schema+json.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var bytes = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (bytes.Length + count > 65536) throw new InvalidOperationException("Seller schema exceeds 64 KiB.");
                    bytes.Write(buffer, 0, count);
                }
                using var parsed = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
                document = parsed.RootElement.Clone();
                documents.Add(url.AbsoluteUri, document);
            }
            result.Add(SupplierFormats.Build(mediaType, direction, document, descriptor.GetProperty("definition").GetString()!));
        }
        return result;
    }

    public Part CreatePart(SupplierBinding supplier, string text, string? dataJson, string? mediaType)
    {
        if (supplier.Mode == SupplierCommunicationMode.NaturalLanguage)
        {
            if (dataJson is not null || mediaType is not (null or "text/plain"))
                throw new InvalidOperationException("NaturalLanguage mode permits only text/plain; do not send JSON data.");
            if (!supplier.InputModes.Contains("text/plain") || string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Seller must accept a nonempty text/plain message.");
            return new Part { Text = text, MediaType = "text/plain" };
        }
        if (dataJson is null || mediaType is null)
            throw new InvalidOperationException("Structured mode requires dataJson and an understood advertised mediaType.");
        if (!supplier.InputModes.Contains(mediaType))
            throw new InvalidOperationException("Seller does not advertise this input mediaType.");
        var format = supplier.Formats.SingleOrDefault(item => item.Direction == "input" && item.MediaType == mediaType)
            ?? throw new InvalidOperationException("No understood seller input schema/codec for this mediaType.");
        using var data = JsonDocument.Parse(dataJson);
        return new Part { Data = SupplierFormats.Validate(format, data.RootElement,
            supplier.Mode == SupplierCommunicationMode.Native), MediaType = mediaType };
    }

    internal void ValidateOutputs(SupplierBinding supplier, IEnumerable<Part> parts)
    {
        foreach (var part in parts)
        {
            if (part.Text is not null)
            {
                if (part.MediaType is not (null or "text/plain")) throw new InvalidOperationException("Unknown seller text type.");
                continue;
            }
            if (part.Data is not { } data) throw new InvalidOperationException("Unsupported seller output part.");
            if (supplier.Mode == SupplierCommunicationMode.NaturalLanguage)
            {
                throw new InvalidOperationException("NaturalLanguage mode accepts only text/plain seller replies.");
            }
            var format = supplier.Formats.SingleOrDefault(item => item.Direction == "output" && item.MediaType == part.MediaType)
                ?? throw new InvalidOperationException("Seller output has no understood schema/codec.");
            part.Data = SupplierFormats.Validate(format, data, supplier.Mode == SupplierCommunicationMode.Native);
        }
    }

    public async Task<AgentCard> ResolveCardAsync(Uri cardUrl, CancellationToken cancellationToken)
    {
        options.ValidateRemoteUri(cardUrl, "Agent Card");
        var origin = new Uri(cardUrl.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return await new A2ACardResolver(origin, httpClient, cardUrl.PathAndQuery)
            .GetAgentCardAsync(cancellationToken);
    }

    public async Task<SendMessageResponse> SendAsync(
        SupplierBinding supplier, Message message, CancellationToken cancellationToken, bool returnImmediately = true,
        Action<SupplierStream>? retainStream = null)
    {
        using var activity = BuyerTelemetry.Source.StartActivity("invoke_agent SupplierAgent", System.Diagnostics.ActivityKind.Client,
            System.Diagnostics.Activity.Current?.Context ?? default,
            tags: [new("gen_ai.operation.name", "invoke_agent"), new("gen_ai.agent.name", "SupplierAgent"),
                new("gen_ai.provider.name", "agentcon.supplier"), new("agentcon.a2a.task.id", message.TaskId),
                new("agentcon.a2a.context.id", message.ContextId)]);
        using var client = new A2AClient(supplier.Endpoint, httpClient);
        try
        {
            message.Metadata ??= new();
            message.Metadata.TryAdd(MessageInferenceCounter.MetadataKey, JsonSerializer.SerializeToElement(0));
            if (message.Parts.Count == 0 || supplier.Mode != SupplierCommunicationMode.NaturalLanguage && message.Parts.Count != 1)
                throw new InvalidOperationException("Send a nonempty natural-language message or exactly one understood typed data part.");
            foreach (var part in message.Parts)
            {
                if (part.ContentCase is not (PartContentCase.Text or PartContentCase.Data))
                    throw new InvalidOperationException("Unsupported outbound part type.");
                CreatePart(supplier, part.Text ?? "", part.Data?.GetRawText(), part.MediaType);
            }
            var request = new SendMessageRequest
            {
                Message = message,
                Configuration = new SendMessageConfiguration { ReturnImmediately = returnImmediately,
                    AcceptedOutputModes = (supplier.Mode == SupplierCommunicationMode.NaturalLanguage
                        ? new[] { "text/plain" }
                        : supplier.Formats.Where(format => format.Direction == "output").Select(format => format.MediaType)
                            .Append("text/plain").Distinct()).ToList() }
            };
            SendMessageResponse response;
            if (supplier.Streaming && retainStream is not null)
            {
                var stream = new SupplierStream(httpClient, this, supplier, message);
                try
                {
                    response = await stream.StartAsync(request, cancellationToken);
                    if (response.Task?.Status.State == TaskState.AuthRequired &&
                        response.Task.Status.Message?.Metadata?.ContainsKey(BuyerCard.AuthorizationExtensionUri) == true)
                        retainStream(stream);
                    else await stream.DisposeAsync();
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }
            }
            else response = await client.SendMessageAsync(request, cancellationToken);
            if (message.TaskId is not null && response.Task?.Id != message.TaskId ||
                message.ContextId is not null && (response.Task?.ContextId ?? response.Message?.ContextId) != message.ContextId)
                throw new InvalidOperationException("Supplier response changed the bound task/context IDs; inspect the original task before retrying.");
            ValidateOutputs(supplier, (response.Task?.Artifacts ?? []).SelectMany(artifact => artifact.Parts)
                .Concat(response.Task?.Status.Message?.Parts ?? []).Concat(response.Message?.Parts ?? []));
            activity?.SetTag("agentcon.a2a.task.id", response.Task?.Id);
            activity?.SetTag("agentcon.a2a.context.id", response.Task?.ContextId);
            if (response.Task?.Status.State is TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
                BuyerTelemetry.Error(activity, "supplier_task_failed");
            return response;
        }
        catch (Exception exception)
        {
            BuyerTelemetry.Error(activity, exception);
            throw;
        }
    }

    public async Task<AgentTask> GetTaskAsync(SupplierBinding supplier, string taskId, CancellationToken cancellationToken)
    {
        using var client = new A2AClient(supplier.Endpoint, httpClient);
        var task = await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
        ValidateOutputs(supplier, (task.Artifacts ?? []).SelectMany(artifact => artifact.Parts).Concat(task.Status.Message?.Parts ?? []));
        return task;
    }

    public Uri SelectEndpoint(AgentCard card, Uri cardUrl)
    {
        var agentInterface = card.SupportedInterfaces.FirstOrDefault(
            candidate => candidate.ProtocolBinding == "JSONRPC" && candidate.ProtocolVersion == "1.0")
            ?? throw new InvalidOperationException("Supplier must advertise an A2A 1.0 JSONRPC interface. Other bindings are not implemented yet.");
        if (!Uri.TryCreate(agentInterface.Url, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("Supplier card JSONRPC interface URL must be absolute.");
        }
        options.ValidateRemoteUri(endpoint, "Supplier interface");
        if (endpoint.Scheme != cardUrl.Scheme ||
            !string.Equals(endpoint.Host, cardUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            endpoint.Port != cardUrl.Port)
        {
            throw new InvalidOperationException("Supplier card JSONRPC interface must have the same origin as the catalog's Agent Card URL.");
        }
        if (options.IsSelf(endpoint))
        {
            throw new InvalidOperationException("The buyer cannot select itself as a supplier.");
        }
        return endpoint;
    }
}

public sealed class SupplierTurnTools(SupplierGateway gateway, BuyerTurn turn, CancellationToken turnCancellation)
{
    private readonly System.Diagnostics.ActivityContext _parent = System.Diagnostics.Activity.Current?.Context ?? default;

    public Task<string> SearchAsync(string? query = null) => ExecuteAsync("search_supplier_catalog", async () =>
    {
        var result = await gateway.SearchAsync(query, turnCancellation);
        return JsonSerializer.Serialize(new
        {
            untrustedCatalogData = true,
            result.Catalog,
            result.Query,
            matchCount = result.Matches.Count,
            matches = result.Matches.Select(entry => new
            {
                entry.Identifier,
                entry.Version,
                entry.DisplayName,
                entry.Description,
                entry.Tags,
                cardUrl = entry.CardUrl.AbsoluteUri,
                entry.Catalog
            }),
            result.AgentEntriesScanned,
            excludedSelfEntries = result.ExcludedSelfEntries,
            result.CatalogsRead,
            result.Warnings,
            result.Note,
            selectedSupplier = turn.Conversation.SelectedSupplier?.Describe()
        }, JsonOptions);
    });

    public Task<string> SelectAsync(string identifier, string? version = null) => ExecuteAsync("select_supplier", async () =>
    {
        var selection = await gateway.SelectAsync(identifier, version, turnCancellation);
        turn.Conversation.SelectedSupplier = selection.Binding;
        return JsonSerializer.Serialize(new
        {
            untrustedSupplierData = true,
            selectedSupplier = selection.Binding.Describe(),
            agentCard = JsonSerializer.SerializeToElement(selection.Card, A2AJsonUtilities.DefaultOptions)
        }, JsonOptions);
    });

    public Task<string> SendAsync(
        string text = "", string? dataJson = null, string? taskId = null, string? contextId = null, string? supplierIdentifier = null,
        string? mediaType = null) => SendCoreAsync(text, dataJson, taskId, contextId, supplierIdentifier, mediaType, null);

    public Task<string> SendNativeAsync(string text, string dataJson, string mediaType, string? taskId = null,
        string? contextId = null, string? supplierIdentifier = null) =>
        SendCoreAsync("", dataJson, taskId, contextId, supplierIdentifier, mediaType, text);

    private Task<string> SendCoreAsync(string text, string? dataJson, string? taskId, string? contextId,
        string? supplierIdentifier, string? mediaType, string? nativeText) => ExecuteAsync("send_supplier_message", async () =>
    {
        if (turn.SupplierStream is not null)
            throw new InvalidOperationException("Seller is waiting for input or wallet approval. Return the handoff; do not send another request in this turn.");
        if (turn.PriceApprovalRequired)
            throw new InvalidOperationException("Price assessment requires caller input. Do not send an order in this turn.");
        if (string.IsNullOrWhiteSpace(text) && dataJson is null)
        {
            throw new ArgumentException("Provide text or a structured JSON data part.");
        }
        var supplier = await gateway.RefreshModeAsync(ResolveDestination(taskId, contextId, supplierIdentifier), turnCancellation);
        if (nativeText is not null && supplier.Mode == SupplierCommunicationMode.NaturalLanguage)
        {
            text = nativeText;
            dataJson = null;
            mediaType = null;
        }
        var parts = new List<Part> { gateway.CreatePart(supplier, text, dataJson, mediaType) };
        var response = await gateway.SendAsync(supplier, new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = Role.User,
            Parts = parts,
            Metadata = new() { [MessageInferenceCounter.MetadataKey] = JsonSerializer.SerializeToElement(turn.Inference.SinceLastMessage()) },
            TaskId = taskId,
            ContextId = contextId
        }, turnCancellation, returnImmediately: !turn.NativeExecution,
            retainStream: stream => turn.SupplierStream = stream);
        Capture(supplier, response);
        return JsonSerializer.Serialize(new
        {
            supplier = supplier.Describe(),
            response = JsonSerializer.SerializeToElement(response, A2AJsonUtilities.DefaultOptions)
        }, JsonOptions);
    });

    public Task<string> GetTaskAsync(string taskId, string? supplierIdentifier = null) => ExecuteAsync("get_supplier_task", async () =>
    {
        var supplier = turn.Conversation.SupplierTasks.Resolve(taskId, supplierIdentifier, "task");
        var task = await gateway.GetTaskAsync(supplier, taskId, turnCancellation);
        Capture(supplier, new SendMessageResponse { Task = task });
        return JsonSerializer.Serialize(new
        {
            supplier = supplier.Describe(),
            task = JsonSerializer.SerializeToElement(task, A2AJsonUtilities.DefaultOptions)
        }, JsonOptions);
    });

    public Task<string> AssessQuoteAsync(string quoteId, decimal? maxTotalUsd = null) => ExecuteAsync("assess_supplier_quote", () =>
    {
        var parts = turn.SupplierResponses.SelectMany(response =>
            (response.Task?.Artifacts ?? []).SelectMany(artifact => artifact.Parts)
                .Concat(response.Task?.Status.Message?.Parts ?? []).Concat(response.Message?.Parts ?? [])).ToArray();
        var dataQuote = parts.Where(part => part.Data is not null).Select(part => part.Data!.Value)
            .FirstOrDefault(data => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("quoteId", out var id) &&
                id.GetString() == quoteId);
        object assessment;
        if (dataQuote.ValueKind != JsonValueKind.Undefined)
        {
            assessment = gateway.AssessPrice(dataQuote.GetProperty("wine").GetString()!,
                decimal.ToInt32(dataQuote.GetProperty("vintage").GetDecimal()),
                dataQuote.GetProperty("currency").GetString()!,
                decimal.ToInt32(dataQuote.GetProperty("cases").GetDecimal()),
                decimal.ToInt32(dataQuote.GetProperty("bottlesPerCase").GetDecimal()),
                dataQuote.GetProperty("casePriceMinor").GetDecimal(), dataQuote.GetProperty("deliveryMinor").GetDecimal(),
                dataQuote.GetProperty("totalMinor").GetDecimal(), maxTotalUsd);
        }
        else
        {
            var text = parts.Select(part => part.Text).FirstOrDefault(text =>
                !string.IsNullOrWhiteSpace(quoteId) && text?.Contains($"Quote {quoteId}:", StringComparison.Ordinal) == true);
            if (text is null)
                throw new InvalidOperationException("Inspect the supplier quote in this turn before assessing its price; do not invent a quote ID.");
            var match = System.Text.RegularExpressions.Regex.Match(text,
                @"^Quote [^:]+: (?<cases>\d+) cases of (?<vintage>\d{4}) (?<wine>[^()]+) \((?<bottles>\d+) bottles/case\) to .+?\. (?<currency>[A-Z]{3}) (?<case>\d+\.\d{2})/case \+ \k<currency> (?<delivery>\d+\.\d{2}) demo delivery = \k<currency> (?<total>\d+\.\d{2})\.",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success)
                assessment = new { withinBallpark = false, reason = "Cannot verify this text quote's pricing. Ask the caller; do not order." };
            else
            {
                decimal Amount(string group) => decimal.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
                assessment = gateway.AssessPrice(match.Groups["wine"].Value.Trim(), (int)Amount("vintage"),
                    match.Groups["currency"].Value, (int)Amount("cases"), (int)Amount("bottles"),
                    Amount("case") * 100, Amount("delivery") * 100, Amount("total") * 100, maxTotalUsd);
            }
        }
        turn.QuoteAssessments.Add(new { quoteId, assessment });
        turn.PriceApprovalRequired = !JsonSerializer.SerializeToElement(assessment).GetProperty("withinBallpark").GetBoolean();
        return Task.FromResult(JsonSerializer.Serialize(assessment, JsonOptions));
    });

    private SupplierBinding ResolveDestination(string? taskId, string? contextId, string? supplierIdentifier)
    {
        var conversation = turn.Conversation;
        if (taskId is not null)
        {
            var owner = conversation.SupplierTasks.Resolve(taskId, supplierIdentifier, "task");
            if (conversation.SupplierTaskStates.TryGetValue((owner.Identifier, taskId), out var state) &&
                state is TaskState.Completed or TaskState.Failed or TaskState.Rejected or TaskState.Canceled)
                throw new InvalidOperationException("Seller task is terminal. Start a new task by omitting taskId; retain its contextId to use the quote.");
            if (contextId is not null && !conversation.SupplierContexts.IsOwnedBy(contextId, owner))
            {
                throw new InvalidOperationException("The supplier context does not belong to the supplier that owns this task.");
            }
            return owner;
        }
        if (contextId is not null)
        {
            return conversation.SupplierContexts.Resolve(contextId, supplierIdentifier, "context");
        }
        var selected = conversation.SelectedSupplier
            ?? throw new InvalidOperationException("No supplier is selected. Search the AI Catalog and select a supplier by its catalog identifier first.");
        if (supplierIdentifier is not null && !string.Equals(selected.Identifier, supplierIdentifier, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Supplier '{supplierIdentifier}' is not the selected supplier ('{selected.Identifier}'). Select it explicitly first.");
        }
        return selected;
    }

    private async Task<string> ExecuteAsync(string tool, Func<Task<string>> operation)
    {
        using var activity = BuyerTelemetry.Source.StartActivity($"execute_tool {tool}",
            System.Diagnostics.ActivityKind.Internal, _parent,
            tags: [new("gen_ai.operation.name", "execute_tool"), new("gen_ai.tool.name", tool),
                new("gen_ai.agent.name", "BuyerAgent"), new("gen_ai.tool.type", "function")]);
        try
        {
            if (turn.ToolFailure is not null)
                throw new InvalidOperationException("A supplier tool already failed in this turn. Inspect the failure before retrying a possible order.");
            return await operation();
        }
        catch (Exception exception)
        {
            BuyerTelemetry.Error(activity, exception);
            turn.ToolFailure = exception;
            throw;
        }
    }

    private void Capture(SupplierBinding supplier, SendMessageResponse response)
    {
        turn.SupplierResponses.Add(response);
        if (response.Task is { } task)
        {
            turn.Conversation.SupplierTaskStates[(supplier.Identifier, task.Id)] = task.Status.State;
            turn.Conversation.SupplierTasks.Add(task.Id, supplier);
            if (!string.IsNullOrEmpty(task.ContextId))
            {
                turn.Conversation.SupplierContexts.Add(task.ContextId, supplier);
            }
        }
        if (response.Message?.ContextId is { Length: > 0 } messageContext)
        {
            turn.Conversation.SupplierContexts.Add(messageContext, supplier);
        }
        var quoteContext = response.Task?.ContextId ?? response.Message?.ContextId;
        var quoteId = SupplierFormats.ObservedQuoteId(
            (response.Task?.Artifacts ?? []).SelectMany(artifact => artifact.Parts)
                .Concat(response.Task?.Status.Message?.Parts ?? []).Concat(response.Message?.Parts ?? []));
        if (quoteId is not null && !string.IsNullOrEmpty(quoteContext))
        {
            var key = (supplier.Identifier, quoteId);
            if (turn.Conversation.NativeQuotes.Count >= 128 && !turn.Conversation.NativeQuotes.ContainsKey(key))
                throw new InvalidOperationException("Observed quote capacity reached; inspect supplier tasks before retrying.");
            turn.Conversation.NativeQuotes[key] = new(supplier, response, quoteContext);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

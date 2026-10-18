using A2A;
using System.Text.Json;

namespace BuyerAgent;

/// <summary>
/// A supplier selected from an AI Catalog entry, with its validated A2A JSON-RPC endpoint and, when both sides
/// support it, the SLIMRPC target (slim://org/namespace/app) its calls use instead.
/// </summary>
public sealed record SupplierBinding(
    string Identifier, string? Version, string? DisplayName, Uri CardUrl, Uri Endpoint, string Catalog)
{
    public IReadOnlyList<string> InputModes { get; init; } = ["text/plain"];
    public IReadOnlyList<string> OutputModes { get; init; } = ["text/plain"];
    public IReadOnlyList<SupplierFormat> Formats { get; init; } = [];
    public SupplierCommunicationMode Mode { get; init; }
    public bool Streaming { get; init; }
    public string? SlimTarget { get; init; }
    public object Describe() => new
    {
        identifier = Identifier,
        version = Version,
        displayName = DisplayName,
        cardUrl = CardUrl.AbsoluteUri,
        endpoint = Endpoint.AbsoluteUri,
        transport = SlimTarget is null ? "JSONRPC" : "SLIMRPC",
        slimTarget = SlimTarget,
        catalog = Catalog,
        inputModes = InputModes,
        communicationMode = Mode.ToString(),
        streaming = Streaming,
        understoodStructures = Formats.Select(format => new { format.MediaType, format.Direction, format.Schema })
    };
}

public sealed record SupplierSelection(SupplierBinding Binding, AgentCard Card);

public sealed class SupplierGateway(HttpClient httpClient, BuyerOptions options, AiCatalogClient catalog, BuyerSlim? slim = null)
{
    /// <summary>True when the buyer talks SLIM, so a first request that names no supplier polls them all in one multicast.</summary>
    public bool SlimEnabled => slim?.Configured == true;
    /// <summary>The transport this gateway's turn uses (from its options snapshot).</summary>
    public SupplierTransport Transport => options.SupplierTransport;

    public object AssessPrice(string wine, int vintage, string currency, int cases, int bottlesPerCase,
        decimal casePriceMinor, decimal deliveryMinor, decimal totalMinor, decimal? maxTotalUsd)
    {
        if (cases is < 1 or > 48 || bottlesPerCase is < 1 or > 100 ||
            casePriceMinor <= 0 || deliveryMinor < 0 || totalMinor <= 0)
            throw new ArgumentException("Quote quantities and amounts must be positive and bounded; delivery cannot be negative.");
        if (totalMinor != cases * casePriceMinor + deliveryMinor)
            return new { withinBallpark = false, reason = "Quote total does not match quantity, case price and delivery." };
        var reference = PriceReference(wine, vintage);
        if (reference is null || currency != "USD")
            return new { withinBallpark = false, reason = "No configured demo price reference for this wine, vintage or currency. Ask the caller before ordering." };
        var (label, minBottle, maxBottle) = reference.Value;
        var bottlePrice = casePriceMinor / bottlesPerCase / 100;
        var allowed = bottlePrice >= minBottle && bottlePrice <= maxBottle &&
            (maxTotalUsd is null || totalMinor / 100 <= maxTotalUsd);
        return new { withinBallpark = allowed, bottlePriceUsd = bottlePrice, totalUsd = totalMinor / 100,
            reference = $"Configured {label} demo price range.",
            minBottlePriceUsd = minBottle, maxBottlePriceUsd = maxBottle,
            reason = allowed ? "Within demo price range and supplied budget." : "Outside demo price range or caller budget; stop and ask the caller." };
    }
    // Buyer-side market references per bottle (USD); quotes for anything else are never auto-ordered.
    private (string Label, decimal MinBottle, decimal MaxBottle)? PriceReference(string wine, int vintage) => wine switch
    {
        _ when wine.Equals("Barolo", StringComparison.OrdinalIgnoreCase) && vintage == 2019 =>
            ("2019 Barolo", options.DemoMinBottlePriceUsd, options.DemoMaxBottlePriceUsd),
        _ when wine.Contains("Vermentino", StringComparison.OrdinalIgnoreCase) && vintage == 2020 =>
            ("2020 Vermentino di Gallura", 20m, 80m),
        _ when wine.Contains("Cannonau", StringComparison.OrdinalIgnoreCase) && vintage == 2019 =>
            ("2019 Cannonau di Sardegna", 8m, 40m),
        _ when wine.Contains("Margaux", StringComparison.OrdinalIgnoreCase) && vintage == 2015 =>
            ("2015 Château Margaux", 400m, 2000m),
        _ when wine.Contains("Ruinart", StringComparison.OrdinalIgnoreCase) && vintage == 2013 =>
            ("2013 Dom Ruinart Blanc de Blancs", 100m, 600m),
        _ => null
    };
    public SupplierGateway ForMode(SupplierCommunicationMode mode) => new(httpClient, options.WithMode(mode), catalog, slim);
    public SupplierGateway ForOptions(BuyerOptions snapshot) => new(httpClient, snapshot, catalog, slim);

    /// <summary>
    /// The client for a supplier call: SLIMRPC when the binding has a SLIM target and the buyer is connected to the node,
    /// otherwise HTTP JSON-RPC. Dispose <c>Owned</c> (the per-call HTTP client); SLIM clients are shared and owned by
    /// <see cref="BuyerSlim"/>.
    /// </summary>
    internal (IA2AClient Client, IDisposable? Owned) OpenClient(SupplierBinding supplier)
    {
        var transport = options.SupplierTransport;
        if (transport != SupplierTransport.Http && supplier.SlimTarget is { } target && slim?.ClientFor(target) is { } slimClient)
            return (slimClient, null);
        if (transport == SupplierTransport.Slim)
            throw new InvalidOperationException(supplier.SlimTarget is null
                ? "The SLIM transport is selected, but this seller's Agent Card lists no SLIMRPC interface."
                : "The SLIM transport is selected, but the buyer's SLIM connection is not ready.");
        if (supplier.Endpoint.Scheme == "slim")
            throw new InvalidOperationException(transport == SupplierTransport.Http
                ? "The HTTP transport is selected, but this seller's Agent Card lists no JSON-RPC interface."
                : "This seller is reachable only over SLIM, and the buyer's SLIM connection is not ready.");
        var http = new A2AClient(supplier.Endpoint, httpClient);
        return (http, http);
    }
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
        // Prefer the card's SLIMRPC interface when the buyer is configured for SLIM; JSON-RPC stays the fallback.
        var slimTarget = slim?.Configured == true
            ? card.SupportedInterfaces.FirstOrDefault(candidate => candidate.ProtocolBinding == "SLIMRPC" &&
                candidate.ProtocolVersion == "1.0" && BuyerSlim.TargetName(candidate.Url) is not null)?.Url
            : null;
        // A seller reachable only over SLIM (e.g. in a cluster) publishes a card without a JSON-RPC interface.
        var endpoint = slimTarget is not null && !card.SupportedInterfaces.Any(candidate => candidate.ProtocolBinding == "JSONRPC")
            ? new Uri(slimTarget)
            : SelectEndpoint(card, entry.CardUrl);
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
                Mode = mode, Streaming = card.Capabilities.Streaming == true, SlimTarget = slimTarget },
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
        activity?.SetTag("agentcon.a2a.transport",
            supplier.SlimTarget is null || options.SupplierTransport == SupplierTransport.Http ? "JSONRPC" : "SLIMRPC");
        var (client, owned) = OpenClient(supplier);
        using var ownedClient = owned;
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
                var stream = new SupplierStream(this, supplier, message);
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

    /// <summary>One supplier's answer to a multicast: its response, or why there is none.</summary>
    public sealed record MulticastReply(SupplierBinding Supplier, SendMessageResponse? Response, string? Error);

    /// <summary>
    /// Sends the same message to every supplier. Over SLIM (every supplier on SLIM and the buyer connected) this is a
    /// single SendMessage on a group channel. Otherwise it is an HTTP fan-out: the message to each supplier with
    /// returnImmediately, then GetTask polling until each task is final or needs input/authorization.
    /// </summary>
    public async Task<(string Transport, IReadOnlyList<MulticastReply> Replies)> MulticastAsync(
        IReadOnlyList<SupplierBinding> suppliers, Message message, CancellationToken cancellationToken)
    {
        if (suppliers.Count == 0) throw new InvalidOperationException("No supplier understands this message.");
        foreach (var supplier in suppliers)
            foreach (var part in message.Parts)
                CreatePart(supplier, part.Text ?? "", part.Data?.GetRawText(), part.MediaType);
        using var activity = BuyerTelemetry.Source.StartActivity("invoke_agent SupplierAgents (multicast)", System.Diagnostics.ActivityKind.Client,
            System.Diagnostics.Activity.Current?.Context ?? default,
            tags: [new("gen_ai.operation.name", "invoke_agent"), new("agentcon.a2a.multicast.members", suppliers.Count)]);
        var timeout = TimeSpan.FromSeconds(Math.Min(options.RequestTimeoutSeconds, 60));
        message.Metadata ??= new();
        message.Metadata.TryAdd(MessageInferenceCounter.MetadataKey, JsonSerializer.SerializeToElement(0));
        var accepted = suppliers.SelectMany(supplier => supplier.Mode == SupplierCommunicationMode.NaturalLanguage
                ? ["text/plain"]
                : supplier.Formats.Where(format => format.Direction == "output").Select(format => format.MediaType))
            .Append("text/plain").Distinct().ToList();
        var useSlim = slim is not null && options.SupplierTransport != SupplierTransport.Http &&
            suppliers.All(supplier => supplier.SlimTarget is not null) &&
            slim.CanMulticast(suppliers.Select(supplier => supplier.SlimTarget!));
        var previousGroup = ExchangeStore.CurrentGroup.Value;
        ExchangeStore.CurrentGroup.Value = (Guid.NewGuid().ToString("N"), useSlim
            ? $"SLIM multicast · 1 SendMessage to {suppliers.Count} suppliers"
            : $"HTTP fan-out · {suppliers.Count} × SendMessage + GetTask polling");
        try
        {
            return await MulticastCoreAsync(suppliers, message, useSlim, accepted, timeout, activity, cancellationToken);
        }
        finally
        {
            ExchangeStore.CurrentGroup.Value = previousGroup;
        }
    }

    private async Task<(string Transport, IReadOnlyList<MulticastReply> Replies)> MulticastCoreAsync(
        IReadOnlyList<SupplierBinding> suppliers, Message message, bool useSlim, List<string> accepted, TimeSpan timeout,
        System.Diagnostics.Activity? activity, CancellationToken cancellationToken)
    {
        if (useSlim)
        {
            activity?.SetTag("agentcon.a2a.transport", "SLIMRPC multicast");
            var byTarget = suppliers.ToDictionary(supplier => BuyerSlim.TargetName(supplier.SlimTarget!)!, StringComparer.Ordinal);
            var request = new SendMessageRequest
            {
                Message = message,
                Configuration = new SendMessageConfiguration { ReturnImmediately = false, AcceptedOutputModes = accepted }
            };
            var replies = new Dictionary<string, MulticastReply>(StringComparer.Ordinal);
            Exception? failed = null;
            try
            {
                var multicastTimeout = TimeSpan.FromSeconds(Math.Min(options.MulticastTimeoutSeconds, timeout.TotalSeconds));
                await foreach (var (target, response) in slim!.MulticastAsync(byTarget.Keys.ToList(), request, multicastTimeout, cancellationToken))
                {
                    var supplier = byTarget[target];
                    try
                    {
                        ValidateOutputs(supplier, (response.Task?.Artifacts ?? []).SelectMany(artifact => artifact.Parts)
                            .Concat(response.Task?.Status.Message?.Parts ?? []).Concat(response.Message?.Parts ?? []));
                        replies[target] = new(supplier, response, null);
                    }
                    catch (InvalidOperationException exception)
                    {
                        replies[target] = new(supplier, null, exception.Message);
                    }
                }
            }
            catch (Exception exception) when (replies.Count == 0 && !cancellationToken.IsCancellationRequested)
            {
                // No supplier has the request (e.g. the group session could not be set up): send it to each one instead.
                failed = exception;
            }
            if (failed is not null)
            {
                activity?.SetTag("agentcon.a2a.multicast.error", failed.GetType().Name);
                // A transport failure may mean the buyer's connection to the node was replaced: give the per-supplier
                // fallback fresh sessions instead of ones opened on the old connection.
                if (ReconnectingSlimClient.SessionMayBeDead(failed)) await slim!.ResetIdleClientsAsync(failed);
                ExchangeStore.CurrentGroup.Value = (Guid.NewGuid().ToString("N"),
                    $"SLIM multicast failed ({failed.GetType().Name}) · {suppliers.Count} × SendMessage instead");
            }
            else return ("SLIMRPC multicast: one SendMessage to the group", byTarget
                .Select(pair => replies.GetValueOrDefault(pair.Key) ?? new(pair.Value, null, "No multicast reply before the timeout."))
                .ToList());
        }
        activity?.SetTag("agentcon.a2a.transport", useSlim ? "SLIMRPC per supplier after multicast failure" : "HTTP fan-out with polling");
        var fanout = await Task.WhenAll(suppliers.Select(async supplier =>
        {
            try
            {
                // Each HTTP request is its own A2A message; the content is identical.
                var copy = JsonSerializer.Deserialize<Message>(JsonSerializer.Serialize(message, A2AJsonUtilities.DefaultOptions),
                    A2AJsonUtilities.DefaultOptions)!;
                copy.MessageId = Guid.NewGuid().ToString("N");
                var response = await SendAsync(supplier, copy, cancellationToken, returnImmediately: true);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(timeout);
                // Poll with backoff (0.5 s doubling to 2 s) so a slow supplier does not flood the dashboard.
                var interval = TimeSpan.FromMilliseconds(500);
                while (response.Task is { } task && task.Status.State is TaskState.Submitted or TaskState.Working)
                {
                    await Task.Delay(interval, deadline.Token);
                    interval = TimeSpan.FromMilliseconds(Math.Min(interval.TotalMilliseconds * 2, 2000));
                    response = new SendMessageResponse { Task = await GetTaskAsync(supplier, task.Id, deadline.Token) };
                }
                return new MulticastReply(supplier, response, null);
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or A2AException ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return new MulticastReply(supplier, null, exception is OperationCanceledException
                    ? "No final answer before the polling timeout." : exception.Message);
            }
        }));
        return ("HTTP fan-out: one SendMessage per supplier, then GetTask polling", fanout);
    }

    public async Task<AgentTask> GetTaskAsync(SupplierBinding supplier, string taskId, CancellationToken cancellationToken)
    {
        var (client, owned) = OpenClient(supplier);
        using var ownedClient = owned;
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
        if (await PollAllSuppliersAsync(taskId, contextId, mediaType))
            return await SendToAllCoreAsync(nativeText ?? text, dataJson, mediaType, null,
                "The caller named no supplier, so this first request went to every catalog supplier in one SLIM multicast.");
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

    /// <summary>
    /// Sends one message to every catalog supplier that understands it (SLIM multicast or HTTP fan-out with polling)
    /// and records each reply like send_supplier_message does, so quotes can be assessed and ordered point-to-point.
    /// </summary>
    public Task<string> SendToAllAsync(string text = "", string? dataJson = null, string? mediaType = null) =>
        ExecuteAsync("send_to_all_suppliers", async () =>
    {
        if (turn.SupplierStream is not null)
            throw new InvalidOperationException("Seller is waiting for input or wallet approval. Return the handoff; do not send another request in this turn.");
        if (string.IsNullOrWhiteSpace(text) && dataJson is null)
            throw new ArgumentException("Provide text or a structured JSON data part.");
        return await SendToAllCoreAsync(text, dataJson, mediaType, null, null);
    });

    /// <summary>
    /// On SLIM, a first request in which the caller names no catalog supplier polls every seller in one multicast.
    /// Continuations, purchases and a second broadcast in the same turn stay point-to-point. Whether the caller
    /// named a supplier comes from the caller's own message, not from the model's choice of target.
    /// </summary>
    private async Task<bool> PollAllSuppliersAsync(string? taskId, string? contextId, string? mediaType)
    {
        if (!gateway.SlimEnabled || turn.LastBroadcast is not null ||
            taskId is not null || contextId is not null || mediaType == SupplierFormats.Purchase)
            return false;
        var suppliers = (await gateway.SearchAsync(null, turnCancellation)).Matches;
        var request = string.Join(" ", turn.Message.Parts.Where(part => part.Text is not null).Select(part => part.Text));
        return suppliers.Count > 1 &&
            !suppliers.Any(entry => request.Contains(entry.Identifier, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> SendToAllCoreAsync(string text, string? dataJson, string? mediaType,
        IReadOnlyList<string>? candidates, string? note)
    {
        var catalog = await gateway.SearchAsync(null, turnCancellation);
        var included = new List<SupplierBinding>();
        var skipped = new List<object>();
        foreach (var entry in catalog.Matches.Where(entry => candidates is null || candidates.Contains(entry.Identifier)))
        {
            try
            {
                var supplier = (await gateway.SelectAsync(entry.Identifier, entry.Version, turnCancellation)).Binding;
                // Typed messages go only to suppliers that understand the media type; text only to text suppliers.
                var understood = dataJson is null
                    ? supplier.Mode == SupplierCommunicationMode.NaturalLanguage
                    : supplier.Mode != SupplierCommunicationMode.NaturalLanguage && supplier.InputModes.Contains(mediaType ?? "");
                if (understood) included.Add(supplier);
                else skipped.Add(new { supplier = entry.Identifier, reason = "does not accept this message type in its current mode" });
            }
            catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
            {
                skipped.Add(new { supplier = entry.Identifier, reason = exception.Message });
            }
        }
        if (included.Count == 0)
        {
            // Not a tool failure: tell the model which message types the suppliers accept so it can resend this turn.
            return JsonSerializer.Serialize(new
            {
                sent = false,
                reason = "No catalog supplier accepts this message type in its current mode. Resend with a type they accept.",
                accepted = await Task.WhenAll(catalog.Matches.Select(async entry =>
                {
                    try
                    {
                        var supplier = (await gateway.SelectAsync(entry.Identifier, entry.Version, turnCancellation)).Binding;
                        return (object)new { supplier = entry.Identifier, communicationMode = supplier.Mode.ToString(),
                            textAccepted = supplier.Mode == SupplierCommunicationMode.NaturalLanguage,
                            understoodStructures = supplier.Formats.Where(format => format.Direction == "input")
                                .Select(format => new { format.MediaType, format.Schema }) };
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
                    {
                        return new { supplier = entry.Identifier, error = exception.Message };
                    }
                })),
                skipped
            }, JsonOptions);
        }
        var part = gateway.CreatePart(included[0], text, dataJson, mediaType);
        var (transport, replies) = await gateway.MulticastAsync(included, new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = Role.User,
            Parts = [part],
            Metadata = new() { [MessageInferenceCounter.MetadataKey] = JsonSerializer.SerializeToElement(turn.Inference.SinceLastMessage()) }
        }, turnCancellation);
        var captured = new List<SendMessageResponse>();
        foreach (var reply in replies.Where(reply => reply.Response is not null))
        {
            Capture(reply.Supplier, reply.Response!);
            captured.Add(reply.Response!);
        }
        turn.LastBroadcast = captured;
        return JsonSerializer.Serialize(new
        {
            untrustedSupplierData = true,
            transport,
            note,
            // Compact per-supplier summary: full responses can exceed the model's tool-result limit.
            replies = replies.Select(reply =>
            {
                var task = reply.Response?.Task;
                var message = reply.Response?.Message;
                var parts = (task?.Artifacts ?? []).SelectMany(artifact => artifact.Parts)
                    .Concat(task?.Status.Message?.Parts ?? []).Concat(message?.Parts ?? []).ToList();
                return new
                {
                    supplier = reply.Supplier.Identifier,
                    transport = reply.Supplier.SlimTarget is null || gateway.Transport == SupplierTransport.Http
                        ? "JSONRPC" : "SLIMRPC",
                    taskId = task?.Id,
                    contextId = task?.ContextId ?? message?.ContextId,
                    state = task?.Status.State.ToString(),
                    text = string.Join(" ", parts.Where(part => part.Text is not null).Select(part => part.Text)),
                    data = parts.Where(part => part.Data is not null).Select(part => part.Data).ToList(),
                    error = reply.Error
                };
            }),
            skipped
        }, JsonOptions);
    }

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

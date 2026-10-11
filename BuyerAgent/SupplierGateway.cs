using A2A;
using System.Text.Json;

namespace BuyerAgent;

/// <summary>A supplier selected from an AI Catalog entry, with its validated A2A JSON-RPC endpoint.</summary>
public sealed record SupplierBinding(
    string Identifier, string? Version, string? DisplayName, Uri CardUrl, Uri Endpoint, string Catalog)
{
    public object Describe() => new
    {
        identifier = Identifier,
        version = Version,
        displayName = DisplayName,
        cardUrl = CardUrl.AbsoluteUri,
        endpoint = Endpoint.AbsoluteUri,
        catalog = Catalog
    };
}

public sealed record SupplierSelection(SupplierBinding Binding, AgentCard Card);

public sealed class SupplierGateway(HttpClient httpClient, BuyerOptions options, AiCatalogClient catalog)
{
    public Task<CatalogSearchResult> SearchAsync(string? query, CancellationToken cancellationToken) =>
        catalog.SearchAsync(query, cancellationToken);

    public async Task<SupplierSelection> SelectAsync(string identifier, string? version, CancellationToken cancellationToken)
    {
        var entry = await catalog.FindAsync(identifier, version, cancellationToken);
        var card = await ResolveCardAsync(entry.CardUrl, cancellationToken);
        var endpoint = SelectEndpoint(card, entry.CardUrl);
        return new SupplierSelection(
            new SupplierBinding(entry.Identifier, entry.Version, entry.DisplayName ?? card.Name, entry.CardUrl, endpoint, entry.Catalog),
            card);
    }

    public async Task<AgentCard> ResolveCardAsync(Uri cardUrl, CancellationToken cancellationToken)
    {
        options.ValidateRemoteUri(cardUrl, "Agent Card");
        var origin = new Uri(cardUrl.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        return await new A2ACardResolver(origin, httpClient, cardUrl.PathAndQuery)
            .GetAgentCardAsync(cancellationToken);
    }

    public async Task<SendMessageResponse> SendAsync(
        SupplierBinding supplier, Message message, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Source.StartActivity("invoke_agent SupplierAgent", System.Diagnostics.ActivityKind.Client,
            System.Diagnostics.Activity.Current?.Context ?? default,
            tags: [new("gen_ai.operation.name", "invoke_agent"), new("gen_ai.agent.name", "SupplierAgent"),
                new("gen_ai.provider.name", "agentcon.supplier"), new("agentcon.a2a.task.id", message.TaskId),
                new("agentcon.a2a.context.id", message.ContextId)]);
        using var client = new A2AClient(supplier.Endpoint, httpClient);
        try
        {
            var response = await client.SendMessageAsync(new SendMessageRequest
            {
                Message = message,
                Configuration = new SendMessageConfiguration { ReturnImmediately = true }
            }, cancellationToken);
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
        return await client.GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken);
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
        string text, string? dataJson = null, string? taskId = null, string? contextId = null, string? supplierIdentifier = null) => ExecuteAsync("send_supplier_message", async () =>
    {
        if (string.IsNullOrWhiteSpace(text) && dataJson is null)
        {
            throw new ArgumentException("Provide text or a structured JSON data part.");
        }
        var supplier = ResolveDestination(taskId, contextId, supplierIdentifier);
        var parts = new List<Part>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            parts.Add(Part.FromText(text));
        }
        if (dataJson is not null)
        {
            using var document = JsonDocument.Parse(dataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("The supplier data part must be a JSON object.");
            }
            parts.Add(Part.FromData(document.RootElement.Clone()));
        }
        var response = await gateway.SendAsync(supplier, new Message
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = Role.User,
            Parts = parts,
            TaskId = taskId,
            ContextId = contextId
        }, turnCancellation);
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

    private SupplierBinding ResolveDestination(string? taskId, string? contextId, string? supplierIdentifier)
    {
        var conversation = turn.Conversation;
        if (taskId is not null)
        {
            var owner = conversation.SupplierTasks.Resolve(taskId, supplierIdentifier, "task");
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
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

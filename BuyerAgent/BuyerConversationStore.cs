using System.Collections.Concurrent;

namespace BuyerAgent;

public sealed class BuyerConversationStore(BuyerOptions options)
{
    private readonly ConcurrentDictionary<string, BuyerConversation> _conversations = new();
    private readonly object _admissionLock = new();

    public BuyerConversation Get(string contextId)
    {
        lock (_admissionLock)
        {
            if (_conversations.TryGetValue(contextId, out var conversation))
            {
                return conversation;
            }
            if (_conversations.Count >= options.MaxConversations)
            {
                throw new InvalidOperationException("Buyer conversation capacity reached. Restart the demo service to clear its in-memory conversations.");
            }
            conversation = new BuyerConversation();
            _conversations[contextId] = conversation;
            return conversation;
        }
    }
}

public sealed class BuyerConversation
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public List<BuyerHistoryTurn> History { get; } = [];

    /// <summary>The supplier explicitly selected from the AI Catalog for new supplier conversations.</summary>
    public SupplierBinding? SelectedSupplier { get; set; }

    /// <summary>Supplier tasks created in this conversation, bound to the supplier that created them.</summary>
    public SupplierOwnership SupplierTasks { get; } = new();

    /// <summary>Supplier context IDs returned in this conversation, bound to the supplier that issued them.</summary>
    public SupplierOwnership SupplierContexts { get; } = new();

    public void Remember(BuyerHistoryTurn turn)
    {
        History.Add(turn);
        while (History.Count > 8)
        {
            History.RemoveAt(0);
        }
    }
}

/// <summary>Maps supplier-issued IDs to their issuing supplier so a later selection change cannot misroute them.</summary>
public sealed class SupplierOwnership
{
    private readonly Dictionary<string, List<SupplierBinding>> _owners = new(StringComparer.Ordinal);

    public IEnumerable<(string Id, SupplierBinding Supplier)> Entries =>
        _owners.SelectMany(pair => pair.Value.Select(owner => (pair.Key, owner)));

    public void Add(string id, SupplierBinding supplier)
    {
        if (!_owners.TryGetValue(id, out var owners))
        {
            _owners[id] = owners = [];
        }
        if (!owners.Contains(supplier))
        {
            owners.Add(supplier);
        }
    }

    public bool Contains(string id) => _owners.ContainsKey(id);

    public bool IsOwnedBy(string id, SupplierBinding supplier) =>
        _owners.TryGetValue(id, out var owners) && owners.Contains(supplier);

    public SupplierBinding Resolve(string id, string? supplierIdentifier, string kind)
    {
        if (!_owners.TryGetValue(id, out var owners))
        {
            throw new InvalidOperationException($"Only supplier {kind}s created in this buyer conversation can be accessed.");
        }
        var candidates = supplierIdentifier is null
            ? owners
            : owners.Where(owner => string.Equals(owner.Identifier, supplierIdentifier, StringComparison.Ordinal)).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException($"Supplier {kind} '{id}' does not belong to supplier '{supplierIdentifier}'."),
            _ => throw new InvalidOperationException(
                $"Supplier {kind} '{id}' exists at multiple suppliers ({string.Join(", ", candidates.Select(owner => owner.Identifier))}). Specify supplierIdentifier.")
        };
    }
}

public sealed record BuyerHistoryTurn(string Request, string Response, IReadOnlyList<string> SupplierResponses);

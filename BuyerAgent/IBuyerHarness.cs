using A2A;

namespace BuyerAgent;

public interface IBuyerHarness
{
    Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken);
}

public sealed class BuyerTurn(Message message, BuyerConversation conversation)
{
    public SupplierCommunicationMode? CommunicationMode { get; init; }
    public BuyerOptions? OptionsSnapshot { get; init; }
    public bool NativeExecution { get; set; }
    public string? NativeContinuationError { get; set; }
    public MessageInferenceCounter Inference { get; } = new();
    public Message Message { get; } = message;
    public BuyerConversation Conversation { get; } = conversation;
    public List<SendMessageResponse> SupplierResponses { get; } = [];
    public Exception? ToolFailure { get; set; }
    public List<object> QuoteAssessments { get; } = [];
    public bool PriceApprovalRequired { get; set; }
    public SupplierStream? SupplierStream { get; set; }
}

public sealed class MessageInferenceCounter
{
    public const string MetadataKey = "agentcon.llmCalls";
    private int _calls;
    private int _sent;
    public void Record() => Interlocked.Increment(ref _calls);
    public int Total => Volatile.Read(ref _calls);
    public int SinceLastMessage()
    {
        var calls = Volatile.Read(ref _calls);
        return calls - Interlocked.Exchange(ref _sent, calls);
    }
}

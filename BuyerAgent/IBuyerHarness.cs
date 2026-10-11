using A2A;

namespace BuyerAgent;

public interface IBuyerHarness
{
    Task<string> RunAsync(BuyerTurn turn, CancellationToken cancellationToken);
}

public sealed class BuyerTurn(Message message, BuyerConversation conversation)
{
    public Message Message { get; } = message;
    public BuyerConversation Conversation { get; } = conversation;
    public List<SendMessageResponse> SupplierResponses { get; } = [];
    public Exception? ToolFailure { get; set; }
}

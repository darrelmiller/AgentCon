using A2A;

namespace BuyerAgent;

public static class SupplierAuthorization
{
    public static string Instructions(AgentTask task)
    {
        var metadata = task.Status.Message?.Metadata;
        if (metadata is null || !metadata.TryGetValue(BuyerCard.AuthorizationExtensionUri, out var extension))
            return "The supplier requires wallet authorization, but did not provide an OID4VP presentation request. " +
                "No purchase is confirmed. Ask the supplier operator to configure its presentation verifier.";
        if (!extension.TryGetProperty("authorizationRequest", out var request) ||
            !request.TryGetProperty("client_id", out var client) ||
            !request.TryGetProperty("request_uri", out var requestUri) ||
            client.ValueKind != System.Text.Json.JsonValueKind.String ||
            requestUri.ValueKind != System.Text.Json.JsonValueKind.String ||
            !Uri.TryCreate(requestUri.GetString(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 ||
            string.IsNullOrWhiteSpace(client.GetString()) || client.GetString()!.Length > 2048)
            throw new InvalidOperationException("Supplier returned an invalid OID4VP authorization request.");
        var wallet = $"openid4vp://?client_id={Uri.EscapeDataString(client.GetString()!)}" +
            $"&request_uri={Uri.EscapeDataString(uri.AbsoluteUri)}&request_uri_method=get";
        var page = "";
        if (metadata.TryGetValue("agentcon.presentation", out var presentation) &&
            presentation.TryGetProperty("presentationUrl", out var url) &&
            Uri.TryCreate(url.GetString(), UriKind.Absolute, out var pageUri) &&
            pageUri.Scheme == "https" && pageUri.UserInfo.Length == 0 &&
            pageUri.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority))
            page = $"Open {pageUri.AbsoluteUri} to display the QR code. ";
        return "The purchase is waiting for wallet authorization; no order has been confirmed. " +
            page + $"On your phone, open this presentation in Sphereon Wallet: {wallet}\n" +
            "Select the over_21 credential issued by agentcon-issuer and approve disclosure. " +
            "Do not paste the credential or its presentation into this chat. " +
            "After approving in the wallet, return here and ask me to continue the pending purchase. " +
            $"I must continue supplier task {task.Id} in context {task.ContextId}, not start another order. " +
            "The issuer's credential proves possession of its signed demo claim, not independently verified age.";
    }
}

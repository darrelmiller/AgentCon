using A2A;
using System.Text.Json;

namespace BuyerAgent;

public static class BuyerCard
{
    public const string AuthorizationExtensionUri =
        "https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/v1";

    public static AgentCard Create(BuyerOptions options) => new()
    {
        Name = "AgentCon Wine Buyer",
        Description = "A demo wine buyer powered by the GitHub Copilot SDK, that discovers wine suppliers through an AI Catalog and communicates with them through A2A.",
        Version = "0.1.0",
        SupportedInterfaces =
        [
            new AgentInterface
            {
                Url = new Uri(new Uri(options.ListenUrl), "/a2a").AbsoluteUri,
                ProtocolBinding = "JSONRPC",
                ProtocolVersion = "1.0"
            }
        ],
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = options.CommunicationMode == SupplierCommunicationMode.NaturalLanguage
            ? ["text/plain"] : ["text/plain", "application/json", SupplierFormats.Quote, SupplierFormats.Receipt],
        Capabilities = new AgentCapabilities
        {
            Streaming = true,
            PushNotifications = false,
            Extensions =
            [
                new AgentExtension
                {
                    Uri = AuthorizationExtensionUri,
                    Description = "Relays supplier OID4VP authorization requests to the caller; wallet invocation remains external.",
                    Required = false,
                    Params = JsonSerializer.SerializeToElement(new { oid4vpVersions = new[] { "1.0" } })
                }
            ]
        },
        Skills =
        [
            new AgentSkill
            {
                Id = "wine-buying",
                Name = "Wine buyer",
                Description = "Search the AI Catalog for wine suppliers, select one by catalog identifier, request quotes, send text or structured messages, and inspect supplier tasks.",
                Tags = ["wine", "buyer", "a2a", "demo"],
                InputModes = ["text/plain"],
                OutputModes = options.CommunicationMode == SupplierCommunicationMode.NaturalLanguage
                    ? ["text/plain"] : ["text/plain", "application/json", SupplierFormats.Quote, SupplierFormats.Receipt],
                Examples = ["Ask the supplier for a quote for 12 cases of 2019 Barolo delivered to San Jose."]
            }
        ]
    };
}

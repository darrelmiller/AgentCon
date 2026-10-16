using A2A;
using System.Text.Json;

namespace BuyerAgent;

public static class BuyerCard
{
    public const string AuthorizationExtensionUri =
        "https://github.com/a2aproject/experimental-ext-oid4vp-auth/tree/main/v1";

    /// <param name="slimUrl">slim://&lt;name&gt; when the buyer also serves A2A over SLIMRPC.</param>
    public static AgentCard Create(BuyerOptions options, string? slimUrl = null) => new()
    {
        Name = "AgentCon Wine Buyer",
        Description = "A demo wine buyer that discovers suppliers through an AI Catalog and negotiates A2A parts automatically. Text requests use Copilot; enabled wine-v1 typed inbound requests execute through installed codecs without buyer inference.",
        Version = "0.1.0",
        SupportedInterfaces =
        [
            new AgentInterface
            {
                Url = new Uri(new Uri(options.ListenUrl), "/a2a").AbsoluteUri,
                ProtocolBinding = "JSONRPC",
                ProtocolVersion = "1.0"
            },
            .. (slimUrl is null ? [] : new[] { new AgentInterface { Url = slimUrl, ProtocolBinding = "SLIMRPC", ProtocolVersion = "1.0" } })
        ],
        DefaultInputModes = options.NativeStructuredSupport ? ["text/plain", SupplierFormats.Rfq, SupplierFormats.Purchase] : ["text/plain"],
        DefaultOutputModes = options.CommunicationMode == SupplierCommunicationMode.NaturalLanguage
            ? ["text/plain"] : ["text/plain", SupplierFormats.Quote, SupplierFormats.Receipt],
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
                },
                .. (options.NativeStructuredSupport ? new[] { new AgentExtension
                {
                    Uri = SupplierFormats.Extension,
                    Description = "Installed deterministic wine-v1 input codecs. Route RFQs by agentcon.supplierIdentifier/version catalog metadata (or unique catalog match); purchases use an observed quote in this buyer context. Optional positive agentcon.maxTotalUsd budget. Streaming suppliers complete the original buyer task automatically after external wallet approval; nonstreaming suppliers require the same purchase on the same buyer task.",
                    Required = false,
                    Params = JsonSerializer.SerializeToElement(new { schemas = SupplierFormats.Installed().Select(format => new
                    {
                        mediaType = format.MediaType, direction = format.Direction,
                        url = new Uri(new Uri(options.ListenUrl), "/schemas/wine-v1.schema.json").AbsoluteUri,
                        definition = format.MediaType switch { SupplierFormats.Rfq => "rfq", SupplierFormats.Purchase => "purchaseOrder",
                            SupplierFormats.Quote => "quote", _ => "receipt" }
                    }) })
                } } : [])
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
                InputModes = options.NativeStructuredSupport ? ["text/plain", SupplierFormats.Rfq, SupplierFormats.Purchase] : ["text/plain"],
                OutputModes = options.CommunicationMode == SupplierCommunicationMode.NaturalLanguage
                    ? ["text/plain"] : ["text/plain", SupplierFormats.Quote, SupplierFormats.Receipt],
                Examples = ["Ask the supplier for a quote for 12 cases of 2019 Barolo delivered to San Jose."]
            }
        ]
    };
}

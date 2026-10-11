using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;
using A2A;

namespace BuyerAgent;

public sealed record SupplierFormat(string MediaType, string Direction, JsonElement Schema, JsonSchema Validator);

public static class SupplierFormats
{
    public const string Extension = "urn:agentcon:extension:structured-schemas:v1";
    public const string Rfq = "application/vnd.agentcon.demo.wine-rfq.v1+json";
    public const string Purchase = "application/vnd.agentcon.demo.wine-purchase-order.v1+json";
    public const string Quote = "application/vnd.agentcon.demo.wine-quote.v1+json";
    public const string Receipt = "application/vnd.agentcon.demo.wine-order-receipt.v1+json";
    private static readonly JsonSerializerOptions NativeOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new WholeNumberConverter() }
    };

    public static SupplierFormat Build(string mediaType, string direction, JsonElement document, string definition)
    {
        if (document.ValueKind != JsonValueKind.Object ||
            !document.TryGetProperty("$defs", out var definitions) || definitions.ValueKind != JsonValueKind.Object ||
            !definitions.TryGetProperty(definition, out _))
            throw new InvalidOperationException("Advertised schema definition is missing.");
        CheckReferences(document);
        var schema = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = definitions,
            ["$ref"] = "#/$defs/" + definition.Replace("~", "~0").Replace("/", "~1")
        });
        return new(mediaType, direction, schema, JsonSchema.FromText(schema.GetRawText()));
    }

    private static void CheckReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "$id" or "$dynamicRef" or "$recursiveRef")
                    throw new InvalidOperationException("Only local, fixed JSON Schema references are supported.");
                if (property.Name == "$ref" && (property.Value.ValueKind != JsonValueKind.String ||
                    !property.Value.GetString()!.StartsWith("#/", StringComparison.Ordinal)))
                    throw new InvalidOperationException("Remote schema references are not supported.");
                CheckReferences(property.Value);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckReferences(item);
    }

    public static IReadOnlyList<SupplierFormat> Native(AgentCard card)
    {
        using var stream = typeof(SupplierFormats).Assembly.GetManifestResourceStream("BuyerAgent.WineSchema.json")!;
        using var schema = JsonDocument.Parse(stream);
        var formats = new List<SupplierFormat>();
        foreach (var (type, definition, direction) in new[]
        {
            (Rfq, "rfq", "input"), (Purchase, "purchaseOrder", "input"),
            (Quote, "quote", "output"), (Receipt, "receipt", "output")
        })
            if ((direction == "input" ? card.DefaultInputModes : card.DefaultOutputModes).Contains(type))
                formats.Add(Build(type, direction, schema.RootElement, definition));
        return formats;
    }

    public static JsonElement Validate(SupplierFormat format, JsonElement data, bool native)
    {
        if (!format.Validator.Evaluate(JsonNode.Parse(data.GetRawText()), new EvaluationOptions
            { RequireFormatValidation = true }).IsValid)
            throw new InvalidOperationException($"Payload does not satisfy the understood schema for {format.MediaType}.");
        if (!native) return data.Clone();
        return format.MediaType switch
        {
            Rfq => RoundTrip<WineRfq>(data, value =>
            {
                if (!value.Wine.Equals("Barolo", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(value.DeliveryCity) || value.DeliveryCity.Any(char.IsControl) ||
                    value.DeliveryCountry is not ("US" or "CA"))
                    throw new InvalidOperationException("Native wine RFQ supports Barolo with a nonempty delivery city in the US or Canada.");
            }),
            Purchase => RoundTrip<WinePurchaseOrder>(data),
            Quote => RoundTrip<WineQuote>(data, value =>
            {
                if (value.TotalMinor != value.Cases * 14400 + 2500)
                    throw new InvalidOperationException("Native quote total is inconsistent.");
            }),
            Receipt => RoundTrip<WineReceipt>(data, value =>
            {
                if (value.TotalMinor < 0) throw new InvalidOperationException("Native receipt total must be non-negative.");
            }),
            _ => throw new InvalidOperationException("No native codec exists for this media type.")
        };
    }

    private static JsonElement RoundTrip<T>(JsonElement data, Action<T>? validate = null)
    {
        var value = data.Deserialize<T>(NativeOptions) ?? throw new InvalidOperationException("Native payload is null.");
        validate?.Invoke(value);
        return JsonSerializer.SerializeToElement(value, NativeOptions);
    }

    private sealed class WholeNumberConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var number = reader.GetDecimal();
            if (number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue)
                throw new JsonException("Expected a bounded whole number.");
            return (int)number;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }
}

public sealed record WineRfq(string Kind, string SchemaVersion, bool DemoOnly, string Wine, int Vintage, int Cases, string DeliveryCity,
    string DeliveryCountry = "US");
public sealed record WinePurchaseOrder(string Kind, string SchemaVersion, bool DemoOnly, string QuoteId, string PurchaseOrderId);
public sealed record WineQuote(string Kind, string SchemaVersion, bool DemoOnly, string QuoteId, string Wine,
    int Vintage, int Cases, int BottlesPerCase, string DeliveryCity, string Currency, int CasePriceMinor,
    int DeliveryMinor, int TotalMinor, DateTimeOffset ExpiresAt, string DeliveryCountry = "US");
public sealed record WineReceipt(string Kind, string SchemaVersion, bool DemoOnly, string OrderId,
    string PurchaseOrderId, string QuoteId, string Status, string Currency, int TotalMinor,
    string AgeVerification, string Payment, string Fulfillment);

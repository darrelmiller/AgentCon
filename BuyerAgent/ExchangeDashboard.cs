using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BuyerAgent;

public sealed class ExchangeDashboard(
    HttpClient http, AiCatalogClient catalog, BuyerOptions options, ExchangeStore store,
    ILogger<ExchangeDashboard> logger, BuyerSlim? slim = null)
{
    public static bool IsLocalRequest(HttpContext context, BuyerOptions options)
    {
        var request = context.Request;
        var expected = new Uri(options.ListenUrl);
        if (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote)) return false;
        if (!Uri.TryCreate($"http://{request.Host}", UriKind.Absolute, out var host) ||
            !host.IsLoopback || host.Port != expected.Port) return false;
        if (request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        var origin = request.Headers.Origin;
        if (origin.Count > 0 && (origin.Count != 1 ||
            !string.Equals(origin[0], $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)))
            return false;
        return request.Method is "GET" or "HEAD" || origin.Count == 1;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/demo", async (HttpContext context) =>
        {
            using var resource = typeof(ExchangeDashboard).Assembly.GetManifestResourceStream("BuyerAgent.ExchangeDashboard.html")
                ?? throw new InvalidOperationException("Embedded exchange dashboard is missing.");
            var html = await new StreamReader(resource).ReadToEndAsync(context.RequestAborted);
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
            context.Response.Headers.ContentSecurityPolicy =
                $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; connect-src 'self'; " +
                "base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html.Replace("{{NONCE}}", nonce, StringComparison.Ordinal), context.RequestAborted);
        });
        app.MapGet("/demo/state", async (HttpContext context, ExchangeDashboard dashboard) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await dashboard.StateAsync(context.RequestAborted));
        });
        app.MapGet("/demo/catalog", async (HttpContext context, ExchangeDashboard dashboard) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return await dashboard.CatalogAsync(context.RequestAborted);
        });
        app.MapGet("/demo/agent-card", async (HttpContext context, string? identifier, ExchangeDashboard dashboard) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await dashboard.CardAsync(identifier, context.RequestAborted);
        });
        app.MapPost("/demo/clear", async (HttpContext context, ExchangeDashboard dashboard) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await dashboard.StateAsync(context.RequestAborted, clear: true);
            return Results.NoContent();
        });
        app.MapPost("/demo/native", (HttpContext context, NativeChange change, BuyerOptions options,
            A2A.A2AServerOptions serverOptions, ILogger<ExchangeDashboard> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            options.NativeStructuredSupport = change.Enabled;
            // The SDK copies input modes at startup; keep its shared HTTP/SLIM validator aligned with the live card.
            serverOptions.SupportedInputModes = BuyerCard.Create(options).DefaultInputModes.ToArray();
            logger.LogInformation("Buyer native support changed to {Enabled}; active turns retain their snapshot.", change.Enabled);
            return Results.Ok(new { nativeStructuredSupport = change.Enabled });
        });
        app.MapPost("/demo/supplier-structures", async (HttpContext context, SupplierChange change, ExchangeDashboard dashboard) =>
            await dashboard.ChangeSupplierAsync(change, context.RequestAborted));
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record NativeChange([property: JsonRequired] bool Enabled);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record SupplierChange([property: JsonRequired] string Identifier, [property: JsonRequired] bool Enabled);

    private async Task<IResult> CatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Results.Json(await catalog.ReadRootAsync(cancellationToken),
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Dashboard AI Catalog unavailable: {ErrorType}", exception.GetType().Name);
            return Results.Json(new { error = "Cannot read the configured AI Catalog. Check configuration and buyer console." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<IResult> ChangeSupplierAsync(SupplierChange change, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SupplierControlToken))
            return Results.Json(new { error = "Configure process-scoped Buyer:SupplierControlToken and matching supplier control token." }, statusCode: 409);
        try
        {
            var snapshot = await catalog.LoadAsync(cancellationToken);
            var entries = snapshot.AgentEntries.Where(entry => entry.Identifier == change.Identifier).ToArray();
            if (entries.Length != 1 || !entries[0].CardUrl.IsLoopback)
                return Results.BadRequest(new { error = "Controls require a unique loopback supplier from the configured catalog." });
            var url = new Uri(entries[0].CardUrl.GetLeftPart(UriPartial.Authority) + "/demo/structures");
            if (options.IsSelf(url)) return Results.BadRequest(new { error = "Cannot configure the buyer as a supplier." });
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new("Bearer", options.SupplierControlToken);
            request.Content = JsonContent.Create(new { enabled = change.Enabled });
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Supplier control rejected with HTTP {Status}", (int)response.StatusCode);
                return Results.Json(new { error = $"Supplier control rejected: HTTP {(int)response.StatusCode}." }, statusCode: 502);
            }
            return Results.Ok(new { structuredDataParts = change.Enabled });
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Supplier control unavailable: {ErrorType}", exception.GetType().Name);
            return Results.Json(new { error = "Supplier control unavailable. Check configuration and console." }, statusCode: 502);
        }
    }

    private async Task<IResult> CardAsync(string? identifier, CancellationToken cancellationToken)
    {
        if (identifier is null)
            return Results.Json(BuyerCard.Create(options.WithMode(options.CommunicationMode)), A2A.A2AJsonUtilities.DefaultOptions);
        try
        {
            var snapshot = await catalog.LoadAsync(cancellationToken);
            var entries = snapshot.AgentEntries.Where(entry => entry.Identifier == identifier).ToArray();
            if (entries.Length != 1)
                return Results.BadRequest(new { error = "Choose a unique seller from the configured catalog." });
            var url = entries[0].CardUrl;
            options.ValidateRemoteUri(url, "Agent Card");
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var bytes = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (bytes.Length + count > 65536)
                    throw new InvalidOperationException("Agent Card exceeds 64 KiB.");
                bytes.Write(buffer, 0, count);
            }
            using var card = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            if (card.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Agent Card must be a JSON object.");
            return Results.Json(card.RootElement.Clone());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
            HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Dashboard Agent Card unavailable: {ErrorType}", exception.GetType().Name);
            return Results.Json(new { error = "Cannot load this Agent Card. Check the seller, catalog and buyer console." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<object> StateAsync(CancellationToken cancellationToken, bool clear = false)
    {
        await store.DashboardGate.WaitAsync(cancellationToken);
        try { return await ReadStateAsync(cancellationToken, clear); }
        finally { store.DashboardGate.Release(); }
    }

    private async Task<object> ReadStateAsync(CancellationToken cancellationToken, bool clear)
    {
        var refreshedSuppliers = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        object[] sellers = [];
        string? catalogError = null;
        IReadOnlyList<string> warnings = [];
        try
        {
            var snapshot = await catalog.LoadAsync(cancellationToken);
            warnings = snapshot.Warnings;
            // Discovery is metadata only; the viewer never selects suppliers or runs tasks.
            sellers = await Task.WhenAll(snapshot.AgentEntries.Take(AiCatalogClient.MaxResults).Select(async entry =>
            {
                var health = new Uri(entry.CardUrl.GetLeftPart(UriPartial.Authority) + "/health");
                var status = "unavailable";
                bool? structured = null;
                string? problem = null;
                string? slimUrl = null;
                AgentLlmTotals? llm = null;
                try
                {
                    using var response = await http.GetAsync(health, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        status = "HTTP ready";
                        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                        if (body.RootElement.TryGetProperty("llm", out var counters) &&
                            counters.ValueKind == JsonValueKind.Object)
                        {
                            var sample = counters.Deserialize<AgentLlmTotals>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                            if (sample is not null && !string.IsNullOrWhiteSpace(sample.InstanceId) &&
                                sample.Calls >= 0 && sample.TimedCalls >= 0 &&
                                double.IsFinite(sample.ResponseTimeMs) && sample.ResponseTimeMs >= 0)
                            {
                                llm = store.SupplierLlm(entry.Identifier, sample);
                                refreshedSuppliers.TryAdd(entry.Identifier, 0);
                            }
                            else throw new JsonException("Invalid supplier LLM counters.");
                        }
                        if (body.RootElement.TryGetProperty("structuredDataParts", out var enabled) &&
                            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                            structured = enabled.GetBoolean();
                        // A supplier serving SLIMRPC reports its slim:// name; its SLIM exchanges are recorded under it.
                        if (body.RootElement.TryGetProperty("slim", out var slim) && slim.ValueKind == JsonValueKind.String &&
                            BuyerSlim.TargetName(slim.GetString()!) is { } name)
                            slimUrl = $"slim://{name}";
                    }
                    else problem = $"Health endpoint returned HTTP {(int)response.StatusCode}.";
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException ||
                    exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    problem = exception is OperationCanceledException ? "Health probe timed out." : "Health endpoint unreachable.";
                }
                return (object)new
                {
                    identifier = entry.Identifier, name = entry.DisplayName ?? entry.Identifier,
                    origin = entry.CardUrl.GetLeftPart(UriPartial.Authority),
                    cardUrl = entry.CardUrl.AbsoluteUri, slimUrl, status, problem, llm, structuredDataParts = structured,
                    controlsEnabled = entry.CardUrl.IsLoopback && !string.IsNullOrWhiteSpace(options.SupplierControlToken) && structured is not null
                };
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException)
        {
            logger.LogWarning("Exchange dashboard catalog unavailable: {ErrorType}", exception.GetType().Name);
            catalogError = "Cannot read the configured catalog. Check the catalog and buyer console.";
        }
        if (clear) store.Clear(refreshedSuppliers.Keys.ToHashSet());
        return new
        {
            capturedAt = DateTimeOffset.UtcNow,
            buyer = new { name = "Buyer Agent", origin = options.ListenUrl.TrimEnd('/'), status = "HTTP ready",
                // slimUrl once serving on SLIM; slimConfigured without slimUrl means the node is unreachable.
                slimUrl = slim?.ServingUrl, slimConfigured = slim?.Configured == true,
                communicationMode = options.CommunicationMode.ToString(), nativeStructuredSupport = options.NativeStructuredSupport,
                llm = store.BuyerLlm.Snapshot() },
            sellers, catalogError, warnings, capture = store.Snapshot()
        };
    }
}

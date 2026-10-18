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
        app.MapPost("/demo/clear", (HttpContext context, ExchangeStore exchanges) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            exchanges.Clear();
            return Results.NoContent();
        });
        app.MapPost("/demo/native", (HttpContext context, NativeChange change, BuyerOptions options,
            ILogger<ExchangeDashboard> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            options.NativeStructuredSupport = change.Enabled;
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

    private async Task<object> StateAsync(CancellationToken cancellationToken)
    {
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
                var probe = entry.CardUrl.AbsolutePath == "/.well-known/agent-card.json"
                    ? await ProbeHealthAsync(entry.CardUrl, cancellationToken)
                    : await ProbePublishedAsync(entry.CardUrl, cancellationToken);
                return (object)new
                {
                    identifier = entry.Identifier, name = entry.DisplayName ?? entry.Identifier,
                    origin = probe.Origin, cardUrl = entry.CardUrl.AbsoluteUri, slimUrl = probe.SlimUrl,
                    slimVerified = probe.SlimVerified, status = probe.Status, problem = probe.Problem,
                    structuredDataParts = probe.Structured,
                    controlsEnabled = entry.CardUrl.IsLoopback && !string.IsNullOrWhiteSpace(options.SupplierControlToken) && probe.Structured is not null
                };
            }));
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or HttpRequestException)
        {
            logger.LogWarning("Exchange dashboard catalog unavailable: {ErrorType}", exception.GetType().Name);
            catalogError = "Cannot read the configured catalog. Check the catalog and buyer console.";
        }
        return new
        {
            capturedAt = DateTimeOffset.UtcNow,
            buyer = new { name = "Buyer Agent", origin = options.ListenUrl.TrimEnd('/'), status = "HTTP ready",
                // slimUrl once serving on SLIM; slimConfigured without slimUrl means the node is unreachable.
                slimUrl = slim?.ServingUrl, slimConfigured = slim?.Configured == true,
                communicationMode = options.CommunicationMode.ToString(), nativeStructuredSupport = options.NativeStructuredSupport },
            sellers, catalogError, warnings, capture = store.Snapshot()
        };
    }

    private sealed record SellerProbe(string Origin, string Status, string? Problem = null, string? SlimUrl = null,
        bool SlimVerified = true, bool? Structured = null);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Uri, (DateTimeOffset At, SellerProbe Probe)> PublishedProbes = new();
    private static readonly TimeSpan PublishedProbeInterval = TimeSpan.FromSeconds(15);

    /// <summary>A local supplier serves its card at its origin and reports its state on /health.</summary>
    private async Task<SellerProbe> ProbeHealthAsync(Uri cardUrl, CancellationToken cancellationToken)
    {
        var origin = cardUrl.GetLeftPart(UriPartial.Authority);
        try
        {
            using var response = await http.GetAsync(new Uri(origin + "/health"), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new(origin, "unavailable", $"Health endpoint returned HTTP {(int)response.StatusCode}.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            bool? structured = body.RootElement.TryGetProperty("structuredDataParts", out var enabled) &&
                enabled.ValueKind is JsonValueKind.True or JsonValueKind.False ? enabled.GetBoolean() : null;
            // A supplier serving SLIMRPC reports its slim:// name; its SLIM exchanges are recorded under it.
            var slimUrl = body.RootElement.TryGetProperty("slim", out var slimName) && slimName.ValueKind == JsonValueKind.String &&
                BuyerSlim.TargetName(slimName.GetString()!) is { } name ? $"slim://{name}" : null;
            return new(origin, "HTTP ready", SlimUrl: slimUrl, Structured: structured);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(origin, "unavailable", exception is OperationCanceledException ? "Health probe timed out." : "Health endpoint unreachable.");
        }
    }

    /// <summary>
    /// A published card (for example a cloud seller behind an OIDC gateway) lives under the seller's base URL, which keys
    /// the seller here, since several sellers share one host. There is no /health: the card gives the interfaces, and a
    /// GetTask for an unknown task on its JSON-RPC interface (no model call; the buyer's bearer token is attached for
    /// allowlisted origins) shows the seller is up and accepts the buyer. Results are cached, as the page polls often.
    /// </summary>
    private async Task<SellerProbe> ProbePublishedAsync(Uri cardUrl, CancellationToken cancellationToken)
    {
        if (PublishedProbes.TryGetValue(cardUrl, out var cached) && DateTimeOffset.UtcNow - cached.At < PublishedProbeInterval)
            return cached.Probe;
        var origin = new Uri(cardUrl, ".").AbsoluteUri.TrimEnd('/');
        SellerProbe probe;
        try
        {
            using var cardResponse = await http.GetAsync(cardUrl, cancellationToken);
            if (!cardResponse.IsSuccessStatusCode)
                probe = new(origin, "unavailable", $"Agent Card returned HTTP {(int)cardResponse.StatusCode}.");
            else
            {
                using var card = JsonDocument.Parse(await cardResponse.Content.ReadAsStringAsync(cancellationToken));
                string? jsonRpc = null, slimUrl = null;
                if (card.RootElement.TryGetProperty("supportedInterfaces", out var interfaces) && interfaces.ValueKind == JsonValueKind.Array)
                    foreach (var item in interfaces.EnumerateArray())
                    {
                        if (!item.TryGetProperty("protocolBinding", out var binding) || !item.TryGetProperty("url", out var url) ||
                            url.ValueKind != JsonValueKind.String) continue;
                        if (binding.GetString() == "SLIMRPC" && BuyerSlim.TargetName(url.GetString()!) is { } name) slimUrl = $"slim://{name}";
                        // Probe only the seller's own endpoint, under the card's base URL.
                        if (binding.GetString() == "JSONRPC" && url.GetString()!.StartsWith(origin + "/", StringComparison.Ordinal)) jsonRpc = url.GetString();
                    }
                bool? structured = card.RootElement.TryGetProperty("capabilities", out var capabilities) &&
                    capabilities.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Array
                    ? extensions.EnumerateArray().Any(extension => extension.TryGetProperty("uri", out var uri) &&
                        uri.GetString() == "urn:agentcon:extension:structured-schemas:v1")
                    : null;
                probe = jsonRpc is null
                    ? new(origin, slimUrl is null ? "unavailable" : "SLIM only", slimUrl is null ? "Agent Card lists no usable interface." : null,
                        slimUrl, false, structured)
                    : await ProbeJsonRpcAsync(origin, new Uri(jsonRpc), slimUrl, structured, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or IOException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            probe = new(origin, "unavailable", exception is OperationCanceledException ? "Probe timed out." :
                exception is IOException or InvalidOperationException ? "Bearer token unavailable." : "Agent Card unreachable.");
        }
        PublishedProbes[cardUrl] = (DateTimeOffset.UtcNow, probe);
        return probe;
    }

    private async Task<SellerProbe> ProbeJsonRpcAsync(string origin, Uri endpoint, string? slimUrl, bool? structured,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = "dashboard-probe", method = "GetTask",
                @params = new { id = "agentcon-dashboard-probe" } })
        };
        request.Headers.Add("A2A-Version", "1.0");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new(origin, "unavailable", $"HTTPS endpoint refused the buyer (HTTP {(int)response.StatusCode}); check the login.",
                slimUrl, false, structured);
        if (!response.IsSuccessStatusCode)
            return new(origin, "unavailable", $"HTTPS endpoint returned HTTP {(int)response.StatusCode}.", slimUrl, false, structured);
        // Any JSON-RPC answer (here "task not found") means the request reached the seller.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body.RootElement.TryGetProperty("jsonrpc", out _)
            ? new(origin, "HTTPS ready", null, slimUrl, false, structured)
            : new(origin, "unavailable", "HTTPS endpoint did not answer JSON-RPC.", slimUrl, false, structured);
    }
}

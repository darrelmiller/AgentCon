using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace BuyerAgent;

public sealed class ExchangeDashboard(
    HttpClient http, AiCatalogClient catalog, BuyerOptions options, ExchangeStore store,
    ILogger<ExchangeDashboard> logger)
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
        app.MapPost("/demo/mode", (HttpContext context, ModeChange change, BuyerOptions options,
            ILogger<ExchangeDashboard> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!Enum.TryParse<SupplierCommunicationMode>(change.Mode, false, out var mode) ||
                !Enum.IsDefined(mode) || mode.ToString() != change.Mode)
                return Results.BadRequest(new { error = "Choose NaturalLanguage, LearnStructures, or Native." });
            options.CommunicationMode = mode;
            logger.LogInformation("Buyer communication mode changed to {Mode}; active turns retain their mode.", mode);
            return Results.Ok(new { communicationMode = mode.ToString() });
        });
    }

    public sealed record ModeChange(string Mode);

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
                var health = new Uri(entry.CardUrl.GetLeftPart(UriPartial.Authority) + "/health");
                var status = "unavailable";
                string? problem = null;
                try
                {
                    using var response = await http.GetAsync(health, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.IsSuccessStatusCode) status = "HTTP ready";
                    else problem = $"Health endpoint returned HTTP {(int)response.StatusCode}.";
                }
                catch (Exception exception) when (exception is HttpRequestException ||
                    exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    problem = exception is OperationCanceledException ? "Health probe timed out." : "Health endpoint unreachable.";
                }
                return (object)new
                {
                    identifier = entry.Identifier, name = entry.DisplayName ?? entry.Identifier,
                    origin = entry.CardUrl.GetLeftPart(UriPartial.Authority),
                    cardUrl = entry.CardUrl.AbsoluteUri, status, problem
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
                communicationMode = options.CommunicationMode.ToString() },
            sellers, catalogError, warnings, capture = store.Snapshot()
        };
    }
}

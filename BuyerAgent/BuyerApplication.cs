using A2A.AspNetCore;
using System.Net;
using OpenTelemetry.Trace;

namespace BuyerAgent;

public static class BuyerApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configureHost = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
        var section = builder.Configuration.GetSection(BuyerOptions.SectionName);
        if (section["SupplierBaseUrl"] is { Length: > 0 })
        {
            throw new InvalidOperationException("Buyer:SupplierBaseUrl was removed. Publish the supplier's Agent Card in the AI Catalog and set Buyer:CatalogUrl if not using the repository catalog.json.");
        }
        var options = section.Get<BuyerOptions>() ?? new BuyerOptions();
        if (!string.IsNullOrWhiteSpace(options.CatalogUrl))
        {
            options.CatalogUrl = options.ParseCatalogLocation(options.CatalogUrl, builder.Environment.ContentRootPath).AbsoluteUri;
        }
        options.Validate();
        if (options.CommunicationMode != SupplierCommunicationMode.Automatic)
            throw new InvalidOperationException("Buyer communication is now automatic. Remove Buyer:CommunicationMode overrides; use Buyer:NativeStructuredSupport.");
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 128 * 1024);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ExchangeStore>();
        builder.Services.AddTransient<ExchangeCaptureHandler>();
        builder.Services.AddHttpClient<ExchangeDashboard>(client => client.Timeout = TimeSpan.FromSeconds(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        BuyerTelemetry.Configure(builder);
        builder.Services.AddSingleton<BuyerConversationStore>();
        builder.Services.AddSingleton<CopilotRuntime>();
        builder.Services.AddHttpClient<AiCatalogClient>(client =>
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false
            });
        builder.Services.AddHttpClient<SupplierGateway>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            client.DefaultRequestHeaders.Add("A2A-Extensions", BuyerCard.AuthorizationExtensionUri);
        }).AddHttpMessageHandler<ExchangeCaptureHandler>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        });
        builder.Services.AddSingleton<IBuyerHarness, CopilotBuyerHarness>();
        var card = BuyerCard.Create(options);
        builder.Services.AddA2AAgent<BuyerHandler>(card);
        builder.Services.AddSingleton<BuyerSlim>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<BuyerSlim>());
        builder.Services.AddSingleton<SupplierStreamMonitor>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SupplierStreamMonitor>());
        configureHost?.Invoke(builder);

        var app = builder.Build();
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (app.Services.GetService<OpenTelemetry.Trace.TracerProvider>() is { } provider &&
                !provider.ForceFlush(10000))
                Console.Error.WriteLine("Buyer OpenTelemetry shutdown flush failed.");
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/demo"))
            {
                if (!ExchangeDashboard.IsLocalRequest(context, options))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
                await next(context);
                return;
            }
            if (context.Request.Headers.ContainsKey("Origin") ||
                (context.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", agent = card.Name }));
        app.MapA2A("/a2a");
        app.MapGet("/schemas/wine-v1.schema.json", () =>
        {
            if (!options.NativeStructuredSupport) return Results.NotFound();
            using var stream = typeof(SupplierFormats).Assembly.GetManifestResourceStream("BuyerAgent.WineSchema.json")!;
            return Results.Text(new StreamReader(stream).ReadToEnd(), "application/schema+json");
        });
        app.MapGet("/.well-known/agent-card.json", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(BuyerCard.Create(options.WithMode(options.CommunicationMode),
                context.RequestServices.GetRequiredService<BuyerSlim>().ServingUrl), A2A.A2AJsonUtilities.DefaultOptions);
        });
        if (options.ExchangeCaptureEnabled) ExchangeDashboard.Map(app);
        return app;
    }
}

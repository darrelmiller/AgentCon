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
        builder.WebHost.UseUrls(options.ListenUrl);
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 128 * 1024);
        builder.Services.AddSingleton(options);
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
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        });
        builder.Services.AddSingleton<IBuyerHarness, CopilotBuyerHarness>();
        var card = BuyerCard.Create(options);
        builder.Services.AddA2AAgent<BuyerHandler>(card);
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
        app.MapWellKnownAgentCard(card);
        return app;
    }
}

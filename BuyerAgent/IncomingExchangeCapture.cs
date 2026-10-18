using System.Diagnostics;
using System.Text;

namespace BuyerAgent;

public sealed class IncomingExchangeCapture(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ExchangeStore store)
    {
        if (!store.Enabled || !context.Request.Path.StartsWithSegments("/a2a"))
        {
            await next(context);
            return;
        }

        // Use the configured loopback origin, not an untrusted Host header. Never capture credentials or cookies.
        var options = context.RequestServices.GetRequiredService<BuyerOptions>();
        var endpoint = new Uri(new Uri(options.ListenUrl), context.Request.PathBase + context.Request.Path);
        var key = store.Begin(endpoint, inbound: true, httpMethod: context.Request.Method,
            requestContentType: context.Request.ContentType);
        var started = Stopwatch.GetTimestamp();
        void Complete(string state, Exception? error = null) => store.Status(key, state, context.Response.StatusCode,
            errorType: error?.GetType().Name, durationMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            responseContentType: context.Response.ContentType);
        var originalResponse = context.Response.Body;
        try
        {
            // Read a bounded prefix before SDK validation, even if the SDK rejects without consuming the body.
            context.Request.EnableBuffering();
            var bytes = new byte[ExchangeStore.MaxBodyBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), context.RequestAborted);
                if (read == 0) break;
                count += read;
            }
            context.Request.Body.Position = 0;
            store.Body(key, false, Encoding.UTF8.GetString(bytes, 0, Math.Min(count, ExchangeStore.MaxBodyBytes)),
                count > ExchangeStore.MaxBodyBytes, false);

            var capture = new ExchangeCaptureHandler.Capture((body, truncated) =>
            {
                store.Status(key, "receiving", context.Response.StatusCode,
                    responseContentType: context.Response.ContentType);
                store.Body(key, true, body, truncated, context.Response.ContentType?.StartsWith(
                    "text/event-stream", StringComparison.OrdinalIgnoreCase) == true);
            }, _ => { }, _ => { });
            using var tee = new ExchangeCaptureHandler.CaptureStream(originalResponse, capture, leaveOpen: true);
            context.Response.Body = tee;
            await next(context);
            await tee.FlushAsync(context.RequestAborted);
            Complete("complete");
        }
        catch (Exception exception)
        {
            Complete(exception is OperationCanceledException ? "canceled" : "request failed", exception);
            throw;
        }
        finally
        {
            context.Response.Body = originalResponse;
        }
    }
}

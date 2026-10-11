using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using A2A;
using BuyerAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace BuyerAgent.Tests;

public sealed class OtlpIntegrationTests
{
    [Fact]
    [Trait("Category", "CrossLanguage")]
    public async Task RealBuyerSdkHttpAndRustSdkExportTheSameTraceAndParentIds()
    {
        var root = Path.GetDirectoryName(new BuyerOptions().GetCatalogUri().LocalPath)!;
        var fixture = Path.Combine(root, "SupplierAgent", "target", "debug", "examples", "trace_fixture.exe");
        Assert.True(File.Exists(fixture), "Build cargo example trace_fixture before running CrossLanguage tests.");
        var captured = new ConcurrentBag<WireSpan>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = builder.Build();
        collector.MapPost("/v1/traces", async (HttpContext context) =>
        {
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes);
            foreach (var span in Decode(bytes.ToArray())) captured.Add(span);
            context.Response.ContentType = "application/x-protobuf";
        });
        await collector.StartAsync();
        var endpoint = collector.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("agentcon-buyer-test"))
            .AddSource(BuyerTelemetry.SourceName)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter(options =>
            {
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = new Uri(endpoint + "/v1/traces");
            }).Build();
        var start = new ProcessStartInfo(fixture)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = root
        };
        start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint;
        start.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        start.Environment["OTEL_SERVICE_NAME"] = "agentcon-supplier-test";
        start.Environment.Remove("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT");
        start.Environment.Remove("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL");
        start.Environment.Remove("OTEL_EXPORTER_OTLP_HEADERS");
        start.Environment.Remove("OTEL_EXPORTER_OTLP_TRACES_HEADERS");
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            string? line;
            string? origin = null;
            while ((line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))) is not null)
            {
                if (line.StartsWith("TRACE_FIXTURE_URL="))
                {
                    origin = line["TRACE_FIXTURE_URL=".Length..];
                    break;
                }
            }
            Assert.NotNull(origin);
            var drain = process.StandardOutput.ReadToEndAsync();
            using var http = new HttpClient();
            var options = new BuyerOptions();
            var gateway = new SupplierGateway(http, options, new AiCatalogClient(http, options));
            var binding = new SupplierBinding("urn:test:supplier", null, "SupplierAgent",
                new Uri(origin + "/.well-known/agent-card.json"), new Uri(origin + "/a2a"), "test");
            ActivityTraceId traceId;
            using (var agent = BuyerTelemetry.Agent("integration", "integration"))
            {
                traceId = agent!.TraceId;
                var response = await gateway.SendAsync(binding, new Message
                {
                    MessageId = Guid.NewGuid().ToString("N"), Role = Role.User,
                    Parts = [Part.FromData(System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        kind = "rfq", schemaVersion = "1", demoOnly = true,
                        wine = "Barolo", vintage = 2019, cases = 12, deliveryCity = "San Jose"
                    }))]
                }, CancellationToken.None);
                Assert.NotNull(response.Task);
                var completed = false;
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var task = await gateway.GetTaskAsync(binding, response.Task.Id, CancellationToken.None);
                    if (task.Status.State == TaskState.Completed) { completed = true; break; }
                    Assert.NotEqual(TaskState.Failed, task.Status.State);
                    await Task.Delay(50);
                }
                Assert.True(completed, "Supplier SDK background task did not complete");
            }
            Assert.True(provider.ForceFlush(10000));
            await process.StandardInput.WriteLineAsync("stop");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await errors);
            await drain;
            var spans = captured.Where(s => s.TraceId == traceId.ToHexString()).ToArray();
            var supplier = Assert.Single(spans, s => s.Name == "invoke_agent SupplierAgent" && s.Kind == 1);
            var inbound = Assert.Single(spans, s => s.SpanId == supplier.ParentId);
            Assert.Equal(2, inbound.Kind); // SERVER
            var outbound = Assert.Single(spans, s => s.SpanId == inbound.ParentId);
            Assert.Equal(3, outbound.Kind); // CLIENT
            var remoteAgent = Assert.Single(spans, s => s.SpanId == outbound.ParentId);
            Assert.Equal("invoke_agent SupplierAgent", remoteAgent.Name);
            Assert.Equal(3, remoteAgent.Kind);
            Assert.Contains(spans, s => s.Name == "supplier.business");
            Assert.DoesNotContain(spans, s => s.Name.StartsWith("chat"));
        }
        finally
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("stop");
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); }
            }
            await collector.StopAsync();
        }
    }

    private sealed record WireSpan(string TraceId, string SpanId, string ParentId, string Name, int Kind);

    // Only the stable OTLP trace envelope fields needed by this assertion are decoded.
    private static IEnumerable<WireSpan> Decode(byte[] request)
    {
        foreach (var resource in Fields(request).Where(f => f.Number == 1))
        foreach (var scope in Fields(resource.Bytes).Where(f => f.Number == 2))
        foreach (var span in Fields(scope.Bytes).Where(f => f.Number == 2))
        {
            var fields = Fields(span.Bytes).ToArray();
            byte[] Value(int number) => fields.FirstOrDefault(f => f.Number == number).Bytes ?? [];
            yield return new WireSpan(Convert.ToHexString(Value(1)).ToLowerInvariant(),
                Convert.ToHexString(Value(2)).ToLowerInvariant(), Convert.ToHexString(Value(4)).ToLowerInvariant(),
                Encoding.UTF8.GetString(Value(5)), (int)fields.FirstOrDefault(f => f.Number == 6).Integer);
        }
    }

    private static IEnumerable<(int Number, byte[] Bytes, ulong Integer)> Fields(byte[] data)
    {
        var position = 0;
        ulong Read()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = data[position++];
                value |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid protobuf varint");
        }
        while (position < data.Length)
        {
            var tag = Read();
            var number = (int)(tag >> 3);
            switch (tag & 7)
            {
                case 0: yield return (number, [], Read()); break;
                case 1: position += 8; break;
                case 2:
                    var length = checked((int)Read());
                    var value = data[position..(position + length)];
                    position += length;
                    yield return (number, value, 0);
                    break;
                case 5: position += 4; break;
                default: throw new InvalidDataException("Invalid protobuf wire type");
            }
        }
    }
}

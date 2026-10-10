using A2A;
using BuyerAgent;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BuyerAgent.Tests;

/// <summary>Deterministic HTTP for catalogs, Agent Cards and supplier JSON-RPC across several origins.</summary>
internal sealed class FakeNetwork : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _documents = new(StringComparer.Ordinal);
    public List<(Uri Uri, string? Method, Message? Message)> Requests { get; } = [];
    public string? SharedTaskId { get; set; }

    public FakeNetwork Document(string url, object value) => Route(url, () => Json(value));

    public FakeNetwork Raw(string url, string body) => Route(url, () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    });

    public FakeNetwork Route(string url, Func<HttpResponseMessage> response)
    {
        _documents[url] = response;
        return this;
    }

    public FakeNetwork Supplier(int port, string name)
    {
        var card = BuyerCard.Create(new BuyerOptions { ListenUrl = $"http://localhost:{port}" });
        card.Name = name;
        return Document($"http://localhost:{port}/.well-known/agent-card.json", card);
    }

    public IEnumerable<Uri> RpcCalls(string method) =>
        Requests.Where(request => request.Method == method).Select(request => request.Uri);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (request.Method == HttpMethod.Get)
        {
            Requests.Add((uri, null, null));
            return _documents.TryGetValue(uri.AbsoluteUri, out var document)
                ? document()
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        Assert.Equal("1.0", Assert.Single(request.Headers.GetValues("A2A-Version")));
        using var rpc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var method = rpc.RootElement.GetProperty("method").GetString();
        var id = rpc.RootElement.GetProperty("id").Clone();
        if (method == "SendMessage")
        {
            var send = rpc.RootElement.GetProperty("params").Deserialize<SendMessageRequest>(A2AJsonUtilities.DefaultOptions)!;
            Requests.Add((uri, method, send.Message));
            Assert.True(send.Configuration!.ReturnImmediately);
            return Json(new
            {
                jsonrpc = "2.0",
                id,
                result = new SendMessageResponse { Task = TaskResult(send.Message.TaskId ?? SharedTaskId ?? $"task-{uri.Port}", uri.Port) }
            });
        }
        Assert.Equal("GetTask", method);
        Requests.Add((uri, method, null));
        return Json(new
        {
            jsonrpc = "2.0",
            id,
            result = TaskResult(rpc.RootElement.GetProperty("params").GetProperty("id").GetString()!, uri.Port)
        });
    }

    private static AgentTask TaskResult(string id, int port) => new()
    {
        Id = id,
        ContextId = $"context-{port}",
        Status = new A2A.TaskStatus { State = TaskState.Completed },
        Artifacts = []
    };

    public static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, A2AJsonUtilities.DefaultOptions), Encoding.UTF8, "application/json")
    };
}

/// <summary>A temporary directory of catalog files, deleted on dispose.</summary>
internal sealed class TempCatalogs : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "buyer-catalog-tests-" + Guid.NewGuid().ToString("N"));

    public TempCatalogs() => System.IO.Directory.CreateDirectory(Directory);

    public string Write(string name, string json)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllText(path, json);
        return path;
    }

    public string Write(string name, params object[] entries) => Write(name, Catalog(entries));

    public static string Catalog(params object[] entries) => JsonSerializer.Serialize(new
    {
        specVersion = "1.0",
        host = new { displayName = "Test catalog", identifier = "urn:test:catalog" },
        entries
    });

    public static object Agent(string identifier, string url, string? displayName = null, string? description = null, string[]? tags = null, string? version = null) =>
        new Dictionary<string, object?>
        {
            ["identifier"] = identifier,
            ["type"] = AiCatalogClient.AgentCardMediaType,
            ["url"] = url,
            ["displayName"] = displayName,
            ["description"] = description,
            ["tags"] = tags ?? [],
            ["version"] = version
        }.Where(pair => pair.Value is not null).ToDictionary();

    public static object Nested(string identifier, string url) =>
        new { identifier, type = AiCatalogClient.CatalogMediaType, url };

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

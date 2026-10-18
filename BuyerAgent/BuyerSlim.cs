using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using A2A;
using Agntcy.Slim;
using Agntcy.Slim.SlimRpc;
using Google.Protobuf;
using SlimA2A;
using Native = uniffi.slim_bindings;

namespace BuyerAgent;

/// <summary>
/// A2A over SLIMRPC. Connects once to the SLIM node named by Buyer:SlimConfigPath, serves the buyer's own A2A handler
/// at slim://&lt;SlimName&gt;, and opens SLIMRPC clients to suppliers whose Agent Cards advertise SLIMRPC. The buyer
/// only dials out to the node. Calls are recorded in the exchange dashboard in the same JSON-RPC/SSE shape as HTTP.
/// </summary>
public sealed partial class BuyerSlim(BuyerOptions options, ExchangeStore exchanges, IServiceProvider services,
    ILogger<BuyerSlim> logger) : IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<SlimA2AClient>> _clients = new(StringComparer.Ordinal);
    private Task? _startup;
    private SlimA2AConnection? _connection;
    private SlimA2AServer? _server;
    // Multicast needs a raw SLIM app and connection id, which SlimA2AConnection does not expose.
    private Native.App? _groupApp;
    private ulong _groupConnection;

    public bool Configured => options.SlimConfigPath is not null;

    /// <summary>slim://&lt;SlimName&gt; once the buyer is serving on SLIM; otherwise null.</summary>
    public string? ServingUrl => _server is null ? null : $"slim://{options.SlimName}";

    [GeneratedRegex("^[A-Za-z0-9_-]+/[A-Za-z0-9_-]+/[A-Za-z0-9_-]+$")]
    private static partial Regex NamePattern();

    public static void ValidateName(string name, string purpose)
    {
        if (!NamePattern().IsMatch(name))
            throw new InvalidOperationException($"{purpose} '{name}' must be a SLIM name org/namespace/app.");
    }

    /// <summary>The SLIM name in a slim:// (or slimrpc://, or bare) Agent Card interface URL.</summary>
    public static string? TargetName(string url)
    {
        var name = url.StartsWith("slim://", StringComparison.Ordinal) ? url[7..]
            : url.StartsWith("slimrpc://", StringComparison.Ordinal) ? url[10..] : url;
        return NamePattern().IsMatch(name) ? name : null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Connect in the background: an unreachable node must not delay HTTP startup; suppliers fall back to HTTP.
        if (Configured) _startup = Task.Run(ConnectAndServeAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ConnectAndServeAsync()
    {
        try
        {
            var client = ReadClientConfig(options.SlimConfigPath!);
            var endpoint = client["endpoint"]?.GetValue<string>()
                ?? throw new InvalidOperationException("SLIM config client.endpoint is missing.");
            var json = client.ToJsonString();
            var connection = await SlimA2AConnection.ConnectAsync(new SlimA2AConnectionOptions
            {
                Endpoint = endpoint,
                // The file's client section (TLS, node auth such as a slimctl token) replaces the endpoint-only default.
                ConfigureClient = _ => Slim.NewClientConfigFromJson(json),
            });
            _server = await connection.StartServerAsync(new SlimA2AServerOptions
            {
                Identity = options.SlimName,
                SharedSecret = Identity is null ? options.SlimSharedSecret : null,
                IdentityProvider = Identity?.Provider,
                IdentityVerifier = Identity?.Verifier,
                ResolveExtendedAgentCard = (_, _) => Task.FromResult(BuyerCard.Create(options.WithMode(options.CommunicationMode), ServingUrl)),
            }, services.GetRequiredService<IA2ARequestHandler>());
            Volatile.Write(ref _connection, connection);
            EnableMulticast(connection);
            logger.LogInformation("Buyer SLIMRPC ready at slim://{Name} via {Endpoint}", options.SlimName, endpoint);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Buyer SLIMRPC unavailable; suppliers are called over HTTP");
        }
    }

    /// <summary>
    /// Group channels need a raw SLIM app on the existing node connection. The bindings allow one connection per
    /// endpoint in the global service, and SlimA2AConnection (0.3.0) keeps its connection id private, so read it by
    /// reflection. Remove once slim-a2a-dotnet exposes the id or a group client. Failure only disables multicast.
    /// </summary>
    private void EnableMulticast(SlimA2AConnection connection)
    {
        try
        {
            var field = typeof(SlimA2AConnection).GetField("_connectionId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(connection) is not ulong connectionId)
            {
                logger.LogWarning("SLIM multicast disabled: this SlimA2A version does not expose its connection id");
                return;
            }
            var groupName = Native.Name.FromString($"{options.SlimName}-group");
            var service = Native.SlimBindingsMethods.GetGlobalService();
            var groupApp = Identity is { } identity
                ? service.CreateApp(groupName, identity.Provider, identity.Verifier)
                : service.CreateAppWithSecret(groupName, options.SlimSharedSecret!);
            groupApp.Subscribe(groupName, connectionId);
            _groupConnection = connectionId;
            _groupApp = groupApp;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SLIM multicast disabled; point-to-point SLIMRPC continues");
        }
    }

    /// <summary>The app identity from the SLIM config file, or null to use Buyer:SlimSharedSecret.</summary>
    private SlimIdentity? Identity => _identity ??= options.SlimConfigPath is { } path ? ReadIdentity(path) : null;
    private SlimIdentity? _identity;

    /// <summary>How the buyer's SLIM apps prove their identity and which peers they accept.</summary>
    internal sealed record SlimIdentity(Native.IdentityProviderConfig Provider, Native.IdentityVerifierConfig Verifier);

    /// <summary>
    /// Reads <c>app.identity_provider</c> / <c>app.identity_verifier</c> (the schema the a2a CLI plugin and the
    /// suppliers use) from the SLIM config file: <c>jwt</c> (key files, e.g. an Ed25519 did:key signer and a JWKS of
    /// trusted agents) or <c>shared_secret</c>. Null when the file has no app identity.
    /// </summary>
    internal static SlimIdentity? ReadIdentity(string path)
    {
        var app = JsonNode.Parse(File.ReadAllText(path))?["app"];
        if (app?["identity_provider"] is not JsonObject provider || app["identity_verifier"] is not JsonObject verifier)
            return null;
        return new(Type(provider) switch
        {
            "jwt" => new Native.IdentityProviderConfig.Jwt(new Native.ClientJwtAuth(Key(provider, "encoding"),
                Audience(provider), Claim(provider, "issuer"), Claim(provider, "subject"), Duration(provider))),
            "shared_secret" => new Native.IdentityProviderConfig.SharedSecret(Text(provider, "id"), Text(provider, "data")),
            var other => throw new InvalidOperationException($"Unsupported SLIM identity_provider type '{other}'.")
        }, Type(verifier) switch
        {
            "jwt" => new Native.IdentityVerifierConfig.Jwt(new Native.JwtAuth(Key(verifier, "decoding"),
                Audience(verifier), Claim(verifier, "issuer"), Claim(verifier, "subject"), Duration(verifier))),
            "shared_secret" => new Native.IdentityVerifierConfig.SharedSecret(Text(verifier, "id"), Text(verifier, "data")),
            var other => throw new InvalidOperationException($"Unsupported SLIM identity_verifier type '{other}'.")
        });

        static string Type(JsonObject config) => config["type"]?.GetValue<string>() ?? "";
        static string Text(JsonObject config, string key) => config[key]?.GetValue<string>()
            ?? throw new InvalidOperationException($"SLIM identity is missing '{key}'.");
        static string? Claim(JsonObject config, string key) => config["claims"]?[key]?.GetValue<string>();
        static string[] Audience(JsonObject config) => config["claims"]?["audience"]?.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray() ?? [];
        static TimeSpan Duration(JsonObject config)
        {
            var text = config["duration"]?.GetValue<string>() ?? "1h";
            var value = double.Parse(text[..^1], System.Globalization.CultureInfo.InvariantCulture);
            return text[^1] switch
            {
                'h' => TimeSpan.FromHours(value), 'm' => TimeSpan.FromMinutes(value), 's' => TimeSpan.FromSeconds(value),
                _ => throw new InvalidOperationException($"SLIM identity duration '{text}' must end in h, m or s.")
            };
        }
        static Native.JwtKeyType Key(JsonObject config, string expected)
        {
            var key = config["key"] as JsonObject ?? throw new InvalidOperationException("SLIM jwt identity is missing 'key'.");
            if (key["type"]?.GetValue<string>() != expected)
                throw new InvalidOperationException($"SLIM jwt identity key must be of type '{expected}'.");
            var algorithm = key["algorithm"]?.GetValue<string>() switch
            {
                "EdDSA" => Native.JwtAlgorithm.EdDsa, "ES256" => Native.JwtAlgorithm.Es256, "ES384" => Native.JwtAlgorithm.Es384,
                "RS256" => Native.JwtAlgorithm.Rs256, var other => throw new InvalidOperationException($"Unsupported JWT algorithm '{other}'.")
            };
            var format = key["format"]?.GetValue<string>() switch
            {
                "pem" => Native.JwtKeyFormat.Pem, "jwk" => Native.JwtKeyFormat.Jwk, "jwks" => Native.JwtKeyFormat.Jwks,
                var other => throw new InvalidOperationException($"Unsupported JWT key format '{other}'.")
            };
            var file = key["key"]?["file"]?.GetValue<string>()
                ?? throw new InvalidOperationException("SLIM jwt identity key must name a file: {\"file\": \"...\"}.");
            var keyConfig = new Native.JwtKeyConfig(algorithm, format, new Native.JwtKeyData.File(file));
            return expected == "encoding" ? new Native.JwtKeyType.Encoding(keyConfig) : new Native.JwtKeyType.Decoding(keyConfig);
        }
    }

    internal static JsonNode ReadClientConfig(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException("SLIM config file is empty.");
        return root["client"]?.DeepClone() ?? throw new InvalidOperationException("SLIM config file has no client section.");
    }

    /// <summary>How long a stream may stay silent before its session is treated as dead (agents answer at once).</summary>
    internal static readonly TimeSpan FirstEventTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Longest one-shot SLIM call (quotes take seconds; streams have no such limit, purchases wait for wallets).</summary>
    internal static readonly TimeSpan UnaryCallTimeout = TimeSpan.FromSeconds(45);
    private readonly ConcurrentDictionary<string, int> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// A SLIMRPC client to <paramref name="target"/> when connected, recorded in the dashboard; otherwise null. It opens
    /// a new session after a failure, so a restarted supplier is reached again (see <see cref="ReconnectingSlimClient"/>).
    /// </summary>
    public IA2AClient? ClientFor(string target)
    {
        if (Volatile.Read(ref _connection) is null || TargetName(target) is not { } remote) return null;
        IA2AClient client = new ReconnectingSlimClient(remote, () => CurrentClient(remote),
            (stale, reason) => ResetClientAsync(remote, stale, reason), FirstEventTimeout, UnaryCallTimeout,
            () => Track(remote), logger);
        return exchanges.Enabled ? new CapturingClient(client, exchanges, new Uri($"slim://{remote}")) : client;
    }

    private SlimA2AClient CurrentClient(string remote)
    {
        var connection = Volatile.Read(ref _connection) ?? throw new InvalidOperationException("SLIM is not connected.");
        return _clients.GetOrAdd(remote, name => new Lazy<SlimA2AClient>(() => connection.CreateClient(new SlimA2AClientOptions
        {
            // One SLIM app per supplier; the server identity stays free for inbound calls.
            Identity = $"{options.SlimName}-to-{name.Split('/')[^1]}",
            SharedSecret = Identity is null ? options.SlimSharedSecret : null,
            IdentityProvider = Identity?.Provider,
            IdentityVerifier = Identity?.Verifier,
            Remote = name,
        }))).Value;
    }

    private IDisposable Track(string remote)
    {
        _inFlight.AddOrUpdate(remote, 1, (_, count) => count + 1);
        return new Release(() => _inFlight.AddOrUpdate(remote, 0, (_, count) => Math.Max(0, count - 1)));
    }

    private sealed class Release(Action action) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) action(); }
    }

    /// <summary>
    /// Drops every per-supplier session with no call in flight, so the next calls start new ones. Used when a failure
    /// points at the buyer's own connection to the node (it reconnects on its own, but sessions opened on the old
    /// connection stay dead); a session with a call in flight is left to fail and reset itself.
    /// </summary>
    public async Task ResetIdleClientsAsync(Exception reason)
    {
        foreach (var (remote, entry) in _clients.ToArray())
            if (entry.IsValueCreated && _inFlight.GetValueOrDefault(remote) == 0)
                await ResetClientAsync(remote, entry.Value, reason).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops <paramref name="stale"/> if it is still the client for <paramref name="remote"/>, and disposes it before
    /// the next call creates a replacement under the same SLIM app name.
    /// </summary>
    private async Task ResetClientAsync(string remote, IA2AClient stale, Exception reason)
    {
        if (!_clients.TryGetValue(remote, out var entry) || !entry.IsValueCreated || !ReferenceEquals(entry.Value, stale) ||
            !_clients.TryRemove(new KeyValuePair<string, Lazy<SlimA2AClient>>(remote, entry)))
            return;
        logger.LogWarning("SLIM call to {Remote} failed ({ErrorType}); the next call opens a new session",
            remote, reason.GetType().Name);
        try
        {
            await entry.Value.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Closing the previous SLIM client to {Remote} failed", remote);
        }
    }

    /// <summary>True when every target can be reached in one SLIM multicast.</summary>
    public bool CanMulticast(IEnumerable<string> targets) =>
        Volatile.Read(ref _connection) is not null && _groupApp is not null && targets.All(target => TargetName(target) is not null);

    private static readonly TimeSpan MulticastCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonParser ProtoJson = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// One A2A SendMessage to every target over a SLIM group channel; yields each member's reply as it arrives,
    /// attributed to the target it came from. A2A 1.0 JSON is the protobuf JSON mapping, so SDK and proto types
    /// convert through JSON. Members that do not answer within <paramref name="timeout"/> are recorded as such.
    /// </summary>
    public async IAsyncEnumerable<(string Target, SendMessageResponse Response)> MulticastAsync(
        IReadOnlyList<string> targets, SendMessageRequest request, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var groupApp = _groupApp ?? throw new InvalidOperationException("SLIM multicast is not connected.");
        var names = targets.Select(target => TargetName(target)
            ?? throw new ArgumentException($"'{target}' is not a SLIM name.")).ToArray();
        var requestJson = JsonSerializer.Serialize(request, A2AJsonUtilities.DefaultOptions);
        var proto = ProtoJson.Parse<Lf.A2a.V1.SendMessageRequest>(requestJson);
        // One dashboard exchange per member, all carrying the same single request.
        var keys = names.ToDictionary(name => name, name => exchanges.Enabled ? Record(name, requestJson) : default);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        var members = names.Select(Native.Name.FromString).ToArray();
        using var channel = uniffi.slim_rpc.Channel.NewGroupWithConnection(groupApp, members, _groupConnection);
        try
        {
            var reader = await channel.CallMulticastUnaryAsync("lf.a2a.v1.A2AService", "SendMessage",
                proto.ToByteArray(), timeout, null).ConfigureAwait(false);
            await foreach (var item in SlimRpcStreams.ReadMulticastStreamAsync<Lf.A2a.V1.SendMessageResponse>(reader, cancellationToken))
            {
                // The source is the member's SLIM name, possibly with an instance suffix.
                var target = names.FirstOrDefault(name => item.Context == name || item.Context.StartsWith(name + "/", StringComparison.Ordinal));
                if (target is null || !answered.Add(target)) continue;
                var responseJson = JsonFormatter.Default.Format(item.Value);
                var response = JsonSerializer.Deserialize<SendMessageResponse>(responseJson, A2AJsonUtilities.DefaultOptions)
                    ?? throw new InvalidOperationException("Empty multicast reply.");
                if (exchanges.Enabled)
                {
                    var key = keys[target];
                    exchanges.Status(key, "receiving", 200);
                    var (body, truncated) = CapturingClient.Bound(new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = "multicast",
                        ["result"] = JsonNode.Parse(JsonSerializer.Serialize(response, A2AJsonUtilities.DefaultOptions))
                    }.ToJsonString());
                    exchanges.Body(key, true, body, truncated, false);
                    exchanges.Status(key, "complete", durationMs: clock.Elapsed.TotalMilliseconds);
                }
                yield return (target, response);
            }
        }
        finally
        {
            foreach (var name in names.Where(name => !answered.Contains(name) && exchanges.Enabled))
                exchanges.Status(keys[name], "no multicast reply", errorType: "Timeout", durationMs: clock.Elapsed.TotalMilliseconds);
            // Close the group session: disposing the channel alone leaves it, and its traffic, open on every member.
            try
            {
                await channel.CloseAsync(MulticastCloseTimeout).ConfigureAwait(false);
            }
            catch (uniffi.slim_rpc.RpcException exception)
            {
                logger.LogDebug(exception, "Closing the SLIM multicast session failed");
            }
        }
    }

    private (long, long) Record(string name, string requestJson)
    {
        var key = exchanges.Begin(new Uri($"slim://{name}"));
        var (body, truncated) = CapturingClient.Bound(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = "multicast", ["method"] = "SendMessage",
            ["params"] = JsonNode.Parse(requestJson)
        }.ToJsonString());
        exchanges.Body(key, false, body, truncated, false);
        return key;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        if (_startup is { } startup) await startup.ConfigureAwait(false);
        Interlocked.Exchange(ref _groupApp, null)?.Dispose();
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
            await connection.DisposeAsync().ConfigureAwait(false); // also disposes the server and clients
    }

    /// <summary>
    /// Records SLIMRPC calls as JSON-RPC request/response bodies (streams as SSE frames), so the dashboard's task,
    /// LLM-call and conversation views work unchanged. The wire is protobuf over SLIM; this is a faithful rendering.
    /// </summary>
    private sealed class CapturingClient(IA2AClient inner, ExchangeStore store, Uri endpoint) : IA2AClient
    {
        private static readonly JsonSerializerOptions Json = A2AJsonUtilities.DefaultOptions;

        internal static (string Body, bool Truncated) Bound(string body) =>
            Encoding.UTF8.GetByteCount(body) <= ExchangeStore.MaxBodyBytes
                ? (body, false)
                : (Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(body), 0, ExchangeStore.MaxBodyBytes), true);

        private (long, long) Begin(string method, string id, object request)
        {
            var key = store.Begin(endpoint);
            var (body, truncated) = Bound(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method,
                ["params"] = JsonSerializer.SerializeToNode(request, request.GetType(), Json)
            }.ToJsonString());
            store.Body(key, false, body, truncated, false);
            return key;
        }

        private static string Result(string id, object result, Type type) => new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = JsonSerializer.SerializeToNode(result, type, Json)
        }.ToJsonString();

        private async Task<T> UnaryAsync<T>(string method, object request, Func<Task<T>> call)
        {
            var id = Guid.NewGuid().ToString();
            var key = Begin(method, id, request);
            var clock = Stopwatch.StartNew();
            try
            {
                var response = await call().ConfigureAwait(false);
                store.Status(key, "receiving", 200);
                var (body, truncated) = Bound(Result(id, response!, typeof(T)));
                store.Body(key, true, body, truncated, false);
                store.Status(key, "complete", durationMs: clock.Elapsed.TotalMilliseconds);
                return response;
            }
            catch (Exception exception)
            {
                store.Status(key, exception is OperationCanceledException ? "canceled" : "transport failed",
                    errorType: exception.GetType().Name, durationMs: clock.Elapsed.TotalMilliseconds);
                throw;
            }
        }

        private async IAsyncEnumerable<StreamResponse> StreamAsync(string method, object request,
            IAsyncEnumerable<StreamResponse> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid().ToString();
            var key = Begin(method, id, request);
            var clock = Stopwatch.StartNew();
            var frames = new StringBuilder();
            var count = 0;
            var finished = false;
            string? error = null;
            await using var reader = source.WithCancellation(cancellationToken).GetAsyncEnumerator();
            try
            {
                while (true)
                {
                    StreamResponse item;
                    try
                    {
                        if (!await reader.MoveNextAsync()) { finished = true; break; }
                        item = reader.Current;
                    }
                    catch (Exception exception)
                    {
                        error = exception is OperationCanceledException ? "canceled" : exception.GetType().Name;
                        throw;
                    }
                    if (count++ == 0) store.Status(key, "receiving", 200);
                    frames.Append("id: ").Append(count).Append("\ndata: ").Append(Result(id, item, typeof(StreamResponse))).Append("\n\n");
                    var (body, truncated) = Bound(frames.ToString());
                    store.Body(key, true, body, truncated, true);
                    yield return item;
                }
            }
            finally
            {
                var status = finished ? "complete" : error == "canceled" ? "canceled"
                    : error is not null ? "response failed" : "response not fully consumed";
                store.Status(key, status, errorType: error is null or "canceled" ? null : error,
                    durationMs: clock.Elapsed.TotalMilliseconds);
            }
        }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
            UnaryAsync("SendMessage", request, () => inner.SendMessageAsync(request, cancellationToken));

        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, CancellationToken cancellationToken = default) =>
            StreamAsync("SendStreamingMessage", request, inner.SendStreamingMessageAsync(request, cancellationToken), cancellationToken);

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, CancellationToken cancellationToken = default) =>
            UnaryAsync("GetTask", request, () => inner.GetTaskAsync(request, cancellationToken));

        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, CancellationToken cancellationToken = default) =>
            UnaryAsync("ListTasks", request, () => inner.ListTasksAsync(request, cancellationToken));

        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, CancellationToken cancellationToken = default) =>
            UnaryAsync("CancelTask", request, () => inner.CancelTaskAsync(request, cancellationToken));

        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, CancellationToken cancellationToken = default) =>
            StreamAsync("SubscribeToTask", request, inner.SubscribeToTaskAsync(request, cancellationToken), cancellationToken);

        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(
            TaskPushNotificationConfig config, CancellationToken cancellationToken = default) =>
            inner.CreateTaskPushNotificationConfigAsync(config, cancellationToken);

        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(
            GetTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
            inner.GetTaskPushNotificationConfigAsync(request, cancellationToken);

        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(
            ListTaskPushNotificationConfigsRequest request, CancellationToken cancellationToken = default) =>
            inner.ListTaskPushNotificationConfigsAsync(request, cancellationToken);

        public Task DeleteTaskPushNotificationConfigAsync(
            DeleteTaskPushNotificationConfigRequest request, CancellationToken cancellationToken = default) =>
            inner.DeleteTaskPushNotificationConfigAsync(request, cancellationToken);

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, CancellationToken cancellationToken = default) =>
            inner.GetExtendedAgentCardAsync(request, cancellationToken);
    }
}

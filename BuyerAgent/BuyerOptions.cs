using System.Reflection;

namespace BuyerAgent;

public enum SupplierCommunicationMode { NaturalLanguage, LearnStructures, Native, Automatic }

public sealed class BuyerOptions
{
    public const string SectionName = "Buyer";
    public const string RepositoryCatalogMetadataKey = "AgentConRepositoryCatalog";
    public string ListenUrl { get; set; } = "http://localhost:5100";
    public string Model { get; set; } = "auto";
    /// <summary>AI Catalog location: absolute or relative local path, file URI, or HTTP(S) URL. Defaults to the repository catalog.json.</summary>
    public string? CatalogUrl { get; set; }
    public bool AllowInsecureSupplierHttp { get; set; }
    public bool ExchangeCaptureEnabled { get; set; }
    private int _communicationMode = (int)SupplierCommunicationMode.Automatic;
    private int _nativeStructuredSupport;
    public bool NativeStructuredSupport
    {
        get => Volatile.Read(ref _nativeStructuredSupport) != 0;
        set => Volatile.Write(ref _nativeStructuredSupport, value ? 1 : 0);
    }
    public string? SupplierControlToken { get; set; }
    /// <summary>
    /// SLIM node connection file (JSON or YAML-as-JSON) whose <c>client</c> section is a SLIM client config: endpoint,
    /// TLS and node authentication. The same file the a2a CLI SLIMRPC plugin and the suppliers read. Unset disables SLIMRPC.
    /// </summary>
    public string? SlimConfigPath { get; set; }
    /// <summary>The buyer's SLIM app name (org/namespace/app); callers reach the buyer at slim://&lt;SlimName&gt;.</summary>
    public string SlimName { get; set; } = "agntcy/agentcon/buyer";
    /// <summary>Shared secret all demo SLIM apps use for their identity; process-scoped, never in tracked configuration.</summary>
    public string? SlimSharedSecret { get; set; }
    public bool AllowLoopbackPresentationHttp { get; set; }
    public SupplierCommunicationMode CommunicationMode
    {
        get => (SupplierCommunicationMode)Volatile.Read(ref _communicationMode);
        set => Volatile.Write(ref _communicationMode, (int)value);
    }
    public BuyerOptions WithMode(SupplierCommunicationMode mode)
    {
        var snapshot = (BuyerOptions)MemberwiseClone();
        snapshot.CommunicationMode = mode;
        return snapshot;
    }
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int MaxConversations { get; set; } = 32;
    public decimal DemoMinBottlePriceUsd { get; set; } = 8;
    public decimal DemoMaxBottlePriceUsd { get; set; } = 30;
    public string CopilotStateDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentConDemo", "buyer-copilot");

    public static string? RepositoryCatalogPath => typeof(BuyerOptions).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == RepositoryCatalogMetadataKey)?.Value;

    public void Validate()
    {
        if (SupplierControlToken is { } token && (token.Length is < 32 or > 256 ||
            token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
            throw new InvalidOperationException("Supplier control token must contain 32-256 base64url characters.");
        if (!Enum.IsDefined(CommunicationMode))
            throw new InvalidOperationException("Buyer:CommunicationMode is invalid.");
        if (DemoMinBottlePriceUsd <= 0 || DemoMaxBottlePriceUsd < DemoMinBottlePriceUsd ||
            DemoMaxBottlePriceUsd > 10000)
            throw new InvalidOperationException("Configure a positive, ordered demo bottle-price range of at most USD 10000.");
        if (!Uri.TryCreate(ListenUrl, UriKind.Absolute, out var listen) ||
            listen.Scheme != Uri.UriSchemeHttp || !listen.IsLoopback ||
            listen.AbsolutePath != "/" || listen.Query.Length > 0 || listen.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("Buyer:ListenUrl must be an HTTP loopback origin, e.g. http://localhost:5100.");
        }
        if (string.IsNullOrWhiteSpace(Model) || RequestTimeoutSeconds is < 1 or > 600 ||
            MaxConversations is < 1 or > 128 || !Path.IsPathFullyQualified(CopilotStateDirectory))
        {
            throw new InvalidOperationException("Configure a model, a 1-600 second timeout, 1-128 conversations, and an absolute Copilot state directory.");
        }
        if (SlimConfigPath is not null)
        {
            if (!File.Exists(SlimConfigPath))
                throw new InvalidOperationException($"Buyer:SlimConfigPath '{SlimConfigPath}' does not exist.");
            // The config file's app identity (e.g. did:key) wins; otherwise a shared secret is required.
            if (BuyerSlim.ReadIdentity(SlimConfigPath) is null && SlimSharedSecret is not { Length: >= 32 })
                throw new InvalidOperationException("Set a process-scoped Buyer:SlimSharedSecret (at least 32 characters), or an app identity in Buyer:SlimConfigPath.");
            BuyerSlim.ValidateName(SlimName, "Buyer:SlimName");
        }
        GetCatalogUri();
    }

    /// <summary>Resolves and validates the configured catalog location without reading it.</summary>
    public Uri GetCatalogUri() => ParseCatalogLocation(CatalogUrl, Directory.GetCurrentDirectory());

    public Uri ParseCatalogLocation(string? value, string baseDirectory)
    {
        var location = string.IsNullOrWhiteSpace(value) ? RepositoryCatalogPath : value.Trim();
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new InvalidOperationException("Set Buyer:CatalogUrl to an AI Catalog path or URL.");
        }
        Uri uri;
        if (Path.IsPathFullyQualified(location))
        {
            uri = new Uri(Path.GetFullPath(location));
        }
        else if (Uri.TryCreate(location, UriKind.Absolute, out var absolute) && absolute.Scheme.Length > 1)
        {
            uri = absolute;
        }
        else if (!Path.IsPathRooted(location) && !location.Contains(':'))
        {
            uri = new Uri(Path.GetFullPath(Path.Combine(baseDirectory, location)));
        }
        else
        {
            throw new InvalidOperationException($"Buyer:CatalogUrl '{location}' is not a supported path or URL.");
        }
        if (uri.IsFile)
        {
            if (uri.IsUnc)
            {
                throw new InvalidOperationException("Buyer:CatalogUrl must be a local file path, not a network (UNC) path.");
            }
            return uri;
        }
        ValidateRemoteUri(uri, "Buyer:CatalogUrl");
        if (uri.AbsolutePath == "/" && uri.Query.Length == 0)
        {
            uri = new Uri(uri, "/.well-known/ai-catalog.json");
        }
        return uri;
    }

    public void ValidateRemoteUri(Uri uri, string purpose)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{purpose} URL must be an absolute HTTPS or HTTP URL without credentials or fragment.");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback && !AllowInsecureSupplierHttp)
        {
            throw new InvalidOperationException($"{purpose} URL must use HTTPS, or HTTP on loopback. Set Buyer:AllowInsecureSupplierHttp to opt into plaintext HTTP for a trusted LAN demo.");
        }
    }

    public bool IsSelf(Uri uri)
    {
        var listen = new Uri(ListenUrl, UriKind.Absolute);
        return uri.Scheme == listen.Scheme && uri.Port == listen.Port &&
            (string.Equals(uri.Host, listen.Host, StringComparison.OrdinalIgnoreCase) || (uri.IsLoopback && listen.IsLoopback));
    }
}

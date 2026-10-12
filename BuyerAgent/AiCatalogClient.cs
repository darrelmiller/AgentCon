using System.Net;
using System.Text.Json;

namespace BuyerAgent;

public sealed record CatalogAgentEntry(
    string Identifier,
    string? Version,
    string? DisplayName,
    string? Description,
    IReadOnlyList<string> Tags,
    Uri CardUrl,
    string Catalog);

public sealed record CatalogSnapshot(
    string RootCatalog,
    IReadOnlyList<CatalogAgentEntry> AgentEntries,
    int CatalogsRead,
    int ExcludedSelfEntries,
    IReadOnlyList<string> Warnings);

public sealed record CatalogSearchResult(
    string Catalog,
    string Query,
    IReadOnlyList<CatalogAgentEntry> Matches,
    int AgentEntriesScanned,
    int ExcludedSelfEntries,
    int CatalogsRead,
    IReadOnlyList<string> Warnings,
    string Note);

/// <summary>
/// Reads AI Catalog (specVersion 1.0) documents from a local file or HTTP(S) and lists A2A Agent Card entries.
/// Catalog content is untrusted data; it only yields candidate destinations that are validated before use.
/// </summary>
public sealed class AiCatalogClient(HttpClient httpClient, BuyerOptions options)
{
    public const string CatalogMediaType = "application/ai-catalog+json";
    public const string AgentCardMediaType = "application/a2a-agent-card+json";
    public const int MaxNestingDepth = 4;
    public const int MaxCatalogs = 16;
    public const int MaxEntries = 500;
    public const int MaxDocumentBytes = 1024 * 1024;
    public const int MaxResults = 25;

    public async Task<JsonElement> ReadRootAsync(CancellationToken cancellationToken)
    {
        using var document = await ReadCatalogAsync(options.GetCatalogUri(), true, [], cancellationToken)
            ?? throw new InvalidOperationException("Root AI Catalog is missing.");
        return document.RootElement.Clone();
    }

    public async Task<CatalogSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var root = options.GetCatalogUri();
        var entries = new List<CatalogAgentEntry>();
        var warnings = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedSelf = 0;
        var entryCount = 0;
        var pending = new Queue<(Uri Location, int Depth)>();
        pending.Enqueue((root, 0));
        visited.Add(root.AbsoluteUri);
        var catalogsRead = 0;
        while (pending.TryDequeue(out var next))
        {
            using var document = await ReadCatalogAsync(next.Location, next.Depth == 0, warnings, cancellationToken);
            if (document is null)
            {
                continue;
            }
            catalogsRead++;
            var catalog = Display(next.Location);
            foreach (var entry in ParseEntries(document.RootElement, catalog))
            {
                if (++entryCount > MaxEntries)
                {
                    warnings.Add($"Stopped after {MaxEntries} catalog entries; remaining entries were not read.");
                    pending.Clear();
                    break;
                }
                if (entry.Type == CatalogMediaType)
                {
                    if (entry.Url is null)
                    {
                        warnings.Add($"Skipped nested catalog '{entry.Identifier}' in {catalog}: inline nested catalogs are not supported.");
                        continue;
                    }
                    if (!TryResolve(next.Location, entry.Url, out var nested))
                    {
                        warnings.Add($"Skipped nested catalog '{entry.Identifier}' in {catalog}: invalid URL.");
                        continue;
                    }
                    if (!IsAllowedNestedCatalog(next.Location, nested, out var reason))
                    {
                        warnings.Add($"Skipped nested catalog '{entry.Identifier}' in {catalog}: {reason}.");
                        continue;
                    }
                    if (!visited.Add(nested.AbsoluteUri))
                    {
                        warnings.Add($"Skipped nested catalog '{entry.Identifier}' in {catalog}: {Display(nested)} was already read (circular or duplicate reference).");
                        continue;
                    }
                    if (next.Depth + 1 > MaxNestingDepth)
                    {
                        warnings.Add($"Skipped nested catalog '{entry.Identifier}' in {catalog}: maximum nesting depth {MaxNestingDepth} reached.");
                        continue;
                    }
                    if (visited.Count > MaxCatalogs)
                    {
                        warnings.Add($"Skipped nested catalog '{entry.Identifier}' in {catalog}: at most {MaxCatalogs} catalogs are read.");
                        continue;
                    }
                    pending.Enqueue((nested, next.Depth + 1));
                    continue;
                }
                if (entry.Type != AgentCardMediaType)
                {
                    continue;
                }
                if (entry.Url is null)
                {
                    warnings.Add($"Skipped A2A entry '{entry.Identifier}' in {catalog}: inline Agent Cards cannot be used as supplier destinations.");
                    continue;
                }
                if (!TryResolve(next.Location, entry.Url, out var cardUrl))
                {
                    warnings.Add($"Skipped A2A entry '{entry.Identifier}' in {catalog}: invalid Agent Card URL.");
                    continue;
                }
                try
                {
                    options.ValidateRemoteUri(cardUrl, "Agent Card");
                }
                catch (InvalidOperationException exception)
                {
                    warnings.Add($"Skipped A2A entry '{entry.Identifier}' in {catalog}: {exception.Message}");
                    continue;
                }
                if (options.IsSelf(cardUrl))
                {
                    excludedSelf++;
                    continue;
                }
                entries.Add(new CatalogAgentEntry(entry.Identifier, entry.Version, entry.DisplayName, entry.Description, entry.Tags, cardUrl, catalog));
            }
        }
        return new CatalogSnapshot(Display(root), entries, catalogsRead, excludedSelf, warnings);
    }

    public async Task<CatalogSearchResult> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        var snapshot = await LoadAsync(cancellationToken);
        var terms = Tokenize(query);
        var matches = snapshot.AgentEntries
            .Select(entry => (Entry: entry, Score: terms.Count == 0 ? 1 : Score(entry, terms)))
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Entry.Identifier, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Entry.Version, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(candidate => candidate.Entry)
            .ToList();
        var note = matches.Count switch
        {
            0 => "No A2A Agent Card entries in the catalog matched. Nothing was selected; do not invent a supplier.",
            1 => "One candidate matched. Selection requires select_supplier with its catalog identifier.",
            _ => "Multiple candidates matched. Do not pick one by order; ask the caller which catalog identifier to select."
        };
        return new CatalogSearchResult(snapshot.RootCatalog, query ?? "", matches, snapshot.AgentEntries.Count,
            snapshot.ExcludedSelfEntries, snapshot.CatalogsRead, snapshot.Warnings, note);
    }

    public async Task<CatalogAgentEntry> FindAsync(string identifier, string? version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Provide the supplier's exact catalog identifier.");
        }
        var snapshot = await LoadAsync(cancellationToken);
        var candidates = snapshot.AgentEntries
            .Where(entry => string.Equals(entry.Identifier, identifier, StringComparison.Ordinal) &&
                (version is null || string.Equals(entry.Version, version, StringComparison.Ordinal)))
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException(
                $"No A2A Agent Card entry with identifier '{identifier}'{(version is null ? "" : $" and version '{version}'")} exists in catalog {snapshot.RootCatalog}."),
            _ => throw new InvalidOperationException(
                $"Catalog identifier '{identifier}' is ambiguous ({candidates.Count} entries: versions " +
                string.Join(", ", candidates.Select(entry => entry.Version ?? "(none)")) + $" in {string.Join(", ", candidates.Select(entry => entry.Catalog).Distinct())}). Specify a version.")
        };
    }

    private async Task<JsonDocument?> ReadCatalogAsync(Uri location, bool isRoot, List<string> warnings, CancellationToken cancellationToken)
    {
        using var activity = BuyerTelemetry.Source.StartActivity("catalog.fetch");
        activity?.SetTag("agentcon.catalog.transport", location.IsFile ? "file" : "http");
        try
        {
            var bytes = location.IsFile
                ? await ReadFileAsync(location, cancellationToken)
                : await ReadHttpAsync(location, cancellationToken);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException($"AI Catalog {Display(location)} is not valid JSON: {exception.Message}");
            }
            try
            {
                ValidateCatalog(document.RootElement, Display(location));
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (!isRoot && exception is InvalidOperationException or HttpRequestException)
        {
            BuyerTelemetry.Error(activity, exception);
            warnings.Add($"Nested catalog {Display(location)} could not be read: {exception.Message}");
            return null;
        }
        catch (Exception exception)
        {
            BuyerTelemetry.Error(activity, exception);
            throw;
        }
    }

    private static async Task<byte[]> ReadFileAsync(Uri location, CancellationToken cancellationToken)
    {
        var path = location.LocalPath;
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new InvalidOperationException($"AI Catalog file not found: {path}");
        }
        if (file.Length > MaxDocumentBytes)
        {
            throw new InvalidOperationException($"AI Catalog file exceeds {MaxDocumentBytes} bytes: {path}");
        }
        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"AI Catalog file could not be read: {path}: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidOperationException($"AI Catalog file could not be read: {path}: {exception.Message}");
        }
    }

    private async Task<byte[]> ReadHttpAsync(Uri location, CancellationToken cancellationToken)
    {
        options.ValidateRemoteUri(location, "AI Catalog");
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        request.Headers.Accept.ParseAdd(CatalogMediaType);
        request.Headers.Accept.ParseAdd("application/json;q=0.9");
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"AI Catalog request to {location} failed: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"AI Catalog request to {location} timed out.");
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw new InvalidOperationException($"AI Catalog {location} returned redirect {status}; redirects are not followed. Configure the final catalog URL.");
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"AI Catalog {location} returned HTTP {status}.");
            }
            if (response.Content.Headers.ContentLength > MaxDocumentBytes)
            {
                throw new InvalidOperationException($"AI Catalog {location} exceeds {MaxDocumentBytes} bytes.");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxDocumentBytes)
                {
                    throw new InvalidOperationException($"AI Catalog {location} exceeds {MaxDocumentBytes} bytes.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }

    private static void ValidateCatalog(JsonElement root, string catalog)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Malformed(catalog, "the document must be a JSON object");
        }
        if (!root.TryGetProperty("specVersion", out var specVersion) || specVersion.ValueKind != JsonValueKind.String)
        {
            throw Malformed(catalog, "missing string 'specVersion'");
        }
        if (specVersion.GetString() != "1.0")
        {
            throw Malformed(catalog, $"unsupported specVersion '{specVersion.GetString()}'; expected '1.0'");
        }
        if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            throw Malformed(catalog, "missing 'entries' array");
        }
    }

    private static IEnumerable<RawEntry> ParseEntries(JsonElement root, string catalog)
    {
        var parsed = new List<RawEntry>();
        var keys = new HashSet<(string, string?)>();
        var index = 0;
        foreach (var entry in root.GetProperty("entries").EnumerateArray())
        {
            var where = $"entries[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(catalog, $"{where} must be an object");
            }
            var identifier = RequiredString(entry, "identifier", catalog, where);
            var type = RequiredString(entry, "type", catalog, where);
            var hasUrl = entry.TryGetProperty("url", out var url);
            var hasData = entry.TryGetProperty("data", out _);
            if (hasUrl == hasData)
            {
                throw Malformed(catalog, $"{where} must contain exactly one of 'url' or 'data'");
            }
            if (hasUrl && (url.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(url.GetString())))
            {
                throw Malformed(catalog, $"{where}.url must be a non-empty string");
            }
            var version = OptionalString(entry, "version", catalog, where);
            if (!keys.Add((identifier, version)))
            {
                throw Malformed(catalog, $"{where} duplicates identifier '{identifier}'{(version is null ? "" : $" version '{version}'")}");
            }
            var tags = new List<string>();
            if (entry.TryGetProperty("tags", out var tagArray))
            {
                if (tagArray.ValueKind != JsonValueKind.Array || tagArray.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String))
                {
                    throw Malformed(catalog, $"{where}.tags must be an array of strings");
                }
                tags.AddRange(tagArray.EnumerateArray().Select(tag => Truncate(tag.GetString()!, 64)).Take(32));
            }
            parsed.Add(new RawEntry(
                Truncate(identifier, 512), type, version is null ? null : Truncate(version, 64),
                hasUrl ? url.GetString() : null,
                TruncateOptional(OptionalString(entry, "displayName", catalog, where), 200),
                TruncateOptional(OptionalString(entry, "description", catalog, where), 1000),
                tags));
        }
        return parsed;
    }

    private static string RequiredString(JsonElement entry, string name, string catalog, string where) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw Malformed(catalog, $"{where}.{name} must be a non-empty string");

    private static string? OptionalString(JsonElement entry, string name, string catalog, string where)
    {
        if (!entry.TryGetProperty(name, out var value))
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw Malformed(catalog, $"{where}.{name} must be a string");
    }

    private static InvalidOperationException Malformed(string catalog, string problem) =>
        new($"AI Catalog {catalog} is malformed: {problem}.");

    private static bool TryResolve(Uri baseUri, string reference, out Uri resolved)
    {
        resolved = null!;
        if (!Uri.TryCreate(baseUri, reference, out var candidate) || !candidate.IsAbsoluteUri)
        {
            return false;
        }
        resolved = candidate;
        return true;
    }

    private bool IsAllowedNestedCatalog(Uri parent, Uri nested, out string reason)
    {
        if (nested.IsFile)
        {
            reason = parent.IsFile
                ? (nested.IsUnc ? "network (UNC) file paths are not allowed" : "")
                : "a remote catalog cannot reference local files";
            return parent.IsFile && !nested.IsUnc;
        }
        try
        {
            options.ValidateRemoteUri(nested, "Nested AI Catalog");
            reason = "";
            return true;
        }
        catch (InvalidOperationException exception)
        {
            reason = exception.Message.TrimEnd('.');
            return false;
        }
    }

    private static List<string> Tokenize(string? query) => string.IsNullOrWhiteSpace(query)
        ? []
        : query.Split([' ', '\t', '\r', '\n', ',', ';', '.', '/', '-', '_', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.ToLowerInvariant())
            .Where(term => term.Length > 1)
            .Distinct()
            .ToList();

    private static int Score(CatalogAgentEntry entry, IReadOnlyList<string> terms)
    {
        var text = string.Join(' ', new[] { entry.Identifier, entry.DisplayName, entry.Description }
            .Concat(entry.Tags).Where(value => value is not null)).ToLowerInvariant();
        return terms.Count(term => text.Contains(term, StringComparison.Ordinal));
    }

    private static string Display(Uri location) => location.IsFile ? location.LocalPath : location.AbsoluteUri;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static string? TruncateOptional(string? value, int length) => value is null ? null : Truncate(value, length);

    private sealed record RawEntry(
        string Identifier, string Type, string? Version, string? Url, string? DisplayName, string? Description, IReadOnlyList<string> Tags);
}

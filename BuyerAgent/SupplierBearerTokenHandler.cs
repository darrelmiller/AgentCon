using System.Net.Http.Headers;

namespace BuyerAgent;

/// <summary>
/// Adds the configured OIDC bearer token to supplier requests bound for an allowlisted HTTPS origin, the OIDC gateway in
/// front of the cloud sellers. The origin comes from configuration, not from Agent Cards, so a card that points elsewhere
/// never receives the token.
/// </summary>
public sealed class SupplierBearerTokenHandler(BuyerOptions options) : DelegatingHandler
{
    private readonly HashSet<string> _origins = options.SupplierBearerTokenOrigins
        .Select(origin => new Uri(origin).GetLeftPart(UriPartial.Authority))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (options.SupplierBearerTokenFile is { } file && request.Headers.Authorization is null &&
            request.RequestUri is { Scheme: "https" } uri && _origins.Contains(uri.GetLeftPart(UriPartial.Authority)))
        {
            var token = (await File.ReadAllTextAsync(file, cancellationToken)).Trim();
            if (token.Length == 0 || token.Any(character => character is < '!' or > '~'))
                throw new InvalidOperationException($"Buyer:SupplierBearerTokenFile '{file}' does not hold a bearer token.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await base.SendAsync(request, cancellationToken);
    }
}

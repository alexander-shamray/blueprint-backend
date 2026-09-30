using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Common.Infrastructure.Identity;

/// <summary>§11.5's client-credentials grant, cached once per host rather than per inbound call.</summary>
/// <remarks>Its client carries no <see cref="ClientCredentialsHandler"/>, which would recurse on every fetch.</remarks>
public sealed partial class CachingTokenClient(
    IHttpClientFactory clients,
    IOptions<ServiceIdentityOptions> identity,
    AuthorityKeyName authorityKey,
    TimeProvider clock,
    ILogger<CachingTokenClient> logger) : ITokenCache, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> this fetches over (§11.5).</summary>
    public const string HttpClientName = "identity";

    /// <summary>How long before real expiry a cached token stops being handed out.</summary>
    private static readonly TimeSpan ExpiryGuard = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Uri? _tokenEndpoint;

    public async Task<string> GetAsync(string scope, CancellationToken ct)
    {
        if (TryRead(scope, out string cached))
            return cached;

        // One gate across every scope: §11.5 has one scope, and the gate also serialises discovery.
        await _gate.WaitAsync(ct);

        try
        {
            // Re-read inside the gate: waiters behind the first fetcher take its token.
            if (TryRead(scope, out cached))
                return cached;

            CachedToken token = await FetchAsync(scope, ct);
            _tokens[scope] = token;

            return token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryRead(string scope, out string accessToken)
    {
        if (_tokens.TryGetValue(scope, out CachedToken? token) &&
            token.ExpiresAt - ExpiryGuard > clock.GetUtcNow())
        {
            accessToken = token.AccessToken;

            return true;
        }

        accessToken = "";

        return false;
    }

    private async Task<CachedToken> FetchAsync(string scope, CancellationToken ct)
    {
        HttpClient client = clients.CreateClient(HttpClientName);
        Uri endpoint = _tokenEndpoint ??= await DiscoverTokenEndpointAsync(client, ct);

        // The secret in the body rather than the Authorization header; RFC 6749 §2.3.1 permits both.
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = identity.Value.ClientId,
            ["client_secret"] = identity.Value.ClientSecret,
            ["scope"] = scope
        });

        using HttpResponseMessage response = await client.PostAsync(endpoint, form, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // A transient refusal throws what the resilience pipeline retries, rather than an unmapped 500.
            if (IsTransient(response.StatusCode))
            {
                throw new HttpRequestException(
                    Failure(response.StatusCode, body),
                    inner: null,
                    response.StatusCode);
            }

            // A credential or configuration refusal stays a deployment error, never retried.
            throw new InvalidOperationException(Failure(response.StatusCode, body));
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        // Blank counts as missing, as for §11.3's authority: a blank token would be cached and sent.
        if (!root.TryGetProperty("access_token", out JsonElement accessToken) ||
            accessToken.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            throw new InvalidOperationException(
                "The token endpoint answered success with no usable access_token. The body is " +
                "deliberately not echoed here: it is the one payload this host handles that carries a " +
                "bearer token (§13.4).");
        }

        // expires_in is optional (RFC 6749 §5.1); absent, the token counts as expired, the safe direction.
        int lifetime = root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
            expiresIn.TryGetInt32(out int seconds)
            ? seconds
            : 0;

        TokenFetched(logger, scope, lifetime);

        return new CachedToken(accessToken.GetString()!, clock.GetUtcNow().AddSeconds(lifetime));
    }

    /// <summary>Source-generated, because CA1848 and CA1873 are errors under ADR-019.</summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Fetched a client-credentials token for scope {Scope}, valid for {Lifetime}s.")]
    private static partial void TokenFetched(ILogger logger, string scope, int lifetime);

    /// <summary>Disposes the gate, as CA1001 requires even of a singleton.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>The token endpoint from the discovery document §11.3's JWT handler reads, not a built path.</summary>
    private async Task<Uri> DiscoverTokenEndpointAsync(HttpClient client, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(".well-known/openid-configuration", ct);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!document.RootElement.TryGetProperty("token_endpoint", out JsonElement endpoint) ||
            endpoint.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out Uri? parsed))
        {
            throw new InvalidOperationException(
                $"The discovery document at '{client.BaseAddress}' declares no usable token_endpoint. " +
                $"'{authorityKey.Name}' names an OpenID provider (§11.3), and this " +
                "host needs that same one to mint its own token (§11.5).");
        }

        // The document is trusted for its content, not for where this host posts its secret.
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(Unusable(client, parsed, "is not an http or https URL"));

        // Not weaker than the channel the document came over, since development allows HTTP (§11.3).
        if (client.BaseAddress?.Scheme == Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                Unusable(client, parsed, "downgrades the HTTPS authority to plain HTTP"));
        }

        return parsed;
    }

    /// <summary>Why a syntactically valid <c>token_endpoint</c> is still refused; the URL is public.</summary>
    private string Unusable(HttpClient client, Uri endpoint, string fault) =>
        $"The discovery document at '{client.BaseAddress}' declares a token_endpoint of '{endpoint}', which " +
        $"{fault}. This host posts its client secret there (§11.5), so the endpoint may not be less protected " +
        $"than the authority '{authorityKey.Name}' names (§11.3).";

    /// <summary>The failure message, with RFC 6749's <c>error</c> member and never the body (§13.4).</summary>
    private static string Failure(HttpStatusCode status, string body)
    {
        string detail = "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.String)
            {
                detail = $" ({error.GetString()})";
            }
        }
        catch (JsonException)
        {
            // A non-JSON body says nothing worth repeating.
        }

        // Formatted here: the concatenation below keeps string.Create's handler overload from binding (CS1620).
        string code = ((int)status).ToString(CultureInfo.InvariantCulture);

        return $"The token endpoint refused this host's client credentials with {code}{detail}. " +
            $"'{ServiceIdentityOptions.SectionName}' is this host's credential set (§11.5), " +
            "so this is a deployment fault rather than a caller's.";
    }

    /// <summary>A 5xx, 408 or 429: the shapes <c>AddStandardResilienceHandler</c> treats as transient.</summary>
    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 ||
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}

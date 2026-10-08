using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>The most this reads of a discovery or token answer; either is a few kilobytes from Keycloak.</summary>
    public const int MaxAnswerBytes = 64 * 1024;

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

        using HttpRequestMessage request = new(HttpMethod.Post, endpoint) { Content = form };
        using HttpResponseMessage response = await SendBoundedAsync(client, request, ct);
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

        using JsonDocument document = Parse(body, "token endpoint's success answer");
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
        int declared = root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
            expiresIn.TryGetInt32(out int seconds)
            ? seconds
            : 0;

        // Never past the token's own exp, so a wrong expires_in keeps no dead token; one with no exp is not kept.
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset declaredAt = now.AddSeconds(Math.Max(declared, 0));
        DateTimeOffset expiresAt = OwnExpiry(accessToken.GetString()!) is not { } own ? now
            : own < declaredAt ? own
            : declaredAt;

        int lifetime = (int)Math.Max((expiresAt - now).TotalSeconds, 0);
        TokenFetched(logger, scope, lifetime);

        return new CachedToken(accessToken.GetString()!, expiresAt);
    }

    /// <summary>A JWT's <c>exp</c>, read unverified as nothing is authorised on it here; null when absent.</summary>
    private static DateTimeOffset? OwnExpiry(string token)
    {
        string[] parts = token.Split('.');

        if (parts.Length != 3)
            return null;

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using JsonDocument claims = JsonDocument.Parse(Convert.FromBase64String(payload));

            return claims.RootElement.ValueKind == JsonValueKind.Object &&
                claims.RootElement.TryGetProperty("exp", out JsonElement exp) &&
                exp.TryGetInt64(out long epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Sends, reading the answer under <see cref="MaxAnswerBytes"/> as every other hop does.</summary>
    private static async Task<HttpResponseMessage> SendBoundedAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken ct)
    {
        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        try
        {
            await response.Content.LoadIntoBufferAsync(MaxAnswerBytes, ct);

            return response;
        }
        catch (HttpRequestException e) when (e.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            string message =
                $"The identity provider answered '{request.RequestUri}' with more than {MaxAnswerBytes} bytes, " +
                "which no discovery document or token response comes near (§11.5).";

            // A proxy's oversized error page on a transient status is still transient, so it stays retried.
            if (IsTransient(status))
                throw new HttpRequestException(message, e, status);

            throw new InvalidOperationException(message, e);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>A 200 that is not JSON is a misrouted authority, refused as one rather than escaping.</summary>
    private static JsonDocument Parse(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"The identity provider's {what} is not JSON, so the authority is not an OpenID provider or a " +
                "proxy answers in its place (§11.5). The body is not echoed (§13.4).",
                e);
        }
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
        using HttpRequestMessage request = new(HttpMethod.Get, ".well-known/openid-configuration");
        using HttpResponseMessage response = await SendBoundedAsync(client, request, ct);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = Parse(await response.Content.ReadAsStringAsync(ct), "discovery document");

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

        // Keycloak serves it on the authority's own host and port, both routes in §14.1's Compose included.
        if (client.BaseAddress is { } authority &&
            Uri.Compare(parsed, authority, UriComponents.HostAndPort, UriFormat.Unescaped,
                StringComparison.OrdinalIgnoreCase) != 0)
        {
            throw new InvalidOperationException(
                Unusable(client, parsed, "is on another host or port than the authority"));
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

            // Only in an error code's shape, so the endpoint cannot write prose or a secret into a log.
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.String &&
                ErrorCode().IsMatch(error.GetString()!))
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

    /// <summary>Every RFC 6749 §5.2 code's shape: narrower than its NQSCHAR, which admits an echoed form.</summary>
    [GeneratedRegex(@"\A[a-z_]{1,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCode();

    /// <summary>A 5xx, 408 or 429: the shapes <c>AddStandardResilienceHandler</c> treats as transient.</summary>
    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 ||
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}

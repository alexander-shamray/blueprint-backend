using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Notifications.Application.Push;

namespace Notifications.Infrastructure.Push;

/// <summary>FCM's access token, for a signed assertion under OAuth 2.0's JWT bearer grant (RFC 7523).</summary>
/// <remarks>
/// Cached, and renewed <see cref="RenewalMargin"/> before the lifetime the exchange returns. Google's refusal is a
/// credential fault and its silence a transient one, counted here as the provider's own answers are.
/// </remarks>
internal sealed class FcmAccessTokens : IDisposable
{
    internal const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    internal const string Grant = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    /// <summary>Google's ceiling on an assertion's lifetime.</summary>
    internal static readonly TimeSpan AssertionLifetime = TimeSpan.FromHours(1);

    /// <summary>How long before the returned lifetime ends a token is renewed, so none is sent as it expires.</summary>
    internal static readonly TimeSpan RenewalMargin = TimeSpan.FromMinutes(5);

    private readonly FcmOptions _options;
    private readonly PushMetrics _metrics;
    private readonly TimeProvider _clock;
    private readonly RSA _key;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Issued? _issued;

    public FcmAccessTokens(IOptions<FcmOptions> options, PushMetrics metrics, TimeProvider clock)
    {
        _options = options.Value;
        _metrics = metrics;
        _clock = clock;

        // A missing key reaches here in Development alone: AddPushChannel refuses it anywhere else before start.
        _key = RSA.Create(2048);
        if (!string.IsNullOrWhiteSpace(_options.PrivateKey))
            _key.ImportFromPem(_options.PrivateKey);
    }

    public async ValueTask<string> GetAsync(HttpClient client, CancellationToken ct)
    {
        if (Volatile.Read(ref _issued) is { } held && _clock.GetUtcNow() < held.RenewAt)
            return held.AccessToken;

        await _gate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _issued) is { } again && _clock.GetUtcNow() < again.RenewAt)
                return again.AccessToken;

            Issued issued = await ExchangeAsync(client, ct);
            Volatile.Write(ref _issued, issued);
            return issued.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops <paramref name="refused"/> while it is the one held, so the next attempt exchanges.</summary>
    public void Invalidate(string refused)
    {
        if (Volatile.Read(ref _issued) is { } held && held.AccessToken == refused)
            Interlocked.CompareExchange(ref _issued, null, held);
    }

    internal string Assertion(DateTimeOffset now) =>
        Jws.Sign(
            new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" },
            new Dictionary<string, object>
            {
                ["iss"] = _options.ClientEmail!,
                ["scope"] = Scope,
                ["aud"] = _options.TokenUri!.AbsoluteUri,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = (now + AssertionLifetime).ToUnixTimeSeconds()
            },
            data => _key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    public void Dispose()
    {
        _key.Dispose();
        _gate.Dispose();
    }

    private async Task<Issued> ExchangeAsync(HttpClient client, CancellationToken ct)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        using FormUrlEncodedContent form = new(
        [
            new KeyValuePair<string, string>("grant_type", Grant),
            new KeyValuePair<string, string>("assertion", Assertion(now))
        ]);
        using HttpResponseMessage response = await client.PostAsync(_options.TokenUri, form, ct);
        int status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
            throw Refused(status is 429 or >= 500 ? PushFault.Transient : PushFault.Credential, status);

        TokenAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<TokenAnswer>(ct);
        }
        catch (JsonException)
        {
            answer = null;
        }

        if (answer is not { AccessToken.Length: > 0, ExpiresIn: > 0 })
            throw Refused(PushFault.Credential, status);

        // A lifetime inside the margin is used once and exchanged again, rather than held past its end.
        TimeSpan lifetime = TimeSpan.FromSeconds(answer.ExpiresIn);
        TimeSpan held = lifetime > RenewalMargin ? lifetime - RenewalMargin : TimeSpan.Zero;
        return new Issued(answer.AccessToken, now + held);
    }

    // Names the status alone: an OAuth error body can quote the assertion's claims.
    private PushUnavailableException Refused(PushFault cause, int status)
    {
        _metrics.Unavailable(FcmPushProvider.Name, cause);
        return new PushUnavailableException($"Google's token endpoint answered {status}.", cause, status);
    }

    private sealed record Issued(string AccessToken, DateTimeOffset RenewAt);

    private sealed record TokenAnswer(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);
}

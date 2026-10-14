using System.Buffers.Text;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Notifications.Infrastructure.Push;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Both providers' tokens verify under a key the test generated and configured, and renew in time.</summary>
public sealed class PushTokenTests : IDisposable
{
    private static readonly Uri TokenUri = new("https://oauth2.example.test/token");

    private readonly ServiceProvider _meters = new ServiceCollection().AddMetrics().BuildServiceProvider();

    public void Dispose() => _meters.Dispose();

    [Fact]
    public void Fcms_assertion_is_signed_by_the_configured_service_account_key_for_the_token_endpoint()
    {
        DateTimeOffset now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        using RSA configured = RSA.Create(2048);
        using FcmAccessTokens tokens = Fcm(configured.ExportPkcs8PrivateKeyPem());

        string[] parts = tokens.Assertion(now).Split('.');

        configured
            .VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                Base64Url.DecodeFromChars(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1)
            .ShouldBeTrue();

        Json(parts[0]).GetProperty("alg").GetString().ShouldBe("RS256");
        JsonElement claims = Json(parts[1]);
        claims.GetProperty("iss").GetString().ShouldBe("notifications@commerce-test.example.invalid");
        claims.GetProperty("scope").GetString().ShouldBe(FcmAccessTokens.Scope);
        claims.GetProperty("aud").GetString().ShouldBe(TokenUri.AbsoluteUri);
        claims.GetProperty("iat").GetInt64().ShouldBe(now.ToUnixTimeSeconds());
        claims.GetProperty("exp").GetInt64().ShouldBe((now + FcmAccessTokens.AssertionLifetime).ToUnixTimeSeconds());
    }

    [Fact]
    public void Apns_provider_token_is_an_es256_token_of_the_team_under_its_configured_key()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        using ECDsa configured = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ApnsProviderTokens tokens = Apns(clock, configured.ExportPkcs8PrivateKeyPem());

        string[] parts = tokens.Get().Split('.');

        configured
            .VerifyData(
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                Base64Url.DecodeFromChars(parts[2]),
                HashAlgorithmName.SHA256)
            .ShouldBeTrue("RFC 7518 section 3.4: r and s, fixed width, concatenated");

        JsonElement header = Json(parts[0]);
        header.GetProperty("alg").GetString().ShouldBe("ES256");
        header.GetProperty("kid").GetString().ShouldBe("TESTKEY001");
        JsonElement claims = Json(parts[1]);
        claims.GetProperty("iss").GetString().ShouldBe("TESTTEAM01");
        claims.GetProperty("iat").GetInt64().ShouldBe(clock.GetUtcNow().ToUnixTimeSeconds());
    }

    [Fact]
    public void Apns_provider_token_is_reused_until_its_lifetime_and_minted_again_after_it()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        using ApnsProviderTokens tokens = Apns(clock, privateKey: null);
        string first = tokens.Get();

        clock.Advance(ApnsHop.ProviderTokenLifetime - TimeSpan.FromSeconds(1));
        tokens.Get().ShouldBe(first, "Apple refuses a token renewed more often than every twenty minutes");

        clock.Advance(TimeSpan.FromSeconds(1));
        Json(tokens.Get().Split('.')[1]).GetProperty("iat").GetInt64().ShouldBe(clock.GetUtcNow().ToUnixTimeSeconds());
    }

    [Fact]
    public void A_refused_apns_token_is_minted_again_at_the_next_attempt()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
        using ApnsProviderTokens tokens = Apns(clock, privateKey: null);
        _ = tokens.Get();

        clock.Advance(TimeSpan.FromMinutes(1));
        tokens.Invalidate();

        Json(tokens.Get().Split('.')[1]).GetProperty("iat").GetInt64().ShouldBe(clock.GetUtcNow().ToUnixTimeSeconds());
    }

    private static JsonElement Json(string part) =>
        JsonDocument.Parse(Base64Url.DecodeFromChars(part)).RootElement.Clone();

    private FcmAccessTokens Fcm(string? privateKey) =>
        new(
            Options.Create(new FcmOptions
            {
                TokenUri = TokenUri,
                ClientEmail = "notifications@commerce-test.example.invalid",
                PrivateKey = privateKey
            }),
            new PushMetrics(_meters.GetRequiredService<IMeterFactory>()),
            TimeProvider.System);

    private static ApnsProviderTokens Apns(TimeProvider clock, string? privateKey) =>
        new(
            Options.Create(new ApnsOptions { TeamId = "TESTTEAM01", KeyId = "TESTKEY001", PrivateKey = privateKey }),
            clock);
}

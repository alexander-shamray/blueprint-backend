using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// A real HTTP server standing in for the identity provider: a discovery
/// document and a token endpoint, and nothing else.
/// </summary>
/// <remarks>
/// A server rather than a substituted <see cref="HttpMessageHandler"/>, because
/// half of what is being tested is what <see cref="CachingTokenClient"/> does
/// with a <i>document</i> — that it reads <c>token_endpoint</c> out of the
/// discovery response rather than appending a Keycloak-shaped path to the
/// authority. A substituted handler would have to be told the answer, which is
/// the thing under test.
/// </remarks>
public sealed class StubIdentityProvider : IAsyncLifetime
{
    private WebApplication? _app;
    private X509Certificate2? _certificate;

    /// <summary>The authority, with the trailing slash a base address needs.</summary>
    public Uri Authority { get; private set; } = null!;

    /// <summary>How many times the discovery document has been fetched.</summary>
    public int Discoveries { get; private set; }

    /// <summary>Every token request's form fields, in order.</summary>
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> TokenRequests { get; } = new();

    /// <summary>Seconds to declare each issued token valid for.</summary>
    public int? ExpiresIn { get; set; } = 300;

    /// <summary>Status to answer the token endpoint with, when not 200.</summary>
    public int TokenStatus { get; set; } = StatusCodes.Status200OK;

    /// <summary>The body to answer a non-200 token request with.</summary>
    public string TokenFailureBody { get; set; } = "{}";

    /// <summary>Answer the discovery document with no <c>token_endpoint</c>.</summary>
    public bool OmitTokenEndpoint { get; set; }

    /// <summary>Answer a 200 whose <c>access_token</c> is the empty string.</summary>
    public bool BlankAccessToken { get; set; }

    /// <summary>
    /// Serve over TLS with a self-signed certificate, so <see cref="Authority"/>
    /// is an <c>https</c> URL.
    /// </summary>
    /// <remarks>
    /// The one thing a plain-HTTP stub cannot express is a <i>downgrade</i>, so
    /// a provider that only ever spoke HTTP would leave that guard asserted in
    /// prose and measured nowhere.
    /// </remarks>
    public bool UseHttps { get; set; }

    /// <summary>
    /// The <c>token_endpoint</c> to advertise, in place of this stub's own.
    /// </summary>
    /// <remarks>
    /// The document is the only thing that says where the secret goes, which is
    /// exactly why it is worth being able to make it say something hostile.
    /// </remarks>
    public string? AdvertisedTokenEndpoint { get; set; }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        if (UseHttps)
            _certificate = SelfSigned();

        builder.WebHost.ConfigureKestrel(o =>
            o.Listen(
                IPAddress.Loopback,
                0,
                listen =>
                {
                    if (_certificate is not null)
                        listen.UseHttps(_certificate);
                }));

        _app = builder.Build();

        // The realm path is part of the authority, exactly as Keycloak's is —
        // so a client that appended to a base address without a trailing slash
        // would ask for /.well-known/... at the root and miss this.
        _app.MapGet("/realms/test/.well-known/openid-configuration", () =>
        {
            Discoveries++;

            return OmitTokenEndpoint
                ? Results.Json(new { issuer = $"{Authority}" })
                : Results.Json(new
                {
                    token_endpoint = AdvertisedTokenEndpoint ?? $"{Authority}protocol/openid-connect/token"
                });
        });

        _app.MapPost("/realms/test/protocol/openid-connect/token", async (HttpContext context) =>
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            TokenRequests.Enqueue(form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal));

            if (TokenStatus != StatusCodes.Status200OK)
                return Results.Content(TokenFailureBody, "application/json", statusCode: TokenStatus);

            Dictionary<string, object> body = new(StringComparer.Ordinal)
            {
                ["access_token"] = BlankAccessToken ? "" : $"issued-{TokenRequests.Count}",
                ["token_type"] = "Bearer"
            };

            if (ExpiresIn is int lifetime)
                body["expires_in"] = lifetime;

            return Results.Json(body);
        });

        await _app.StartAsync();
        Authority = new Uri($"{_app.Urls.Single()}/realms/test/");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();

        _certificate?.Dispose();
    }

    /// <summary>
    /// A throwaway certificate for loopback, generated in process.
    /// </summary>
    /// <remarks>
    /// Generated rather than taken from <c>dotnet dev-certs</c>: the suite must
    /// pass on a runner where that has never been run, and a test that needs a
    /// machine prepared by hand is a test that is skipped in CI.
    /// </remarks>
    private static X509Certificate2 SelfSigned()
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        // Windows' SChannel will not serve a certificate whose key is
        // ephemeral, so it makes a round trip through PKCS#12 first. On Linux
        // this is a no-op that costs nothing.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
    }
}

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

/// <summary>A real server with a discovery document and a token endpoint, since the document is under test.</summary>
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

    /// <summary>Serve over TLS, so a test can express a downgrade from an <c>https</c> authority.</summary>
    public bool UseHttps { get; set; }

    /// <summary>A <c>token_endpoint</c> to advertise in place of this stub's own, hostile if a test needs.</summary>
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

        // The realm path is part of the authority, as Keycloak's is, so a slashless base address misses it.
        _app.MapGet(
            "/realms/test/.well-known/openid-configuration",
            () =>
            {
                Discoveries++;

                return OmitTokenEndpoint
                    ? Results.Json(new { issuer = $"{Authority}" })
                    : Results.Json(new
                    {
                        token_endpoint = AdvertisedTokenEndpoint ?? $"{Authority}protocol/openid-connect/token"
                    });
            });

        _app.MapPost(
            "/realms/test/protocol/openid-connect/token",
            async (HttpContext context) =>
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

    /// <summary>A loopback certificate generated in process, so no runner needs <c>dotnet dev-certs</c>.</summary>
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

        // Windows' SChannel will not serve an ephemeral key, hence the PFX round trip.
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);
    }
}

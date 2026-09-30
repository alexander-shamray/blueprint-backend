using System.Security.Claims;
using Common.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>Inbound claim mapping, the one step from a token's <c>sub</c> to <see cref="ICurrentUser.Id"/>.</summary>
public class InboundClaimMappingTests
{
    private const string Issuer = "https://identity.invalid/realms/test";

    private static readonly SymmetricSecurityKey SigningKey =
        new(System.Text.Encoding.UTF8.GetBytes("a-test-signing-key-of-sufficient-length-for-hmac-sha256"));

    [Fact]
    public async Task A_raw_sub_claim_reaches_ICurrentUser_as_the_subject()
    {
        Guid subject = Guid.CreateVersion7();

        using IHost host = await StartAsync();
        HttpClient client = host.GetTestClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(subject));

        HttpResponseMessage response = await client.GetAsync(
            new Uri("/subject", UriKind.Relative),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        body.ShouldBe(subject.ToString());
    }

    [Fact]
    public async Task Without_the_mapping_the_same_token_has_no_subject_at_all()
    {
        // Were the framework default to change, the same valid token would carry a `sub` nothing reads.
        Guid subject = Guid.CreateVersion7();

        using IHost host = await StartAsync(mapInboundClaims: false);
        HttpClient client = host.GetTestClient();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token(subject));

        // The throw itself: this pipeline has no exception handler, where a host would answer 500 (§10.5).
        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => client.GetAsync(
                new Uri("/subject", UriKind.Relative),
                TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(ClaimTypes.NameIdentifier);
    }

    private static string Token(Guid subject)
    {
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = Issuer,
            Audience = AuthenticationExtensions.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = subject.ToString() },
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    private static Task<IHost> StartAsync(bool mapInboundClaims = true) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();

                web.ConfigureServices(services =>
                {
                    services.AddHttpContextAccessor();
                    services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddJwtBearer(options =>
                        {
                            // §11.3's values, with the key handed over so no discovery is fetched.
                            options.Audience = AuthenticationExtensions.Audience;
                            options.MapInboundClaims = mapInboundClaims;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = Issuer,
                                ValidateAudience = true,
                                ValidateLifetime = true,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = SigningKey,
                                ClockSkew = TimeSpan.FromSeconds(30),
                                NameClaimType = "preferred_username",
                                RoleClaimType = "roles"
                            };
                            options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                        });
                });

                web.Configure(app =>
                {
                    // Authentication only, since what the handler puts on the context is under test.
                    app.UseAuthentication();
                    app.Run(async context =>
                    {
                        ICurrentUser caller = context.RequestServices.GetRequiredService<ICurrentUser>();

                        await context.Response.WriteAsync(caller.Id.ToString());
                    });
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(NullLoggerProvider.Instance))
            .StartAsync();
}

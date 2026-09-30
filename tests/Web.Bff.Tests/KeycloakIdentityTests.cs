using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using Common.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§11.5's and ADR-052's grants against a real Keycloak, as a wrong realm still compiles.</summary>
[Collection(nameof(KeycloakCollection))]
public sealed class KeycloakIdentityTests(KeycloakFixture keycloak)
{
    private const string BffClient = "web-bff";
    private const string BffSecret = "local-dev-secret";

    /// <summary>ADR-052's address permission as a literal, since this suite may not reference Ordering.</summary>
    private const string DeliveryAddress = "orders:delivery-address";

    private static readonly JwtSecurityTokenHandler Tokens = new();

    [Fact]
    public async Task The_BFF_client_credentials_token_carries_the_platform_audience()
    {
        (bool granted, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        granted.ShouldBeTrue("the realm must hold web-bff with service accounts enabled (§11.5).");

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // Scope and audience are different claims, joined only by the realm's audience mapper (§11.5).
        jwt.Audiences.ShouldContain(AuthenticationExtensions.Audience);
    }

    [Fact]
    public async Task The_BFF_service_account_carries_no_permission_claim()
    {
        (_, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // §11.4's vocabulary is a person's; a host holds a permission only where its read crosses subjects (ADR-052).
        jwt.Claims.ShouldNotContain(c => c.Type == PermissionClaim.Type);
    }

    [Fact]
    public async Task The_worker_client_is_issued_exactly_the_grant_the_record_names()
    {
        (bool granted, string token) = await keycloak.ClientCredentialsAsync(
            KeycloakFixture.WorkerClient,
            KeycloakFixture.WorkerSecret);

        granted.ShouldBeTrue(
            "the realm must hold shipping-worker with service accounts enabled (ADR-052)");

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // Without the audience, the permission rides a token no service validates.
        jwt.Audiences.ShouldContain(AuthenticationExtensions.Audience);

        // Exactly, as ADR-052 sizes this credential by what a thief could read with it.
        string[] permissions =
        [
            .. jwt.Claims.Where(c => c.Type == PermissionClaim.Type).Select(c => c.Value)
        ];

        permissions.ShouldBe(["orders:delivery-address"]);
    }

    [Fact]
    public async Task A_service_requiring_the_permission_accepts_the_worker_and_refuses_the_BFF()
    {
        (_, string worker) = await keycloak.ClientCredentialsAsync(
            KeycloakFixture.WorkerClient,
            KeycloakFixture.WorkerSecret);
        (_, string bff) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        await using WebApplication service = await ServiceValidatingTheRealm();
        using HttpClient client = service.GetTestClient();

        (await StatusOfAsync(client, worker)).ShouldBe(HttpStatusCode.OK);

        // The BFF shares issuer, key and audience, so its 403 can only be the permission (ADR-052).
        (await StatusOfAsync(client, bff)).ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpStatusCode> StatusOfAsync(HttpClient client, string token)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/address");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        return response.StatusCode;
    }

    [Fact]
    public async Task A_service_validating_the_realm_accepts_that_token()
    {
        (_, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        await using WebApplication service = await ServiceValidatingTheRealm();
        using HttpClient client = service.GetTestClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        // The real registration, discovery document, keys and audience, which §12.4's fixture cannot reach.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_client_without_the_scope_is_refused_by_that_service()
    {
        const string Unrelated = "unrelated-client";
        const string Secret = "unrelated-secret";

        await keycloak.CreateUnrelatedClientAsync(Unrelated, Secret);

        (bool granted, string token) = await keycloak.ClientCredentialsAsync(Unrelated, Secret);

        // Same realm, issuer and signing key; only the audience is missing.
        granted.ShouldBeTrue();
        Tokens.ReadJwtToken(token).Audiences.ShouldNotContain(AuthenticationExtensions.Audience);

        await using WebApplication service = await ServiceValidatingTheRealm();
        using HttpClient client = service.GetTestClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>A minimal host running the platform's own token validation rather than Catalog (§11.5).</summary>
    private async Task<WebApplication> ServiceValidatingTheRealm()
    {
        // Development, as the container speaks plain HTTP and §11.3 allows that only there.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = keycloak.Authority;

        // The platform's own registration, so this proves what a service validates.
        builder.AddJwtAuthentication();

        // RequirePermission, so this policy and Ordering's agree on the claim type (§11.4).
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy(DeliveryAddress, p => p.RequirePermission(DeliveryAddress));

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
        app.MapGet("/address", () => Results.Ok()).RequireAuthorization(DeliveryAddress);

        await app.StartAsync(TestContext.Current.CancellationToken);

        return app;
    }
}

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

/// <summary>
/// §11.5's whole argument, against a real Keycloak because realm configuration
/// compiles the same right or wrong: the scope becomes an audience, the
/// audience is what a service validates, and neither is granted to a client
/// the realm merely holds. The negative half matters more: a mapper that put
/// the audience on every token would pass the first test and hand the
/// platform to any client in the realm. Since ADR-052 the realm holds two
/// credentialed clients, and the second is proved both ways here — a grant is
/// a claim about what a token carries, and only a real Keycloak carries one.
/// </summary>
[Collection(nameof(KeycloakCollection))]
public sealed class KeycloakIdentityTests(KeycloakFixture keycloak)
{
    private const string BffClient = "web-bff";
    private const string BffSecret = "local-dev-secret";

    /// <summary>
    /// The permission ADR-052 gives the address reader, spelt as a literal.
    /// </summary>
    /// <remarks>
    /// <c>OrderingPermissions.DeliveryAddress</c> is the owner and this suite
    /// may not reference Ordering to read it; the realm's closed role set is
    /// what ties the two spellings together (§11.4, §11.5).
    /// </remarks>
    private const string DeliveryAddress = "orders:delivery-address";

    private static readonly JwtSecurityTokenHandler Tokens = new();

    [Fact]
    public async Task The_BFF_client_credentials_token_carries_the_platform_audience()
    {
        (bool granted, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        granted.ShouldBeTrue("the realm must hold web-bff with service accounts enabled (§11.5).");

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // The one claim the whole of §11.5 is about. `scope: commerce-api` and
        // `aud: commerce-api` are NOT the same claim, and nothing makes one
        // imply the other but the realm's audience mapper.
        jwt.Audiences.ShouldContain(AuthenticationExtensions.Audience);
    }

    [Fact]
    public async Task The_BFF_service_account_carries_no_permission_claim()
    {
        (_, string token) = await keycloak.ClientCredentialsAsync(BffClient, BffSecret);

        JwtSecurityToken jwt = Tokens.ReadJwtToken(token);

        // §11.4's vocabulary is a person's by default, and this client is the
        // case that holds: the BFF's hop reads what a product listing already
        // publishes, so Catalog's gRPC service asks for authentication and
        // deliberately not a permission. ADR-052 names the exception rather
        // than widening the rule — a host holds one only where the read
        // crosses subjects, and this one does not.
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

        // The audience first: without it the permission below is carried in a
        // token no service validates, and the read fails for the other reason.
        jwt.Audiences.ShouldContain(AuthenticationExtensions.Audience);

        // Exactly, not ShouldContain. ADR-052 sizes this credential by what it
        // reads when it is stolen, so a realm that granted more has to fail
        // somewhere, and this is the assertion that says the realm did not.
        // Keycloak's own defaults live in realm_access and on the account
        // client, which this mapper does not read.
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

        // The refusal, and the BFF rather than an unrelated client on purpose:
        // its token carries the same issuer, the same signing key AND the same
        // audience, so a 403 here can only be the permission doing the work.
        // An unrelated client would be refused at the audience and prove
        // nothing about the grant (ADR-052).
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

        // The real AddJwtAuthentication, the real discovery document, the real
        // signing keys, the real audience constant. This is the assertion
        // §12.4's fixture structurally cannot make.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_client_without_the_scope_is_refused_by_that_service()
    {
        const string Unrelated = "unrelated-client";
        const string Secret = "unrelated-secret";

        await keycloak.CreateUnrelatedClientAsync(Unrelated, Secret);

        (bool granted, string token) = await keycloak.ClientCredentialsAsync(Unrelated, Secret);

        // It gets a perfectly valid token — same realm, same issuer, same
        // signing key. What it does not get is the audience.
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

    /// <summary>
    /// A minimal host running the platform's real token validation against the
    /// container.
    /// </summary>
    /// <remarks>
    /// §11.5 prints this half as a call to Catalog, and a service is what it
    /// stands for; what is actually under test is <c>AddJwtAuthentication</c>
    /// plus the realm, and neither of those is Catalog's. Driving a real
    /// service here would add a SQL container and a migrator run to a suite
    /// whose subject is a token — and it would still be asserting this.
    /// </remarks>
    private async Task<WebApplication> ServiceValidatingTheRealm()
    {
        // Development, because the container speaks plain HTTP:
        // AddJwtAuthentication refuses a non-https authority outside
        // Development and RequireHttpsMetadata would stop the discovery
        // document being fetched at all (§11.3). Both are the same rule, and
        // the container is the case they carve out.
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = keycloak.Authority;

        // The platform's own registration, not a copy of it. A hand-rolled
        // AddJwtBearer here would validate whatever this file decided to
        // validate and prove nothing about what a service does.
        builder.AddJwtAuthentication();

        // RequirePermission, not RequireClaim: the claim type is Common.Web's
        // (§11.4), so this policy and the one Ordering registers cannot drift
        // apart about where a permission lives in a token.
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

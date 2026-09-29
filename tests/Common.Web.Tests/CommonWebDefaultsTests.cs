using Common.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class CommonWebDefaultsTests
{
    [Fact]
    public void The_one_call_a_host_makes_registers_all_three_pieces()
    {
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        // Observability (§13.2).
        host.Services.GetService<MeterProvider>().ShouldNotBeNull();
        host.Services.GetService<TracerProvider>().ShouldNotBeNull();

        // The RFC 9457 customisation PR-03 shipped (§10.5). Registered through
        // AddProblemDetails, whose observable effect here is the configured
        // options rather than a service of its own.
        host.Services
            .GetRequiredService<IOptions<ProblemDetailsOptions>>()
            .Value
            .CustomizeProblemDetails
            .ShouldNotBeNull();

        // Liveness only (§13.5). Common.Web has no connection strings, so it
        // registers no readiness check — those come from each service's own
        // Infrastructure.
        host.Services.GetService<HealthCheckService>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Authorization_is_deny_by_default()
    {
        // §11.4's deny-by-default, and the reason it is a fallback rather than
        // a review rule: without one, UseAuthorization evaluates NOTHING on an
        // endpoint carrying no policy metadata, so a new *Endpoints class that
        // omits its RequireAuthorization line is reachable with no compiler
        // error, no ValidateOnBuild failure and no failing test.
        //
        // Asserted through the provider rather than off AuthorizationOptions,
        // because the provider is what AuthorizationMiddleware asks — reading
        // the options would pass on a fallback the middleware never consults.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        AuthorizationPolicy? fallback = await host.Services
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull(
            "an endpoint with no policy metadata is otherwise reachable by anyone (§11.4)");

        // And it is the authenticated-user requirement rather than an empty
        // policy: a fallback that requires nothing is registered, resolves,
        // and admits everybody — which passes the assertion above.
        fallback.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Authentication_and_the_shared_policy_arrive_together()
    {
        // Neither works alone, which is why one test covers both: a policy
        // requiring an authenticated user, with no scheme registered to
        // authenticate one, rejects every request that reaches it — and the
        // failure surfaces in whichever service first maps an endpoint to it
        // rather than here.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        AuthenticationScheme? bearer = await host.Services
            .GetRequiredService<IAuthenticationSchemeProvider>()
            .GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);

        bearer.ShouldNotBeNull("§11.2 — every service re-validates the token itself");

        (await host.Services
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync("authenticated"))
            .ShouldNotBeNull("the one policy every host shares (§13.2), and the gateway's route file names it");
    }

    [Fact]
    public void The_current_user_port_resolves_per_request()
    {
        // §11.4's port and the accessor it depends on. ASP.NET Core registers
        // no IHttpContextAccessor by default, so the pairing is the assertion:
        // ICurrentUser alone would resolve here and fail ValidateOnBuild in
        // every real host, which is the wrong place to find out.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();
        using IServiceScope scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().ShouldBeOfType<HttpContextCurrentUser>();

        // Scoped, not singleton: a caller is per request, and a captured one
        // would answer the previous request's subject to the next.
        ServiceDescriptor descriptor = builder.Services.Single(d => d.ServiceType == typeof(ICurrentUser));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void A_host_that_cannot_name_its_identity_provider_does_not_start()
    {
        // The eager read of §11.3, and the posture AddSqlServer and
        // AddMassTransitMessaging already take: the key is refused where it is
        // read, so no options type stands between it and the failure.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = null;

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        // Naming the key is the whole value over an options exception: the
        // message is read by somebody looking at a crash loop in a cluster,
        // and "Identity:Authority" is the search term that ends it.
        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_authority_is_a_missing_one(string configured)
    {
        // An environment variable set to nothing — `Identity__Authority=`, the
        // commonest way a deployment gets this wrong — reaches Configuration
        // as "" rather than null, so a null-only guard admits it. The host then
        // starts, having promised it would not, and JwtBearer fails building a
        // metadata address on the first request that carries a token: a 500
        // during traffic instead of a refusal at boot, which is the whole
        // difference the eager read exists to buy.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = configured;

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
    }

    [Theory]
    [InlineData("https://identity.example/realms/commerce#fragment")]
    [InlineData("https://identity.example/realms/commerce?tenant=a")]
    public void An_authority_carrying_a_query_or_fragment_is_not_a_base_address(string configured)
    {
        // Absolute, https, and still not somewhere a discovery document can be
        // fetched from: JwtBearer appends `/.well-known/openid-configuration`
        // to this string, and appending to a fragment puts the suffix in a part
        // of the URL no server ever sees. The host would start and the first
        // bearer request would fetch the realm page instead — the deferred
        // failure this guard exists to turn into a deployment error, reached by
        // a value the shape check above accepts.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = configured;

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
        thrown.Message.ShouldContain(configured, Case.Sensitive);
    }

    [Theory]
    [InlineData("keycloak:8080/realms/commerce")]   // the scheme somebody dropped
    [InlineData("/realms/commerce")]                // a path, from a copied fragment
    [InlineData("ftp://identity.example/realms/commerce")]
    public void An_authority_that_is_not_an_http_url_is_a_missing_one(string configured)
    {
        // Blank was the commonest wrong value, not the only one. Each of these
        // is non-blank and still not an address a discovery document can be
        // fetched from, so a guard that only asks "is it empty?" lets the host
        // start and moves the failure into the first request that carries a
        // token — which is the trade the eager read exists to refuse.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = configured;

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
        thrown.Message.ShouldContain(configured, Case.Sensitive);
    }

    [Fact]
    public void A_plain_http_authority_outside_development_does_not_start()
    {
        // The same rule RequireHttpsMetadata applies, moved to startup. Signing
        // keys fetched over a channel an attacker can rewrite make every
        // validation in §11.3 decorative, and a host that would refuse to fetch
        // them should refuse to start rather than accept traffic first.
        HostApplicationBuilder builder = TelemetryHost.Builder(Environments.Production);
        builder.Configuration[AuthenticationExtensions.AuthorityKey] =
            "http://keycloak:8080/realms/commerce";

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
    }

    [Fact]
    public void A_plain_http_authority_in_development_is_the_documented_local_setup()
    {
        // The other side of the same line, and the reason it is scoped to the
        // environment rather than absolute: §14.1's Compose stack runs Keycloak
        // on http://localhost:8080, and a rule with no carve-out would make the
        // documented local flow impossible.
        HostApplicationBuilder builder = TelemetryHost.Builder(Environments.Development);
        builder.Configuration[AuthenticationExtensions.AuthorityKey] =
            "http://localhost:8080/realms/commerce";

        Should.NotThrow(builder.AddCommonWebDefaults);
    }
}

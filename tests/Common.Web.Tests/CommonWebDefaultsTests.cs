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

        // §10.5's RFC 9457 customisation, observable as configured options.
        host.Services
            .GetRequiredService<IOptions<ProblemDetailsOptions>>()
            .Value
            .CustomizeProblemDetails
            .ShouldNotBeNull();

        // Liveness only (§13.5): readiness checks come from each service's own Infrastructure.
        host.Services.GetService<HealthCheckService>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Authorization_is_deny_by_default()
    {
        // §11.4's deny-by-default fallback, read through the provider AuthorizationMiddleware asks.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();

        AuthorizationPolicy? fallback = await host.Services
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull(
            "an endpoint with no policy metadata is otherwise reachable by anyone (§11.4)");

        // The authenticated-user requirement, since an empty fallback admits everybody.
        fallback.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Authentication_and_the_shared_policy_arrive_together()
    {
        // One test for both, since a policy with no scheme to satisfy it rejects every request.
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
        // §11.4's port with its accessor, which ASP.NET Core does not register by default.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddCommonWebDefaults();

        using IHost host = builder.Build();
        using IServiceScope scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().ShouldNotBeNull();
        scope.ServiceProvider.GetRequiredService<ICurrentUser>().ShouldBeOfType<HttpContextCurrentUser>();

        // Scoped, since a captured caller would answer the previous request's subject.
        ServiceDescriptor descriptor = builder.Services.Single(d => d.ServiceType == typeof(ICurrentUser));
        descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    [Fact]
    public void A_host_that_cannot_name_its_identity_provider_does_not_start()
    {
        // §11.3's eager read: the key is refused where it is read.
        HostApplicationBuilder builder = TelemetryHost.Builder();
        builder.Configuration[AuthenticationExtensions.AuthorityKey] = null;

        InvalidOperationException thrown =
            Should.Throw<InvalidOperationException>(builder.AddCommonWebDefaults);

        // The key's name is the search term for whoever reads the crash loop.
        thrown.Message.ShouldContain(AuthenticationExtensions.AuthorityKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_authority_is_a_missing_one(string configured)
    {
        // `Identity__Authority=` reaches Configuration as "", which a null-only guard admits.
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
        // JwtBearer appends the discovery path, which a query or fragment would swallow.
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
        // Non-blank, and still no address a discovery document can be fetched from.
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
        // RequireHttpsMetadata's rule, moved to startup (§11.3).
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
        // §14.1's Compose Keycloak is plain HTTP, so Development is carved out.
        HostApplicationBuilder builder = TelemetryHost.Builder(Environments.Development);
        builder.Configuration[AuthenticationExtensions.AuthorityKey] =
            "http://localhost:8080/realms/commerce";

        Should.NotThrow(builder.AddCommonWebDefaults);
    }
}

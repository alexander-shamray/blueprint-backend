using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§11.3's validation parameters, read back off the built options, since none fails visibly.</summary>
public class JwtAuthenticationTests
{
    private static JwtBearerOptions Options(string? environmentName = null)
    {
        HostApplicationBuilder builder = TelemetryHost.Builder(environmentName);

        builder.AddCommonWebDefaults();

        // Get, not CurrentValue: ValidAudience is post-configured per named scheme.
        return builder.Build().Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);
    }

    [Fact]
    public void The_authority_comes_from_configuration_and_the_audience_does_not()
    {
        JwtBearerOptions options = Options();

        options.Authority.ShouldBe(TelemetryHost.Authority);

        // A constant, not a key, since §11.5 settles on one audience for the whole platform.
        options.Audience.ShouldBe(AuthenticationExtensions.Audience);
        options.TokenValidationParameters.ValidAudience.ShouldBe(AuthenticationExtensions.Audience);
    }

    [Fact]
    public void Every_validation_is_on_and_the_clock_skew_is_thirty_seconds()
    {
        JwtBearerOptions options = Options();

        options.TokenValidationParameters.ValidateIssuer.ShouldBeTrue();
        options.TokenValidationParameters.ValidateAudience.ShouldBeTrue();
        options.TokenValidationParameters.ValidateLifetime.ShouldBeTrue();
        options.TokenValidationParameters.ValidateIssuerSigningKey.ShouldBeTrue();

        // The framework default keeps an expired token working five minutes past its `exp` (§11.3).
        options.TokenValidationParameters.ClockSkew.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void The_subject_and_the_display_name_are_different_claims()
    {
        JwtBearerOptions options = Options();

        // Identity.Name is a display name; ICurrentUser.Id reads NameIdentifier (§11.4).
        options.TokenValidationParameters.NameClaimType.ShouldBe("preferred_username");
        options.TokenValidationParameters.RoleClaimType.ShouldBe("roles");

        // The framework default, pinned because §11.4's subject rule rests on `sub` becoming NameIdentifier.
        options.MapInboundClaims.ShouldBeTrue();
    }

    [Fact]
    public void Metadata_over_plain_http_is_a_development_affordance_only()
    {
        // §14.1's Keycloak is plain HTTP, so only Development may fetch signing keys without TLS.
        Options(Environments.Development).RequireHttpsMetadata.ShouldBeFalse();
        Options(Environments.Production).RequireHttpsMetadata.ShouldBeTrue();

        // TelemetryHost's environment is neither: anything not Development requires HTTPS.
        Options().RequireHttpsMetadata.ShouldBeTrue();
    }

    [Fact]
    public void The_revocation_bound_is_the_lifetime_plus_the_skew()
    {
        // ADR-033's number, pinned as the sum so a moved term cannot leave a stale literal.
        AuthenticationExtensions.RevocationBound.ShouldBe(
            AuthenticationExtensions.AccessTokenLifetime +
            Options().TokenValidationParameters.ClockSkew);

        AuthenticationExtensions.RevocationBound.ShouldBe(TimeSpan.FromSeconds(330));
    }

    [Fact]
    public async Task A_token_with_more_life_left_than_the_bound_is_refused()
    {
        // ADR-040's control, which ADR-042's check of the realm at rollout does not replace.
        TokenValidatedContext context = Validated(
            AuthenticationExtensions.RevocationBound + TimeSpan.FromSeconds(30));

        await context.Options.Events.OnTokenValidated(context);

        context.Result.ShouldNotBeNull();
        context.Result.Failure.ShouldNotBeNull();
        context.Result.Failure.Message.ShouldContain("revocation bound");
    }

    [Fact]
    public async Task A_token_inside_the_bound_is_accepted()
    {
        // The companion, without which a handler refusing every token would pass the test above.
        TokenValidatedContext context = Validated(AuthenticationExtensions.AccessTokenLifetime);

        await context.Options.Events.OnTokenValidated(context);

        // Null rather than a success: only Fail sets a result at this point.
        context.Result.ShouldBeNull();
    }

    [Fact]
    public async Task A_token_exactly_at_the_bound_is_accepted_and_a_second_more_is_not()
    {
        // Both sides of `<=`, from one frozen instant, so the boundary cannot drift under the test.
        TokenValidatedContext atBound = Validated(AuthenticationExtensions.RevocationBound);
        await atBound.Options.Events.OnTokenValidated(atBound);
        atBound.Result.ShouldBeNull("the comparison is `<=`, so the bound itself is admitted");

        TokenValidatedContext overBound =
            Validated(AuthenticationExtensions.RevocationBound + TimeSpan.FromSeconds(1));
        await overBound.Options.Events.OnTokenValidated(overBound);
        overBound.Result.ShouldNotBeNull();
        overBound.Result.Failure.ShouldNotBeNull("one second past the bound is past it");
    }

    [Fact]
    public void The_longest_window_this_guard_admits_is_the_bound_plus_the_skew()
    {
        // ADR-040's trade: the skew is spent twice, so this asserts the sum rather than ADR-033's bound.
        TimeSpan skew = Options().TokenValidationParameters.ClockSkew;

        (AuthenticationExtensions.RevocationBound + skew).ShouldBe(TimeSpan.FromSeconds(360));

        AuthenticationExtensions.RevocationBound.ShouldBeGreaterThan(
            AuthenticationExtensions.AccessTokenLifetime,
            "the ceiling has to exceed the lifetime by the drift a lagging host reads into it, " +
            "or a correct realm's tokens are refused");
    }

    /// <summary>A built <c>OnTokenValidated</c> context whose token has <paramref name="remaining"/> to live.</summary>
    private static TokenValidatedContext Validated(TimeSpan remaining)
    {
        JwtBearerOptions options = Options();

        // The scheme's own clock, frozen, since the handler reads `context.Options.TimeProvider`.
        FrozenClock clock = new(DateTimeOffset.UtcNow);
        options.TimeProvider = clock;

        return new TokenValidatedContext(
            new DefaultHttpContext(),
            new AuthenticationScheme(
                JwtBearerDefaults.AuthenticationScheme,
                displayName: null,
                handlerType: typeof(JwtBearerHandler)),
            options)
        {
            SecurityToken = new TokenExpiringIn((clock.GetUtcNow() + remaining).UtcDateTime),
            Principal = new ClaimsPrincipal(new ClaimsIdentity())
        };
    }

    /// <summary>A token with a fixed expiry; the signing members throw if read.</summary>
    private sealed class TokenExpiringIn(DateTime expires) : SecurityToken
    {
        public override string Id => nameof(TokenExpiringIn);

        public override string Issuer => TelemetryHost.Authority;

        public override SecurityKey SecurityKey => throw new NotSupportedException();

        public override SecurityKey SigningKey
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override DateTime ValidFrom =>
            ValidTo - AuthenticationExtensions.AccessTokenLifetime;

        // A fixed instant, since one recomputed per access moves nearer on every read.
        public override DateTime ValidTo { get; } = expires;
    }

    /// <summary>A clock that does not move, so a boundary is a boundary.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

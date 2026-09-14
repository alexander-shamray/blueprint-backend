using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Common.Web;

/// <summary>
/// §11.3's JWT bearer registration, composed by <c>AddCommonWebDefaults</c>
/// rather than called by a host. Every service validates the token itself,
/// because anything reaching it by a path other than the gateway would
/// otherwise be unauthenticated (§11.2).
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// The audience every host validates: one for the whole platform (§11.5),
    /// and a constant rather than configuration because it is the same string
    /// in every environment, which is §15.4's test for an options member.
    /// </summary>
    public const string Audience = "commerce-api";

    /// <summary>The configuration key the authority is read from (§14.1, §15.4).</summary>
    public const string AuthorityKey = "Identity:Authority";

    /// <summary>
    /// §11.3's access-token lifetime — 300 seconds, normative for the platform
    /// and the larger of the two terms in ADR-033's revocation bound.
    /// </summary>
    /// <remarks>
    /// Declared once because <see cref="RevocationBound"/> is composed from it
    /// and the realm assertion reads the same value, so the control and the
    /// assertion cannot disagree (ADR-040).
    /// </remarks>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromSeconds(300);

    /// <summary>
    /// The drift a lifetime check absorbs by accepting a token until <c>exp</c>
    /// plus this — thirty seconds, and the smaller term of the same bound.
    /// </summary>
    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ADR-033's revocation bound: <see cref="AccessTokenLifetime"/> plus
    /// <see cref="AllowedClockSkew"/>, the ceiling every host holds an inbound
    /// token's remaining life to. It bounds what a non-conforming realm costs
    /// without checking the lifetime such a realm issued, because a long-lived
    /// token becomes admissible as it approaches expiry (ADR-040).
    /// </summary>
    /// <remarks>
    /// Composed rather than written down, because a sum written as a literal
    /// is two numbers with one place to drift.
    /// </remarks>
    public static TimeSpan RevocationBound => AccessTokenLifetime + AllowedClockSkew;

    public static IHostApplicationBuilder AddJwtAuthentication(this IHostApplicationBuilder builder)
    {
        // Read eagerly and throw naming the key, the posture AddSqlServer and
        // AddMassTransitMessaging take, so a host that cannot name its identity
        // provider does not start. Not an options type with ValidateOnStart:
        // §15.4 makes ServiceIdentityOptions the only one and argues why.
        //
        // Blank counts as missing: an environment variable set to the empty
        // string reaches Configuration as "" rather than null, so a null-only
        // guard admits Identity__Authority= and hands JwtBearer an authority it
        // cannot build a metadata address from.
        string? configured = builder.Configuration[AuthorityKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is not configured. Every host re-validates inbound tokens (§11.2), " +
                "so one that cannot name its identity provider must refuse to start rather than " +
                "answer the first request without a principal.");
        }

        // Non-blank is not an address: `keycloak:8080/realms/commerce` — a
        // dropped scheme — makes JwtBearer fail when the handler is first
        // resolved, which is during traffic.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which is not an absolute http or https URL. " +
                "It is the base address a discovery document is fetched from (§11.3), so a value " +
                "that cannot be one fails the deployment rather than the first request.");
        }

        // A query or a fragment is absolute, http, and still not a base
        // address: JwtBearer appends `/.well-known/openid-configuration` to
        // this string, and appended to `…/realms/commerce#x` the suffix lands
        // inside the fragment, which is never sent, so the first bearer request
        // would fetch the realm's page instead of the discovery document.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which carries a query or fragment. It is a " +
                "base address that a well-known path is appended to (§11.3), so anything after " +
                "the path makes the discovery document unreachable.");
        }

        // And https everywhere but Development, the rule RequireHttpsMetadata
        // applies below, moved to startup: a host that would refuse to fetch
        // metadata over plain HTTP should not start claiming it will.
        if (!builder.Environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which is plain HTTP outside Development. " +
                "Signing keys fetched over a channel an attacker can rewrite make every " +
                "validation below decorative (§11.3).");
        }

        // A local, because the options lambda below captures it and nullable
        // flow analysis does not reach across that boundary.
        string authority = configured;

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = Audience;

                // Metadata over plain HTTP is a development affordance only —
                // §14.1's Keycloak is http://keycloak:8080. Anywhere else this
                // stops the signing keys being fetched over a channel an
                // attacker can rewrite.
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

                // The framework default, written out because §11.4's subject
                // rule rests on it: Keycloak issues `sub`, ICurrentUser.Id
                // reads ClaimTypes.NameIdentifier, and this is the only thing
                // that turns one into the other. Set to false, every
                // authenticated request throws on a valid token — a failure
                // no test over an injected principal can see.
                options.MapInboundClaims = true;

                // ADR-033's bound, enforced rather than stated (ADR-040). The
                // settings behind it live in a realm every chart points at
                // externally, and a token is the one place the realm's answer
                // is observable at a host without a credential. This contains
                // a non-conforming realm rather than detecting it: a token
                // above the bound is refused until its remaining life is
                // inside it, and detection is ADR-042's gate, which asks the
                // realm. Remaining life against this host's clock rather than
                // `exp - iat`, because `iat` is optional in RFC 7519 and reading
                // it means naming a token type from a package this assembly
                // does not pin; `ValidTo` is on SecurityToken itself, and `exp`
                // is mandatory here because ValidateLifetime refuses a token
                // without one. The ceiling is AccessTokenLifetime plus
                // AllowedClockSkew rather than the lifetime alone, because a
                // host lagging the issuer reads a fresh token as having more
                // than the lifetime left and would refuse what a correct realm
                // issues; the skew is therefore spent twice, once here and once
                // by ValidateLifetime after `exp`, and a conforming token passes
                // because its remaining life is within that ceiling. Refused,
                // not logged, the posture the authority guard above takes: a
                // platform that accepts what it says it does not accept has a
                // decorative guarantee.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        // The scheme's own clock where a host substituted one,
                        // the seam the rest of the authentication stack reads
                        // its time from, so a test moves it once.
                        TimeProvider clock = context.Options.TimeProvider ?? TimeProvider.System;

                        // SpecifyKind rather than a plain conversion: ValidTo is
                        // a DateTime whose Kind the handler is not contracted to
                        // set, and DateTimeOffset reads an Unspecified one as
                        // local — which on a host east of UTC would subtract
                        // hours from the remaining life and pass everything.
                        DateTimeOffset expires =
                            new(DateTime.SpecifyKind(context.SecurityToken.ValidTo, DateTimeKind.Utc));

                        if (expires - clock.GetUtcNow() <= RevocationBound)
                            return Task.CompletedTask;

                        // Two causes, and naming only the first sends an
                        // operator to change a realm that is correct: an issuer
                        // running more than the skew ahead of this host's clock
                        // makes a conforming token look long-lived here.
                        context.Fail(
                            $"The token has more than {RevocationBound.TotalSeconds} seconds of life " +
                            "left, which is longer than the revocation bound this platform states " +
                            "(ADR-033). Either the realm that issued it sets an access-token " +
                            "lifetime, or a client-level override, above what §11.3 requires — or " +
                            "this host's clock is running behind the issuer's by more than the " +
                            "skew, which makes a conforming token read as a long-lived one. Check " +
                            "the clocks before changing the realm.");

                        return Task.CompletedTask;
                    }
                };

                // Assigned whole, with the four Validate* flags written out at
                // their defaults, because this block is the checklist a reader
                // audits and a default is not a visible decision. The
                // post-configure step still fills ValidAudience from Audience
                // and the issuer from the discovery document.
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // The default is five minutes, which keeps an expired token
                    // working for five minutes past its own exp; thirty seconds
                    // absorbs real drift between NTP-synced hosts and nothing
                    // else (§11.3). It says nothing about revocation: a token
                    // revoked at the provider stays valid here until it
                    // expires. The field rather than a literal, because
                    // RevocationBound above is a sum of it.
                    ClockSkew = AllowedClockSkew,

                    // A display name, for logs and audit lines, not the subject:
                    // NameIdentifier stays the stable identifier ICurrentUser.Id
                    // reads, and keying a record on Identity.Name would break
                    // the first time somebody changed their username (§11.4).
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            });

        return builder;
    }
}

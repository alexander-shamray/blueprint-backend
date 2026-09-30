using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Common.Web;

/// <summary>§11.3's JWT bearer registration; every service validates the token itself (§11.2).</summary>
public static class AuthenticationExtensions
{
    public const string Audience = "commerce-api";

    /// <summary>The configuration key the authority is read from (§14.1, §15.4).</summary>
    public const string AuthorityKey = "Identity:Authority";

    /// <summary>§11.3's access-token lifetime, the larger term of ADR-033's revocation bound.</summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromSeconds(300);

    private static readonly TimeSpan AllowedClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>ADR-033's revocation bound, the most remaining life an inbound token may carry (ADR-040).</summary>
    public static TimeSpan RevocationBound => AccessTokenLifetime + AllowedClockSkew;

    public static IHostApplicationBuilder AddJwtAuthentication(this IHostApplicationBuilder builder)
    {
        // Read eagerly so a host that cannot name its identity provider does not start (§11.3).
        string? configured = builder.Configuration[AuthorityKey];

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is not configured. Every host re-validates inbound tokens (§11.2), " +
                "so one that cannot name its identity provider must refuse to start rather than " +
                "answer the first request without a principal.");
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which is not an absolute http or https URL. " +
                "It is the base address a discovery document is fetched from (§11.3), so a value " +
                "that cannot be one fails the deployment rather than the first request.");
        }

        // The well-known path is appended to this string, so a fragment would swallow it.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which carries a query or fragment. It is a " +
                "base address that a well-known path is appended to (§11.3), so anything after " +
                "the path makes the discovery document unreachable.");
        }

        if (!builder.Environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"'{AuthorityKey}' is '{configured}', which is plain HTTP outside Development. " +
                "Signing keys fetched over a channel an attacker can rewrite make every " +
                "validation below decorative (§11.3).");
        }

        // A local, because nullable flow analysis does not reach into the lambda below.
        string authority = configured;

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = Audience;

                // §14.1's Keycloak is served without TLS.
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

                // The default, written out because §11.4's subject rule rests on it (§11.3).
                options.MapInboundClaims = true;

                // ADR-040's control over ADR-033's bound.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        TimeProvider clock = context.Options.TimeProvider ?? TimeProvider.System;

                        // ValidTo's Kind is not contracted, and an Unspecified one would be read as local.
                        DateTimeOffset expires =
                            new(DateTime.SpecifyKind(context.SecurityToken.ValidTo, DateTimeKind.Utc));

                        if (expires - clock.GetUtcNow() <= RevocationBound)
                            return Task.CompletedTask;

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

                // The Validate* flags are written out at their defaults, so the block reads as a checklist.
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    // Below the framework's default (§11.3).
                    ClockSkew = AllowedClockSkew,

                    // A display name; the subject stays NameIdentifier (§11.4).
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            });

        return builder;
    }
}

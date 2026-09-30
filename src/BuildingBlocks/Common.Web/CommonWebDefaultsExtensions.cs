using Common.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Common.Web;

/// <summary>What every host needs identically, in the one call each <c>Program.cs</c> makes (§13.2).</summary>
public static class CommonWebDefaultsExtensions
{
    public static IHostApplicationBuilder AddCommonWebDefaults(this IHostApplicationBuilder builder)
    {
        builder.AddObservability();                            // §13.2
        builder.AddJwtAuthentication();                        // §11.3

        // The fallback policy makes authorization deny-by-default (ADR-030).
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy("authenticated", p => p.RequireAuthenticatedUser())
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // §11.4's port, with the accessor ASP.NET Core does not register by default.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        builder.Services.AddCommonProblemDetails();            // §10.5

        // Liveness only; readiness checks need connection strings a service owns (§13.5).
        builder.Services.AddHealthChecks();

        return builder;
    }
}

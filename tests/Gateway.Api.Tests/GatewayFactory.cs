using Common.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Tests;

/// <summary>The real gateway over the shipped <c>appsettings.json</c> (§12.4), reaching no destination.</summary>
/// <remarks>A request that must cross the proxy takes <see cref="StubbedGatewayFactory"/> instead.</remarks>
public class GatewayFactory : WebApplicationFactory<Program>
{
    /// <summary>The authority every host over this <c>Program</c> must name (§11.3).</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>Configuration layered over the shipped <c>appsettings.json</c>.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string>> AdditionalSettings => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(AuthenticationExtensions.AuthorityKey, UnreachableAuthority);

        foreach ((string key, string value) in AdditionalSettings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(ConfigureAuthentication);
    }

    /// <summary>Swaps the JWT scheme for <see cref="TestAuthHandler"/> (§12.4), fetching no OIDC metadata.</summary>
    /// <remarks>Forbid falls back to the challenge scheme, so <see cref="TestAuthHandler"/> answers the 403.</remarks>
    private static void ConfigureAuthentication(IServiceCollection services)
    {
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
            o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
        });

        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
    }
}

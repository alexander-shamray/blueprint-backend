using Common.Infrastructure.Identity;
using Common.Web;
using Grpc.Net.ClientFactory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Web.Bff.Tests;

/// <summary>
/// The real BFF host (§12.4), with two things stood in for: the identity
/// provider and Catalog.
/// </summary>
/// <remarks>
/// <para>
/// The authority is fake and unreachable for <c>CatalogApiFactory</c>'s
/// reason — <c>.invalid</c> is reserved and never resolves, so a test that
/// accidentally dials an identity provider fails loudly rather than slowly. It
/// is required rather than optional because <c>AddJwtAuthentication</c> reads
/// the key eagerly and throws naming it (§11.3).
/// </para>
/// <para>
/// <b>The three <c>Identity:Client</c> values are supplied because
/// <c>ValidateOnStart</c> means the host will not boot without them</b>, and
/// they are unmistakably fake for §15.4's reason: the fixture is the one
/// environment where the correct value is a fake, because a test that passes
/// with a real secret in it is a test that will one day be run against
/// something real. <see cref="OptionsValidationTests"/> is the suite that
/// removes them on purpose.
/// </para>
/// </remarks>
public class BffFactory : WebApplicationFactory<Program>
{
    /// <summary>The authority every host over this <c>Program</c> must name (§11.3).</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The scope the fixture's credentials ask for (§11.5).</summary>
    public const string Scope = "commerce-api";

    /// <summary>
    /// Where the pricing client should point. Left null the host keeps
    /// <c>PricingHop.Address</c>, which resolves to nothing outside a Compose
    /// network — correct for the tests that must not reach Catalog at all.
    /// </summary>
    public Uri? PricingAddress { get; set; }

    /// <summary>
    /// The token source the credential handler draws on, replacing
    /// <see cref="CachingTokenClient"/> so no test needs a provider to prove
    /// what the handler does with a token.
    /// </summary>
    public RecordingTokenCache Tokens { get; } = new();

    /// <summary>
    /// Configuration layered over the host's own, so a subclass can take a
    /// setting away as well as add one.
    /// </summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
        new($"{ServiceIdentityOptions.SectionName}:ClientId", "web-bff-test"),
        new($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret"),
        new($"{ServiceIdentityOptions.SectionName}:Scope", Scope)
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach ((string key, string? value) in Settings)
            builder.UseSetting(key, value);

        builder.ConfigureServices(services =>
        {
            ConfigureAuthentication(services);

            services.RemoveAll<ITokenCache>();
            services.AddSingleton<ITokenCache>(Tokens);

            // Configured after the host's own AddGrpcClient, so this wins —
            // named options apply in registration order. The alternative would
            // be a configuration key for the address, and §15.4's rule is that
            // a value which does not differ between environments is not
            // configuration; adding one so that a test could reach a stub
            // would be the test dictating the deployment surface.
            if (PricingAddress is not null)
            {
                services.Configure<GrpcClientFactoryOptions>(
                    PricingHop.ClientName,
                    o => o.Address = PricingAddress);
            }
        });
    }

    /// <summary>
    /// Replaces the JWT scheme with <see cref="TestAuthHandler"/> (§12.4).
    /// Replacing rather than configuring: the alternative is a fixture that
    /// fetches OIDC metadata from an authority that is unreachable on purpose.
    /// </summary>
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

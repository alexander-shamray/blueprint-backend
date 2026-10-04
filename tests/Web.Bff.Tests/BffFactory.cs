using Common.Infrastructure.Identity;
using Common.Infrastructure.Messaging;
using Common.Web;
using Grpc.Net.ClientFactory;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Web.Bff.Tests;

/// <summary>The real BFF host (§12.4), with the identity provider and Catalog stood in for.</summary>
/// <remarks>Fake <c>Identity:Client</c> values, as <c>ValidateOnStart</c> needs them to boot (§15.4).</remarks>
public class BffFactory : WebApplicationFactory<Program>
{
    /// <summary>The authority every host over this <c>Program</c> must name (§11.3).</summary>
    public const string UnreachableAuthority = "https://identity.invalid/realms/test";

    /// <summary>The scope the fixture's credentials ask for (§11.5).</summary>
    public const string Scope = "commerce-api";

    /// <summary>A server that does not resolve, so a route that queries it fails and no other route notices.</summary>
    public const string UnreachableDatabase =
        "Server=tcp:sql.invalid,1433;Database=Bff;Encrypt=False;Connect Timeout=1";

    public Uri? PricingAddress { get; set; }

    /// <summary>The runtime key (§7.1), which <c>BffServiceFixture</c> points at its container.</summary>
    public string DatabaseConnectionString { get; set; } = UnreachableDatabase;

    /// <summary>The credential handler's token source, in place of <see cref="CachingTokenClient"/>.</summary>
    public RecordingTokenCache Tokens { get; } = new();

    /// <summary>Configuration over the host's own, so a subclass can take a setting away as well as add one.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
        new($"{ServiceIdentityOptions.SectionName}:ClientId", "web-bff-test"),
        new($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret"),
        new($"{ServiceIdentityOptions.SectionName}:Scope", Scope),
        new("ConnectionStrings:Bff", DatabaseConnectionString)
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

            // §9.5's purge, matched by the ImplementationType AddHostedService<T> sets, so a test drives each pass.
            ServiceDescriptor purge = services.Single(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(RetentionPurgeService));
            services.Remove(purge);

            services.AddSingleton<RetentionPurgeService>();

            // After the host's AddGrpcClient, so this wins; the address is not configuration (§15.4).
            if (PricingAddress is not null)
            {
                services.Configure<GrpcClientFactoryOptions>(
                    PricingHop.ClientName,
                    o => o.Address = PricingAddress);
            }
        });
    }

    /// <summary>Swaps the JWT scheme for <see cref="TestAuthHandler"/> (§12.4), fetching no OIDC metadata.</summary>
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

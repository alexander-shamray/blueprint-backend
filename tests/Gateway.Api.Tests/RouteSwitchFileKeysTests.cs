using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>The route switch file is layered over the route file, so any key in it but a route's
/// <c>Suspended</c> entry would change routing or authorisation live; only that entry reaches the configuration
/// (ADR-187).</summary>
public sealed class RouteSwitchFileKeysTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("route-switch-keys-").FullName;

    private string FilePath => Path.Combine(_directory, "routes.json");

    [Fact]
    public void A_routes_suspended_entry_binds_and_every_other_key_in_the_file_is_left_out()
    {
        File.WriteAllText(FilePath, """
            {
              "ReverseProxy": {
                "Routes": {
                  "orders": {
                    "Metadata": { "Suspended": "true", "Other": "x" },
                    "AuthorizationPolicy": "anonymous",
                    "ClusterId": "elsewhere",
                    "Match": { "Path": "/{**catch-all}" }
                  }
                },
                "Clusters": { "ordering": { "Destinations": { "d1": { "Address": "http://elsewhere" } } } }
              },
              "Authentication": { "Authority": "https://elsewhere" }
            }
            """);

        ConfigurationManager configuration = Build();

        configuration[$"ReverseProxy:Routes:orders:Metadata:{SuspendedRoute.MetadataKey}"].ShouldBe("true");
        configuration["ReverseProxy:Routes:orders:Metadata:Other"].ShouldBeNull();
        configuration["ReverseProxy:Routes:orders:AuthorizationPolicy"].ShouldBe("orders:write");
        configuration["ReverseProxy:Routes:orders:ClusterId"].ShouldBe("ordering");
        configuration["ReverseProxy:Routes:orders:Match:Path"].ShouldBe("/api/orders/{**catch-all}");
        configuration["ReverseProxy:Clusters:ordering:Destinations:d1:Address"].ShouldBe("http://ordering");
        configuration["Authentication:Authority"].ShouldBe("https://keycloak");
    }

    [Fact]
    public void A_reload_that_brings_another_key_changes_only_the_suspended_entry()
    {
        File.WriteAllText(FilePath, """
            { "ReverseProxy": { "Routes": { "orders": { "Metadata": { "Suspended": "true" } } } } }
            """);
        ConfigurationManager configuration = Build();

        File.WriteAllText(FilePath, """
            {
              "ReverseProxy": {
                "Routes": { "orders": { "Metadata": { "Suspended": "false" }, "AuthorizationPolicy": "anonymous" } }
              }
            }
            """);
        ((IConfigurationRoot)configuration).Reload();

        configuration[$"ReverseProxy:Routes:orders:Metadata:{SuspendedRoute.MetadataKey}"].ShouldBe("false");
        configuration["ReverseProxy:Routes:orders:AuthorizationPolicy"].ShouldBe("orders:write");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ConfigurationManager Build()
    {
        ConfigurationManager configuration = new();
        configuration.AddInMemoryCollection(
        [
            new("ReverseProxy:Routes:orders:AuthorizationPolicy", "orders:write"),
            new("ReverseProxy:Routes:orders:ClusterId", "ordering"),
            new("ReverseProxy:Routes:orders:Match:Path", "/api/orders/{**catch-all}"),
            new("ReverseProxy:Clusters:ordering:Destinations:d1:Address", "http://ordering"),
            new("Authentication:Authority", "https://keycloak"),
            new(RouteSwitchFile.PathKey, FilePath)
        ]);
        configuration.AddRouteSwitchFile();

        return configuration;
    }
}

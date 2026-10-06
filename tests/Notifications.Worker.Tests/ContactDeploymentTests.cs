using System.Text.Json;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The realm and the Compose unit hold one client, one secret, one scope and one realm (§11.5).</summary>
/// <remarks>An unknown client id passes <c>ValidateOnStart</c> and fails every contact read (§15.4).</remarks>
public sealed class ContactDeploymentTests
{
    private static string File(params string[] path) =>
        System.IO.File.ReadAllText(Path.Combine([RepositoryRoot.Locate(), .. path]));

    private static string Unit() => File("deploy", "compose", "services", "notifications.yml");

    private static JsonElement Realm() =>
        JsonDocument.Parse(File("deploy", "compose", "keycloak", "realm-export.json")).RootElement;

    private static JsonElement Client() =>
        Realm()
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == KeycloakFixture.ContactClient);

    [Fact]
    public void The_realm_and_the_deployment_hold_the_same_secret()
    {
        string secret = Client().GetProperty("secret").GetString()!;

        Unit().ShouldContain($"Identity__Client__ClientSecret: \"${{NOTIFICATIONS_CLIENT_SECRET:-{secret}}}\"");
    }

    [Fact]
    public void The_deployment_names_the_client_the_scope_and_the_realm_the_export_holds()
    {
        string unit = Unit();

        unit.ShouldContain($"Identity__Client__ClientId: \"{KeycloakFixture.ContactClient}\"");
        unit.ShouldContain($"Identity__Client__Scope: \"{NotificationsWorkerFactory.ContactScope}\"");
        unit.ShouldContain($"ContactSource__Realm: \"{Realm().GetProperty("realm").GetString()}\"");
    }

    [Fact]
    public void The_scope_the_deployment_requests_is_one_the_client_holds()
    {
        // Keycloak refuses a scope the client does not hold with invalid_scope, at the first read.
        string[] held =
        [
            .. Client()
                .GetProperty("defaultClientScopes")
                .EnumerateArray()
                .Concat(Client().GetProperty("optionalClientScopes").EnumerateArray())
                .Select(s => s.GetString()!)
        ];

        held.ShouldContain(NotificationsWorkerFactory.ContactScope);
    }
}

using System.Text.Json;
using Common.Web;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The realm's <c>web-bff</c> client, read from the host that owns the client id.</summary>
/// <remarks>An unknown client id passes <c>ValidateOnStart</c> and fails every pricing call (§15.4).</remarks>
public class RealmClientTests
{
    /// <summary>The client id the BFF authenticates as, set by Compose (§14.1) and Helm (§15.4).</summary>
    private const string ClientId = "web-bff";

    private static readonly JsonDocument Realm = JsonDocument.Parse(
        File.ReadAllText(RepositoryFile.Locate(RepositoryFile.RealmExport)));

    private static JsonElement Client => Realm.RootElement
        .GetProperty("clients")
        .EnumerateArray()
        .Single(c => c.GetProperty("clientId").GetString() == ClientId);

    [Fact]
    public void The_realm_holds_the_client_the_BFF_authenticates_as()
    {
        Client.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        Client.GetProperty("protocol").GetString().ShouldBe("openid-connect");
    }

    [Fact]
    public void It_is_confidential_with_service_accounts_enabled()
    {
        // The grant needs both: a public client has no secret, and Keycloak refuses one without service accounts.
        Client.GetProperty("publicClient").GetBoolean().ShouldBeFalse();
        Client.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void No_flow_can_obtain_a_token_as_a_person_through_it()
    {
        // A leaked secret must reach the pricing hop only, never a token for a person.
        Client.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeFalse();
        Client.GetProperty("directAccessGrantsEnabled").GetBoolean().ShouldBeFalse();
        Client.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void The_audience_scope_is_a_DEFAULT_scope_rather_than_an_optional_one()
    {
        string[] defaults =
        [
            .. Client
                .GetProperty("defaultClientScopes")
                .EnumerateArray()
                .Select(s => s.GetString()!)
        ];

        // §11.5's trap: a client-credentials token requests no scope, so an optional one is silently absent.
        defaults.ShouldContain(AuthenticationExtensions.Audience);

        // And not both, which Keycloak's admin console allows.
        string[] optional =
        [
            .. Client
                .GetProperty("optionalClientScopes")
                .EnumerateArray()
                .Select(s => s.GetString()!)
        ];

        optional.ShouldNotContain(AuthenticationExtensions.Audience);
    }

    [Fact]
    public void The_realm_and_the_deployment_hold_the_same_secret()
    {
        string secret = Client.GetProperty("secret").GetString()!;
        string compose = File.ReadAllText(RepositoryFile.Locate(RepositoryFile.ComposeFile));

        // Two files holding one string; no message, as ShouldContain(string, string) binds to the char overload.
        compose.ShouldContain($"Identity__Client__ClientSecret: \"${{BFF_CLIENT_SECRET:-{secret}}}\"");
    }

    [Fact]
    public void The_deployment_names_the_client_and_the_scope_the_realm_holds()
    {
        string compose = File.ReadAllText(RepositoryFile.Locate(RepositoryFile.ComposeFile));

        compose.ShouldContain($"Identity__Client__ClientId: \"{ClientId}\"");

        // The scope, whose audience the realm grants only as a default client scope.
        compose.ShouldContain($"Identity__Client__Scope: \"{AuthenticationExtensions.Audience}\"");
    }

    /// <summary>The second client ADR-052 mints, and the reader of Ordering's address.</summary>
    private const string WorkerClient = "shipping-worker";

    [Fact]
    public void The_service_account_clients_are_exactly_the_hosts_that_call_a_peer()
    {
        string[] serviceAccounts =
        [
            .. Realm.RootElement
                .GetProperty("clients")
                .EnumerateArray()
                .Where(c =>
                    c.TryGetProperty("serviceAccountsEnabled", out JsonElement enabled) &&
                    enabled.GetBoolean())
                .Select(c => c.GetProperty("clientId").GetString()!)
        ];

        // Each secret holder is a synchronous coupling (§11.5), so a client ADR-052 did not decide fails.
        serviceAccounts.ShouldBe([ClientId, WorkerClient], ignoreOrder: true);
    }
}

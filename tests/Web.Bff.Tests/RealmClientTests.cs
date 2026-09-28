using System.Text.Json;
using Common.Web;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// The realm's <c>web-bff</c> client, read from the side that owns the client
/// id — PR-17's <c>GrantablePermissionTests</c> shape, one host over.
/// </summary>
/// <remarks>
/// <b>PR-17 learned this the expensive way and the lesson transfers exactly.</b>
/// It registered <c>inventory:admin</c> on a route without adding the role to
/// the realm, so the path was 403 for every principal Keycloak could issue —
/// not a wrong answer a test would catch, a path nobody could reach. The BFF's
/// version of that mistake is a <c>ClientId</c> in Compose that the realm has
/// never heard of: <c>ValidateOnStart</c> is satisfied because the value is
/// present, the host boots, and every pricing call fails at the token endpoint.
/// <para>
/// <c>Common.Web.Tests</c>' <c>RealmImportTests</c> cannot see this: it is a
/// building block's suite and may not reference a host to read its constants,
/// which is why its own closed-set assertions compare against literals. So the
/// check lives with the value it is checking, here.
/// </para>
/// </remarks>
public class RealmClientTests
{
    /// <summary>
    /// The client id the BFF authenticates as. The same string Compose sets as
    /// <c>Identity__Client__ClientId</c> (§14.1) and Helm as
    /// <c>identity.clientId</c> (§15.4).
    /// </summary>
    /// <remarks>
    /// A constant here rather than in <c>src</c>, deliberately, and the
    /// asymmetry with <c>GatewayPermissions</c> is worth stating: a permission
    /// name is compiled into a policy, so it earns a constant in the host. A
    /// client id is never named in code — <c>ServiceIdentityOptions</c> reads it
    /// from configuration, because it is the one value that legitimately
    /// differs per environment. What has to agree is the deployment and the
    /// realm, and both of those are files this test can read.
    /// </remarks>
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
        // The client-credentials grant needs both: a public client has no
        // secret to present, and without service accounts Keycloak refuses the
        // grant outright with unauthorized_client.
        Client.GetProperty("publicClient").GetBoolean().ShouldBeFalse();
        Client.GetProperty("serviceAccountsEnabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void No_flow_can_obtain_a_token_as_a_person_through_it()
    {
        // The negative half, and the one that matters most. This client holds
        // a secret that is in a Compose file and, in production, in a vault
        // mount — so the blast radius of it leaking must be "the pricing hop",
        // not "a token for any user in the realm". Direct access grants would
        // make it the second.
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

        // §11.5's trap, stated as an assertion. A client-credentials token
        // requests no scope explicitly, so a scope left OPTIONAL is silently
        // absent — the audience mapper never runs, the token carries
        // aud: account, and Catalog rejects the platform's only permitted
        // synchronous hop at the one moment there is no user to blame it on.
        defaults.ShouldContain(AuthenticationExtensions.Audience);

        // And not both, which is a state Keycloak's admin console will let
        // somebody create and which resolves in the wrong direction.
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

        // The client-credentials grant is two parties holding one string, and
        // the two parties are two files in this repository. Neither half fails
        // on its own: the realm imports, the host boots, ValidateOnStart is
        // satisfied because a value is present — and every pricing call is
        // refused at the token endpoint with unauthorized_client, which reads
        // as Catalog's fault from the BFF's logs.
        //
        // This is the class of defect PR-17 named as the repository's most
        // reliable one: a change that lands in one of two files that have to
        // agree. Nothing else in the solution can see this pair, because
        // Common.Web.Tests is a building block's suite and may not read a
        // host's deployment.
        // No custom message on these: Shouldly resolves ShouldContain(string,
        // string) to the IEnumerable<char> overload, so the second argument
        // would be read as a predicate and not compile. The comments carry the
        // argument instead.
        compose.ShouldContain($"Identity__Client__ClientSecret: \"${{BFF_CLIENT_SECRET:-{secret}}}\"");
    }

    [Fact]
    public void The_deployment_names_the_client_and_the_scope_the_realm_holds()
    {
        string compose = File.ReadAllText(RepositoryFile.Locate(RepositoryFile.ComposeFile));

        compose.ShouldContain($"Identity__Client__ClientId: \"{ClientId}\"");

        // And the scope, which is the third of the three §15.4 marks BFF-only.
        // A scope the realm does not assign as a DEFAULT client scope produces
        // a token with no audience — the assertion above this one — so these
        // two tests are the same fact read from opposite ends.
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

        // §11.5 makes the number of hosts holding a client secret the number of
        // synchronous couplings in the platform, and ADR-052 moved it from one
        // to two by deciding what the second one reads when it is stolen. Over-
        // supply has no failing test to catch it — which is what this is — so a
        // third name appearing here is a third coupling or a credential nothing
        // sends. Notifications' client is decided and not built.
        serviceAccounts.ShouldBe([ClientId, WorkerClient], ignoreOrder: true);
    }
}

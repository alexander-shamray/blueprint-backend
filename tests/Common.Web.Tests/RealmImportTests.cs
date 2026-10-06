using System.Globalization;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>§14.1's realm against the validation constants, since §11.5's gaps compile the same.</summary>
/// <remarks>
/// A file test rather than a live Keycloak, since what a realm gets wrong statically needs no container;
/// a deployed realm is judged at rollout instead (ADR-042), so a green run here says nothing of one.
/// </remarks>
public class RealmImportTests
{
    private const string Audience = AuthenticationExtensions.Audience;

    /// <summary>The browser client the compose README's login names.</summary>
    private const string TokenClient = "web-app";

    private static readonly JsonDocument Realm = JsonDocument.Parse(
        File.ReadAllText(RepositoryFile("deploy/compose/keycloak/realm-export.json")));

    private static JsonElement Root => Realm.RootElement;

    private static JsonElement.ArrayEnumerator ClientScopes =>
        Root.GetProperty("clientScopes").EnumerateArray();

    private static JsonElement CommerceApiScope =>
        ClientScopes.Single(s => s.GetProperty("name").GetString() == Audience);

    private static JsonElement.ArrayEnumerator MappersOf(JsonElement scope) =>
        scope.GetProperty("protocolMappers").EnumerateArray();

    /// <summary>Every assertion here is about a JWT, which a switch to <c>saml</c> would leave green.</summary>
    private const string Protocol = "openid-connect";

    [Fact]
    public void Every_part_of_the_token_path_speaks_openid_connect()
    {
        JsonElement tokenClient = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient.GetProperty("protocol").GetString().ShouldBe(Protocol);
        CommerceApiScope.GetProperty("protocol").GetString().ShouldBe(Protocol);

        foreach (JsonElement mapper in MappersOf(CommerceApiScope))
        {
            mapper.GetProperty("protocol").GetString().ShouldBe(
                Protocol,
                $"'{mapper.GetProperty("name").GetString()}' writes into a token this platform reads");
        }
    }

    [Fact]
    public void The_realm_is_the_one_every_host_is_pointed_at()
    {
        // §14.1's Identity__Authority names this realm on every service block.
        Root.GetProperty("realm").GetString().ShouldBe("commerce");
        Root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void An_audience_mapper_puts_the_value_every_service_validates_into_aud()
    {
        // §11.3 validates Audience, and this mapper is the only thing that puts it in `aud` (§11.5).
        JsonElement mapper = MappersOf(CommerceApiScope).Single(
            m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper");

        mapper
            .GetProperty("config")
            .GetProperty("included.client.audience")
            .GetString()
            .ShouldBe(Audience);
        mapper
            .GetProperty("config")
            .GetProperty("access.token.claim")
            .GetString()
            .ShouldBe("true", "an audience on the id token alone is invisible to a bearer check");
    }

    [Fact]
    public void A_role_mapper_writes_the_claim_the_policies_read()
    {
        // The one writer of PermissionClaim.Type that cannot reference the constant.
        JsonElement mapper = MappersOf(CommerceApiScope).Single(
            m => m.GetProperty("name").GetString() == PermissionClaim.Type);

        JsonElement config = mapper.GetProperty("config");
        config.GetProperty("claim.name").GetString().ShouldBe(PermissionClaim.Type);
        config.GetProperty("access.token.claim").GetString().ShouldBe("true");
        config
            .GetProperty("multivalued")
            .GetString()
            .ShouldBe("true", "a single-valued claim silently keeps one permission and drops the rest");

        // Client roles, since a realm-role mapper also emits Keycloak's own roles here (§11.5).
        mapper.GetProperty("protocolMapper").GetString().ShouldBe("oidc-usermodel-client-role-mapper");
        config.GetProperty("usermodel.clientRoleMapping.clientId").GetString().ShouldBe(Audience);
    }

    [Fact]
    public void Every_client_holding_the_scope_holds_it_as_a_default()
    {
        // §11.5: a client-credentials token requests no scope, so an optional one is silently absent.
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string? id = client.GetProperty("clientId").GetString();

            bool optional =
                client.TryGetProperty("optionalClientScopes", out JsonElement optionals) &&
                optionals.EnumerateArray().Any(s => s.GetString() == Audience);

            optional.ShouldBeFalse($"'{id}' holds {Audience} as an optional scope, so its tokens will not carry it");
        }

        // Not vacuous: named rather than counted, since Keycloak's built-in clients hold scopes too.
        JsonElement tokenClient = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient
            .GetProperty("defaultClientScopes")
            .EnumerateArray()
            .Select(s => s.GetString())
            .ShouldContain(
                Audience,
                $"'{TokenClient}' does not hold {Audience} as a default scope, so its " +
                "tokens carry no audience this platform accepts");
    }

    [Fact]
    public void The_documented_login_can_actually_be_performed()
    {
        // The compose README's recipe is a password grant against web-app.
        JsonElement tokenClient = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient
            .GetProperty("enabled")
            .GetBoolean()
            .ShouldBeTrue($"a disabled '{TokenClient}' satisfies both flags below and issues nothing");
        tokenClient
            .GetProperty("directAccessGrantsEnabled")
            .GetBoolean()
            .ShouldBeTrue($"the README obtains a token by password grant against '{TokenClient}'");
        tokenClient
            .GetProperty("publicClient")
            .GetBoolean()
            .ShouldBeTrue($"the README's grant sends no secret, and none is committed for '{TokenClient}'");
    }

    [Fact]
    public void The_access_token_lifetime_is_the_one_the_chapter_states()
    {
        // The field rather than a literal, because every host also enforces RevocationBound from it (ADR-040).
        int statedLifetimeSeconds = (int)AuthenticationExtensions.AccessTokenLifetime.TotalSeconds;

        Root.GetProperty("accessTokenLifespan").GetInt32().ShouldBe(
            statedLifetimeSeconds,
            "§11.3 states the access-token lifetime and this realm is what sets it");

        // §11.3's window rests on no client enabling the implicit flow, which has its own lifetime.
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string id = client.GetProperty("clientId").GetString()!;

            client.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse(
                $"'{id}' enables the implicit flow, whose tokens live for " +
                "accessTokenLifespanForImplicitFlow and not for the lifetime §11.3 states");

            // A client attribute beats the realm setting, so the realm value
            // alone does not pin the window.
            if (client.TryGetProperty("attributes", out JsonElement attributes) &&
                attributes.TryGetProperty("access.token.lifespan", out JsonElement over))
            {
                over.GetString().ShouldBe(
                    statedLifetimeSeconds.ToString(CultureInfo.InvariantCulture),
                    $"'{id}' overrides the access-token lifetime, and an override that " +
                    "disagrees with §11.3 moves the window without moving the realm value " +
                    "this test otherwise reads");
            }
        }
    }

    [Fact]
    public void The_browser_is_issued_no_refresh_token()
    {
        // §11.2's flow ends at the browser, which holds no refresh token (ADR-034).
        JsonElement tokenClient = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        // The positive half, without which the refresh-token assertion holds for the wrong reason.
        tokenClient.GetProperty("standardFlowEnabled").GetBoolean().ShouldBeTrue(
            $"'{TokenClient}' is §11.2's authorization-code client, and the refresh-token " +
            "assertion below says nothing about a client that runs no such flow");

        tokenClient
            .GetProperty("attributes")
            .GetProperty("use.refresh.tokens")
            .GetString()
            .ShouldBe(
                "false",
                $"'{TokenClient}' is the browser's client, and Keycloak's default is to issue it " +
                "a refresh token");
    }

    [Fact]
    public void Both_development_logins_hold_exactly_what_the_readme_says()
    {
        // `browser` proves §11.5's refusal only while it holds nothing.
        JsonElement users = Root.GetProperty("users");

        string[] Permissions(string username) =>
        [
            .. users
                .EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == username)
                .GetProperty("clientRoles")
                .GetProperty(Audience)
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        // Not orders:admin, so the ownership 404 stays demonstrable with the logins shipped.
        Permissions("demo").ShouldBe(
            ["catalog:write", "orders:write", "orders:cancel", "inventory:admin", "payments:admin"],
            ignoreOrder: true);

        JsonElement browser = users
            .EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "browser");

        browser
            .TryGetProperty("clientRoles", out JsonElement granted)
            .ShouldBeFalse("'browser' exists to prove a refusal, so it must hold no client role at all");

        // Pinned, because §14.1's development defaults are documented ones.
        foreach (string username in (string[])["demo", "browser"])
        {
            JsonElement user = users
                .EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == username);

            user
                .GetProperty("enabled")
                .GetBoolean()
                .ShouldBeTrue($"'{username}' is one of §11.5's two documented logins");

            JsonElement credential = user
                .GetProperty("credentials")
                .EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "password");

            credential
                .GetProperty("value")
                .GetString()
                .ShouldBe(username, $"the compose README documents '{username}' as its own password");

            credential
                .GetProperty("temporary")
                .GetBoolean()
                .ShouldBeFalse($"a temporary credential makes '{username}' unusable by the README's password grant");
        }
    }

    [Fact]
    public void No_role_description_exceeds_what_keycloak_can_store()
    {
        // Keycloak's ROLE.DESCRIPTION is VARCHAR(255), and the import fails rather than truncating.
        const int keycloakDescriptionLimit = 255;

        (string Name, string Description)[] roles =
        [
            .. Root
                .GetProperty("roles")
                .GetProperty("client")
                .GetProperty(Audience)
                .EnumerateArray()
                .Select(r => (
                    Name: r.GetProperty("name").GetString()!,
                    Description: r.TryGetProperty("description", out JsonElement d) ? d.GetString()! : ""))
        ];

        roles.ShouldNotBeEmpty();

        foreach ((string name, string description) in roles)
        {
            description.Length.ShouldBeLessThanOrEqualTo(
                keycloakDescriptionLimit,
                $"'{name}' has a {description.Length}-character description; Keycloak stores 255 and " +
                "the import fails the whole realm rather than truncating");
        }
    }

    [Fact]
    public void The_permission_vocabulary_is_a_closed_set_of_client_roles()
    {
        // A route's permission (§10.2) obeys an endpoint's rule (§11.4): grantable, not granted.
        string[] roles =
        [
            .. Root
                .GetProperty("roles")
                .GetProperty("client")
                .GetProperty(Audience)
                .EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];

        // The whole set: orders:admin is read by CancelOrderHandler, and no endpoint names it.
        roles.ShouldBe(
            [
                "catalog:write",
                "inventory:admin",
                "payments:admin",
                "orders:write",
                "orders:cancel",
                "orders:admin",
                "orders:delivery-address"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void The_builtin_client_scopes_are_all_present()
    {
        // A realm import treats `clientScopes` as complete, so an omitted built-in is never created.
        string[] builtins = ["basic", "profile", "email", "roles", "web-origins", "acr"];
        string[] names = [.. ClientScopes.Select(s => s.GetProperty("name").GetString()).OfType<string>()];

        foreach (string builtin in builtins)
        {
            names.ShouldContain(
                builtin,
                $"'{builtin}' is a Keycloak built-in; a realm that declares clientScopes " +
                "and omits it never creates it");
        }

        // Declared is not assigned.
        string[] assigned =
        [
            .. Root
                .GetProperty("clients")
                .EnumerateArray()
                .Single(c => c.GetProperty("clientId").GetString() == TokenClient)
                .GetProperty("defaultClientScopes")
                .EnumerateArray()
                .Select(s => s.GetString())
                .OfType<string>()
        ];

        foreach (string builtin in builtins)
        {
            assigned.ShouldContain(
                builtin,
                $"'{TokenClient}' does not receive '{builtin}', so its tokens are missing " +
                "what that scope carries");
        }

        // Assigned is not carrying: `basic`'s mapper must write `sub` into the access token.
        JsonElement basic = ClientScopes.Single(s => s.GetProperty("name").GetString() == "basic");

        JsonElement subject = MappersOf(basic).Single(
            m => m.GetProperty("protocolMapper").GetString() == "oidc-sub-mapper");

        subject
            .GetProperty("config")
            .GetProperty("access.token.claim")
            .GetString()
            .ShouldBe("true", "a `sub` on the id token alone is invisible to a bearer check");
    }

    /// <summary>The BFF's client, whose grant needs a secret both sides agree on (§11.5).</summary>
    private const string CredentialClient = "web-bff";

    private const string DocumentedLocalSecret = "local-dev-secret";

    /// <summary>ADR-052's second credentialed client, and its own default.</summary>
    private const string WorkerCredentialClient = "shipping-worker";

    private const string DocumentedLocalWorkerSecret = "local-dev-shipping-secret";

    /// <summary>ADR-052's contact reader, and its own default.</summary>
    private const string ContactCredentialClient = "notifications-worker";

    private const string DocumentedLocalContactSecret = "local-dev-notifications-secret";

    /// <summary>The client Keycloak's admin roles live on, the contact reader's among them (ADR-052).</summary>
    private const string RealmManagement = "realm-management";

    private static readonly Dictionary<string, string> DocumentedLocalSecrets =
        new(StringComparer.Ordinal)
        {
            [CredentialClient] = DocumentedLocalSecret,
            [WorkerCredentialClient] = DocumentedLocalWorkerSecret,
            [ContactCredentialClient] = DocumentedLocalContactSecret
        };

    [Fact]
    public void No_client_ships_a_secret_but_the_ones_whose_grants_need_one()
    {
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string clientId = client.GetProperty("clientId").GetString()!;
            bool ships = client.TryGetProperty("secret", out JsonElement secret);

            if (!DocumentedLocalSecrets.TryGetValue(clientId, out string? documented))
            {
                // A generated secret is no documented default, and Keycloak regenerates one on import.
                ships.ShouldBeFalse($"'{clientId}' ships a secret and needs none");

                continue;
            }

            ships.ShouldBeTrue(
                $"'{clientId}' authenticates with the client-credentials grant, so the realm and " +
                "the deployment have to hold the same value (§11.5)");

            // The matching half is the host's deployment, which a building block's suite may not read.
            secret.GetString().ShouldBe(
                documented,
                "a secret in a committed realm must be the documented local default, " +
                "never a generated or real one (§11.6)");
        }

        // Not vacuous: every credentialed client is in the realm.
        string[] present =
        [
            .. Root
                .GetProperty("clients")
                .EnumerateArray()
                .Select(c => c.GetProperty("clientId").GetString()!)
        ];

        foreach (string credentialed in DocumentedLocalSecrets.Keys)
            present.ShouldContain(credentialed);
    }

    [Fact]
    public void The_worker_service_account_holds_exactly_the_role_its_grant_names()
    {
        // The mapper reads a service account's roles from its own user, exported with serviceAccountClientId.
        JsonElement account = Root
            .GetProperty("users")
            .EnumerateArray()
            .Single(u => u.TryGetProperty("serviceAccountClientId", out JsonElement client) &&
                client.GetString() == WorkerCredentialClient);

        string[] granted =
        [
            .. account
                .GetProperty("clientRoles")
                .GetProperty(Audience)
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        // Exactly, since ADR-052 sizes this credential by what it reads when stolen.
        granted.ShouldBe(["orders:delivery-address"]);

        // Nor anything beside it, which would widen the same stolen secret.
        string[] clients = [.. account.GetProperty("clientRoles").EnumerateObject().Select(c => c.Name)];

        clients.ShouldBe([Audience]);
        account.TryGetProperty("realmRoles", out _).ShouldBeFalse();
        account.TryGetProperty("groups", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_contact_service_account_holds_exactly_view_users_on_realm_management()
    {
        JsonElement account = Root
            .GetProperty("users")
            .EnumerateArray()
            .Single(u => u.TryGetProperty("serviceAccountClientId", out JsonElement client) &&
                client.GetString() == ContactCredentialClient);

        // One client and one role on it, since ADR-052 sizes this credential by what it reads when stolen.
        string[] clients = [.. account.GetProperty("clientRoles").EnumerateObject().Select(c => c.Name)];
        clients.ShouldBe([RealmManagement]);

        string[] granted =
        [
            .. account
                .GetProperty("clientRoles")
                .GetProperty(RealmManagement)
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        granted.ShouldBe(["view-users"]);
        account.TryGetProperty("realmRoles", out _).ShouldBeFalse();
        account.TryGetProperty("groups", out _).ShouldBeFalse();
    }

    [Fact]
    public void View_users_composes_exactly_the_two_query_roles_and_neither_composes_further()
    {
        // The pinned Keycloak's own composition, exported with the realm: the worker's check reads the expanded set.
        JsonElement[] management = [.. Root
            .GetProperty("roles")
            .GetProperty("client")
            .GetProperty(RealmManagement)
            .EnumerateArray()];

        JsonElement viewUsers = management.Single(r => r.GetProperty("name").GetString() == "view-users");

        string[] composed =
        [
            .. viewUsers
                .GetProperty("composites")
                .GetProperty("client")
                .GetProperty(RealmManagement)
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        composed.ShouldBe(["query-groups", "query-users"], ignoreOrder: true);

        foreach (string role in composed)
        {
            management
                .Single(r => r.GetProperty("name").GetString() == role)
                .GetProperty("composite")
                .GetBoolean()
                .ShouldBeFalse($"'{role}' composing further would widen the grant past the three roles ADR-052 names");
        }
    }

    [Fact]
    public void The_resource_client_can_mint_no_token_of_its_own()
    {
        // Keycloak generates this client a secret on import, harmless only while it has no flow (§11.5).
        JsonElement resource = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == Audience);

        foreach (string flow in (string[])
        [
            "standardFlowEnabled",
            "implicitFlowEnabled",
            "directAccessGrantsEnabled",
            "serviceAccountsEnabled"
        ])
        {
            resource.GetProperty(flow).GetBoolean().ShouldBeFalse(
                $"'{Audience}' owns the permission vocabulary; '{flow}' would let it mint tokens too");
        }
    }

    [Fact]
    public void No_realm_role_grants_a_permission_by_composition()
    {
        // Every user holds default-roles-commerce, so a permission composed into it reaches every token.
        foreach (JsonElement role in Root.GetProperty("roles").GetProperty("realm").EnumerateArray())
        {
            if (!role.TryGetProperty("composites", out JsonElement composites) ||
                !composites.TryGetProperty("client", out JsonElement clients))
            {
                continue;
            }

            clients.TryGetProperty(Audience, out JsonElement granted).ShouldBeFalse(
                $"realm role '{role.GetProperty("name").GetString()}' composes a '{Audience}' role, " +
                "so every user holding it carries that permission");
        }
    }

    /// <summary>Walks up to the directory holding <c>Platform.slnx</c>, whatever the build's depth.</summary>
    private static string RepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Platform.slnx")))
            directory = directory.Parent;

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"No Platform.slnx above '{AppContext.BaseDirectory}', so '{relativePath}' cannot be located.");
        }

        string path = Path.Combine(directory.FullName, relativePath);

        // An absent file fails here rather than as an empty realm that asserts nothing.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}

using System.Globalization;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// The shipped Keycloak realm, read against the constants this assembly
/// validates tokens with — §11.5's audience gap is realm configuration, not
/// code, so nothing compiles differently when the mapper is missing.
/// </summary>
/// <remarks>
/// The realm is §14.1's Compose realm; a deployed realm is judged at rollout
/// by <c>deploy/keycloak/realm_check.py</c> (ADR-042), so a green run here says
/// the local realm holds the shape and never that the platform does (ADR-033,
/// ADR-034). A file test rather than a live Keycloak, because §11.5 assigns
/// the container-backed suite to the client-credentials question and
/// everything a realm can get wrong statically costs no container.
/// </remarks>
public class RealmImportTests
{
    private const string Audience = AuthenticationExtensions.Audience;

    /// <summary>
    /// The browser client the compose README's login names, whose tokens the
    /// assertions below read.
    /// </summary>
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

    /// <summary>
    /// Keycloak speaks more than one protocol, and every assertion in this file
    /// is about a JWT. A client, a scope or a mapper switched to <c>saml</c>
    /// keeps its name, its flags and its config — so every other test here
    /// stays green while the thing they describe stops being an OIDC token.
    /// </summary>
    private const string Protocol = "openid-connect";

    [Fact]
    public void Every_part_of_the_token_path_speaks_openid_connect()
    {
        // Nothing else in this suite reads `protocol`, so this is the one
        // assertion between a realm that issues JWTs and one that issues
        // something no part of this platform can validate.
        JsonElement tokenClient = Root.GetProperty("clients").EnumerateArray()
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
        // §14.1's Identity__Authority is http://keycloak:8080/realms/commerce
        // on every service block. A renamed realm makes every one of them
        // fetch metadata from a 404 and every request 401 — at runtime, in
        // whichever environment imported the new file first.
        Root.GetProperty("realm").GetString().ShouldBe("commerce");
        Root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void An_audience_mapper_puts_the_value_every_service_validates_into_aud()
    {
        // Without this the realm issues tokens with an `aud` of `account` and
        // every service rejects every caller — §11.5's gap, and the reason
        // that section exists. §11.3 validates Audience; this is the only
        // thing that puts it there.
        JsonElement mapper = MappersOf(CommerceApiScope).Single(
            m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper");

        mapper.GetProperty("config").GetProperty("included.client.audience").GetString()
            .ShouldBe(Audience);
        mapper.GetProperty("config").GetProperty("access.token.claim").GetString()
            .ShouldBe("true", "an audience on the id token alone is invisible to a bearer check");
    }

    [Fact]
    public void A_role_mapper_writes_the_claim_the_policies_read()
    {
        // The fourth party to PermissionClaim.Type, and the one that cannot
        // reference the constant. A mapper writing "permissions" or "roles"
        // leaves every policy in the platform unsatisfiable, with nothing in
        // the solution compiling differently and every unit test still green.
        JsonElement mapper = MappersOf(CommerceApiScope).Single(
            m => m.GetProperty("name").GetString() == PermissionClaim.Type);

        JsonElement config = mapper.GetProperty("config");
        config.GetProperty("claim.name").GetString().ShouldBe(PermissionClaim.Type);
        config.GetProperty("access.token.claim").GetString().ShouldBe("true");
        config.GetProperty("multivalued").GetString()
            .ShouldBe("true", "a single-valued claim silently keeps one permission and drops the rest");

        // Client roles scoped to the API client, not realm roles: a realm-role
        // mapper also emits offline_access, uma_authorization and
        // default-roles-commerce into this claim, which puts Keycloak's own
        // internals inside the vocabulary.
        mapper.GetProperty("protocolMapper").GetString().ShouldBe("oidc-usermodel-client-role-mapper");
        config.GetProperty("usermodel.clientRoleMapping.clientId").GetString().ShouldBe(Audience);
    }

    [Fact]
    public void Every_client_holding_the_scope_holds_it_as_a_default()
    {
        // §11.5's row 2. A client scope left optional is silently absent from
        // any token that does not request it by name — and a client-credentials
        // token requests no scope explicitly, so the BFF's token would carry
        // neither the audience nor the permissions while the realm looked
        // correctly configured in the console.
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string? id = client.GetProperty("clientId").GetString();

            bool optional =
                client.TryGetProperty("optionalClientScopes", out JsonElement optionals) &&
                optionals.EnumerateArray().Any(s => s.GetString() == Audience);

            optional.ShouldBeFalse($"'{id}' holds {Audience} as an optional scope, so its tokens will not carry it");
        }

        // Not vacuous: with no client holding it at all, the loop above passes
        // and no token in the realm ever gets an audience. Named rather than
        // counted, because Keycloak's built-in clients hold scopes and mint
        // tokens nobody uses.
        JsonElement tokenClient = Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient.GetProperty("defaultClientScopes").EnumerateArray()
            .Select(s => s.GetString())
            .ShouldContain(
                Audience,
                $"'{TokenClient}' does not hold {Audience} as a default scope, so its " +
                "tokens carry no audience this platform accepts");
    }

    [Fact]
    public void The_documented_login_can_actually_be_performed()
    {
        // The audience assertion above says the token would be usable; this
        // says one can be obtained at all. The compose README's recipe is a
        // password grant against web-app, which needs the direct access grant
        // and a public client.
        JsonElement tokenClient = Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient.GetProperty("enabled").GetBoolean()
            .ShouldBeTrue($"a disabled '{TokenClient}' satisfies both flags below and issues nothing");
        tokenClient.GetProperty("directAccessGrantsEnabled").GetBoolean()
            .ShouldBeTrue($"the README obtains a token by password grant against '{TokenClient}'");
        tokenClient.GetProperty("publicClient").GetBoolean()
            .ShouldBeTrue($"the README's grant sends no secret, and none is committed for '{TokenClient}'");
    }

    [Fact]
    public void The_access_token_lifetime_is_the_one_the_chapter_states()
    {
        // §11.3 states the lifetime normatively and this realm is what sets it:
        // Common.Web validates the `exp` Keycloak wrote and sets no lifetime of
        // its own. Read from AuthenticationExtensions rather than as a literal,
        // because every host also refuses a token with more than
        // RevocationBound of remaining life (ADR-040), and one number in two
        // files agrees until one is edited. There is no denylist consumer and
        // no introspection call (ADR-033), so this bound is most of the
        // revocation exposure.
        int statedLifetimeSeconds = (int)AuthenticationExtensions.AccessTokenLifetime.TotalSeconds;

        Root.GetProperty("accessTokenLifespan").GetInt32().ShouldBe(
            statedLifetimeSeconds,
            "§11.3 states the access-token lifetime and this realm is what sets it");

        // The realm carries a second lifetime, accessTokenLifespanForImplicitFlow,
        // that is unreachable only because no client enables the implicit flow
        // — a premise §11.3's window rests on, so it is asserted rather than
        // assumed.
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
        // §11.2's flow ends at the browser, so anything web-app is issued is
        // reachable by any script on the origin. A refresh token there turns
        // one XSS into an account takeover that outlives the session; with
        // none issued, the exposure is bounded by the access-token lifetime
        // pinned above.
        JsonElement tokenClient = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        // The positive half: with standardFlowEnabled off there is no
        // authorization-code flow, so "no refresh token reaches the browser"
        // would be true for the wrong reason.
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
        // The two halves of §11.5's demonstration, and the negative one is the
        // one worth a test: `browser` proving a refusal is only a proof while
        // it holds nothing. Granting it catalog:write — the obvious "fix" for
        // a 403 somebody did not expect — turns the 403 case into a second
        // success case, and nothing else here would notice.
        JsonElement users = Root.GetProperty("users");

        string[] Permissions(string username) =>
        [
            .. users.EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == username)
                .GetProperty("clientRoles")
                .GetProperty(Audience)
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        // demo holds Ordering's endpoint permissions and not orders:admin: that
        // role is grantable and held by nobody, so the ownership 404 stays
        // demonstrable with the logins this realm ships. demo holds
        // inventory:admin because stock exists only through the API it
        // guards, and payments:admin for the same reason — each guards a route
        // with no ownership check to override.
        Permissions("demo").ShouldBe(
            ["catalog:write", "orders:write", "orders:cancel", "inventory:admin", "payments:admin"],
            ignoreOrder: true);

        JsonElement browser = users.EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "browser");

        browser.TryGetProperty("clientRoles", out JsonElement granted)
            .ShouldBeFalse("'browser' exists to prove a refusal, so it must hold no client role at all");

        // A user disabled, a credential Keycloak marks temporary, or a
        // different password each fails the README's non-interactive grant
        // with a 401 while every role assertion above stays green. The value
        // is pinned because §11.6's carve-out is for documented local
        // defaults, and one nobody can guess is not one.
        foreach (string username in (string[])["demo", "browser"])
        {
            JsonElement user = users.EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == username);

            user.GetProperty("enabled").GetBoolean()
                .ShouldBeTrue($"'{username}' is one of §11.5's two documented logins");

            JsonElement password = user.GetProperty("credentials").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "password");

            password.GetProperty("value").GetString()
                .ShouldBe(username, $"the compose README documents '{username}' as its own password");

            password.GetProperty("temporary").GetBoolean()
                .ShouldBeFalse($"a temporary credential makes '{username}' unusable by the README's password grant");
        }
    }

    [Fact]
    public void No_role_description_exceeds_what_keycloak_can_store()
    {
        // Keycloak's ROLE.DESCRIPTION is VARCHAR(255) and an over-long value
        // does not truncate: the import throws, the container exits 1, and
        // `up --wait` names Keycloak and nothing about the column.
        const int keycloakDescriptionLimit = 255;

        (string Name, string Description)[] roles =
        [
            .. Root.GetProperty("roles").GetProperty("client").GetProperty(Audience).EnumerateArray()
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
        // The permissions a policy can require have to exist somewhere a
        // person can grant them, and the permission a route requires (§10.2)
        // obeys the same rule as one an endpoint requires (§11.4). Grantable
        // is the bar, not granted.
        string[] roles =
        [
            .. Root.GetProperty("roles").GetProperty("client").GetProperty(Audience).EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];

        // The whole set, not a containment check: ShouldContain would permit
        // any number of undeclared permissions to be grantable. orders:admin
        // is a claim CancelOrderHandler reads and no endpoint names; without
        // the role no token this realm can issue could carry it, and the
        // handler's admin branch would be unreachable code rather than an
        // override somebody can be granted.
        roles.ShouldBe(
            [
                "catalog:write",
                "inventory:admin",
                "payments:admin",
                "orders:write",
                "orders:cancel",
                "orders:admin"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void The_builtin_client_scopes_are_all_present()
    {
        // Keycloak's realm import treats a `clientScopes` array as the complete
        // set: supply only commerce-api and the built-ins are never created.
        // Nothing fails — the token silently loses `sub`, which
        // ICurrentUser.Id reads, and `preferred_username`, `email` and
        // `realm_access` with it.
        string[] builtins = ["basic", "profile", "email", "roles", "web-origins", "acr"];
        string[] names = [.. ClientScopes.Select(s => s.GetProperty("name").GetString()).OfType<string>()];

        foreach (string builtin in builtins)
        {
            names.ShouldContain(
                builtin,
                $"'{builtin}' is a Keycloak built-in; a realm that declares clientScopes " +
                "and omits it never creates it");
        }

        // Declared is not assigned, and the gap between them is the same defect
        // by a shorter route: dropping `basic` from web-app's defaultClientScopes
        // takes `sub` out of the README's token while the scope itself still
        // exists in the realm and every assertion above stays green.
        string[] assigned =
        [
            .. Root.GetProperty("clients").EnumerateArray()
                .Single(c => c.GetProperty("clientId").GetString() == TokenClient)
                .GetProperty("defaultClientScopes").EnumerateArray()
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

        // Present and assigned is still not carrying: deleting the mapper
        // inside `basic`, or turning off its access.token.claim, leaves the
        // scope declared and assigned while every token loses `sub`.
        JsonElement basic = ClientScopes.Single(s => s.GetProperty("name").GetString() == "basic");

        JsonElement subject = MappersOf(basic).Single(
            m => m.GetProperty("protocolMapper").GetString() == "oidc-sub-mapper");

        subject.GetProperty("config").GetProperty("access.token.claim").GetString()
            .ShouldBe("true", "a `sub` on the id token alone is invisible to a bearer check");
    }

    /// <summary>
    /// The one client whose grant requires both sides to agree on a secret
    /// (§11.5), and the documented local-development value it agrees on.
    /// </summary>
    /// <remarks>
    /// A client-credentials flow is two parties holding the same string, one
    /// of which is a committed Compose file, so a Keycloak-generated secret
    /// would leave the realm and the deployment disagreeing. Pinning the value
    /// keeps the rule strong: a generated secret fails here, and so does a
    /// real one.
    /// </remarks>
    private const string CredentialClient = "web-bff";
    private const string DocumentedLocalSecret = "local-dev-secret";

    [Fact]
    public void No_client_ships_a_secret_but_the_one_whose_grant_needs_one()
    {
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string clientId = client.GetProperty("clientId").GetString()!;
            bool ships = client.TryGetProperty("secret", out JsonElement secret);

            if (clientId != CredentialClient)
            {
                // §11.6 and the local-development carve-out: Compose's
                // documented defaults are deliberate, and a randomly generated
                // secret is not one of them. Keycloak regenerates on import.
                ships.ShouldBeFalse($"'{clientId}' ships a secret and needs none");

                continue;
            }

            ships.ShouldBeTrue(
                $"'{clientId}' authenticates with the client-credentials grant, so the realm and " +
                "the deployment have to hold the same value (§11.5)");

            // The documented default and nothing else. The matching half lives
            // in deploy/compose/services/web-bff.yml, which a building block's
            // suite may not read.
            secret.GetString().ShouldBe(
                DocumentedLocalSecret,
                "a secret in a committed realm must be the documented local default, " +
                "never a generated or real one (§11.6)");
        }
    }

    [Fact]
    public void The_resource_client_can_mint_no_token_of_its_own()
    {
        // Why the absent secret above is safe rather than merely tidy:
        // Keycloak generates one on import, so `commerce-api` has a working
        // credential in every running realm. What makes that harmless is that
        // it has no flow to spend it on; enable any of these and the
        // regenerated secret mints tokens carrying every permission in the
        // platform.
        JsonElement resource = Root.GetProperty("clients").EnumerateArray()
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
        // `browser` proving a refusal rests on it holding no permission, and
        // the test above checks the direct grant only. Every user also holds
        // `default-roles-commerce`, a composite, so a permission added to it
        // reaches the token through the same client-role mapper while
        // `browser` still has no `clientRoles` of its own — §11.5's realm-role
        // hazard by inheritance rather than by mapper.
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

    /// <summary>
    /// Walks up from the test binary to the repository root, which is the one
    /// directory <c>Platform.slnx</c> sits in. Not a relative path from the
    /// assembly location: that hard-codes the build's directory depth, and
    /// changing the target framework moves it.
    /// </summary>
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

        // An absent file must fail here rather than as an empty realm that
        // satisfies nothing and asserts nothing — a moved or renamed realm is
        // exactly the change this suite exists to catch.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}

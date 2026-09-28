using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Testcontainers.Keycloak;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// A real Keycloak with the shipped realm imported — the only fixture in the
/// solution that runs one (§11.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>§12.4's fixture deliberately does the opposite, and both are right.</b>
/// It points at an unreachable authority and swaps the JWT scheme for
/// <c>TestAuthHandler</c>, because the several hundred tests that merely need
/// <i>a</i> principal should not pay for an identity provider or fail when one
/// is slow. That fixture therefore cannot see this defect at all: it never
/// validates a token Keycloak issued, so an audience mapper missing from the
/// realm is invisible to every other test in the repository.
/// </para>
/// <para>
/// The realm file is the shipped one, read off disk. A copy written for the
/// test would prove that the copy works.
/// </para>
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime
{
    /// <summary>The realm the platform's tokens come from (§14.1).</summary>
    public const string Realm = "commerce";

    /// <summary>
    /// ADR-052's second credentialed client and the documented local default
    /// it authenticates with (§14.1). Here rather than in each test class for
    /// the reason the admin pair below is: the realm file and this fixture are
    /// the two halves that have to agree.
    /// </summary>
    public const string WorkerClient = "shipping-worker";
    public const string WorkerSecret = "local-dev-shipping-secret";

    /// <summary>
    /// The container's bootstrap admin, set here rather than read back off
    /// the container: the module exposes no accessor for either value, and a
    /// literal that the builder below also sets cannot drift from it.
    /// </summary>
    private const string AdminUser = "admin";
    private const string AdminPassword = "admin";

    /// <summary>
    /// The realm's built-in scopes and nothing else, for the unrelated
    /// client below. A static field because CA1861 is an error under
    /// ADR-019.
    /// </summary>
    private static readonly string[] BuiltInScopes =
        ["basic", "profile", "email", "roles", "acr", "web-origins"];

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder()
        .WithImage("quay.io/keycloak/keycloak:26.0")
        .WithUsername(AdminUser)
        .WithPassword(AdminPassword)
        .WithResourceMapping(
            new FileInfo(RepositoryFile.Locate(RepositoryFile.RealmExport)),
            "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    /// <summary>The realm's authority, as a host would configure it.</summary>
    public string Authority => $"{_keycloak.GetBaseAddress().TrimEnd('/')}/realms/{Realm}";

    public HttpClient Http { get; private set; } = null!;

    public ValueTask InitializeAsync() => Start();

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        await _keycloak.DisposeAsync();
    }

    /// <summary>
    /// A client-credentials token for <paramref name="clientId"/>, or the
    /// error the provider answered with.
    /// </summary>
    public async Task<(bool Granted, string Token)> ClientCredentialsAsync(string clientId, string secret)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret
        });

        using HttpResponseMessage response = await Http.PostAsync(
            $"{Authority}/protocol/openid-connect/token",
            form,
            TestContext.Current.CancellationToken);

        if (!response.IsSuccessStatusCode)
            return (false, "");

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        return (true, body.GetProperty("access_token").GetString()!);
    }

    /// <summary>
    /// Creates a confidential service-account client with NO
    /// <c>commerce-api</c> scope, for the negative half of §11.5.
    /// </summary>
    /// <remarks>
    /// Created here rather than shipped in the realm, and that is the point:
    /// a client the platform does not deploy is exactly what "some other
    /// client in this realm" means, and adding one to the export so a test
    /// could use it would put a credential in a deployed realm for a test's
    /// convenience.
    /// </remarks>
    public async Task<string> CreateUnrelatedClientAsync(string clientId, string secret)
    {
        string admin = await AdminTokenAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{_keycloak.GetBaseAddress().TrimEnd('/')}/admin/realms/{Realm}/clients")
        {
            Content = JsonContent.Create(new
            {
                clientId,
                enabled = true,
                protocol = "openid-connect",
                publicClient = false,
                secret,
                serviceAccountsEnabled = true,
                standardFlowEnabled = false,
                directAccessGrantsEnabled = false,
                // The realm's own built-ins and nothing else. commerce-api is
                // absent, which is the whole experiment.
                defaultClientScopes = BuiltInScopes
            })
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);

        using HttpResponseMessage response = await Http.SendAsync(
            request,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        return clientId;
    }

    private async Task<string> AdminTokenAsync()
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUser,
            ["password"] = AdminPassword
        });

        using HttpResponseMessage response = await Http.PostAsync(
            $"{_keycloak.GetBaseAddress().TrimEnd('/')}/realms/master/protocol/openid-connect/token",
            form,
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        return body.GetProperty("access_token").GetString()!;
    }

    private async ValueTask Start()
    {
        await _keycloak.StartAsync();
        Http = new HttpClient();

        await WaitForRealmAsync();
    }

    /// <summary>
    /// Polls until the imported realm answers, which is a later moment than
    /// the container being up.
    /// </summary>
    /// <remarks>
    /// <b>A poll here rather than a Testcontainers wait strategy, and the first
    /// attempt was the strategy.</b> <c>UntilHttpRequestIsSucceeded</c> with a
    /// path and no port probes the container's <i>first</i> exposed port, and
    /// this image exposes two — 8080 for the realm and 9000 for the management
    /// interface — so the probe asked the wrong one, never succeeded, and the
    /// suite sat on a healthy Keycloak for ten minutes before Testcontainers
    /// tore it down. The container logs said <c>Realm 'commerce' imported</c>
    /// throughout.
    /// <para>
    /// What this waits for is also the right condition rather than a proxy for
    /// it: the process listening is not the same event as the realm existing,
    /// and it is the second that every test here needs. That is the same
    /// distinction §14.1's Compose healthcheck draws, one layer down — a
    /// <c>service_started</c> gate lets the README's token command race the
    /// import and be handed a 404.
    /// </para>
    /// </remarks>
    private async Task WaitForRealmAsync()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(
                    $"{Authority}/.well-known/openid-configuration",
                    TestContext.Current.CancellationToken);

                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException(
            $"Keycloak never served realm '{Realm}' at {Authority}. The container may be up with the " +
            "import having failed, which is the state a readiness probe on the process alone cannot see.");
    }
}

/// <summary>
/// §12.4's per-assembly collection, so one Keycloak serves every test that
/// needs one rather than one per class.
/// </summary>
/// <remarks>
/// <b>The category sits here rather than on each member class, and that is
/// the whole design.</b> xUnit v3 applies a collection's traits to every test
/// in it, so joining the collection <i>is</i> carrying the category and there
/// is no per-class attribute for a new test class to forget. It decides which
/// stage runs a test and never whether it may be absent — a skip on a missing
/// daemon fails open and is still refused (§12.4).
/// <para>
/// <b>This suite is the clearest case for putting the category on a
/// collection rather than on a project.</b> Fifty-nine of its sixty-three
/// tests never needed a container and four need an identity provider, so a
/// project-level split would have had nothing to split — the assembly is
/// mostly fast and pays for a Keycloak once.
/// </para>
/// <para>
/// What that buys is a container start, not a fast suite, and the difference
/// is worth stating because the obvious claim is the wrong one: measured here,
/// the four integration tests cost about 35 seconds wall and the fifty-nine
/// others still take about a minute on their own. The BFF's fast half is slow
/// because §9.7's resilience tests wait on real timeouts, and no category
/// changes that. See <c>docs/testing.md</c>.
/// </para>
/// </remarks>
[CollectionDefinition(nameof(KeycloakCollection))]
[Trait("Category", "Integration")]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>;

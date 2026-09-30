using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using Testcontainers.Keycloak;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>A real Keycloak importing the shipped realm file, the only fixture that runs one (§11.5).</summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    /// <summary>The realm the platform's tokens come from (§14.1).</summary>
    public const string Realm = "commerce";

    /// <summary>ADR-052's second credentialed client and its local default (§14.1), matching the realm.</summary>
    public const string WorkerClient = "shipping-worker";
    public const string WorkerSecret = "local-dev-shipping-secret";

    /// <summary>The bootstrap admin, literals the builder also sets, as the module exposes no accessor.</summary>
    private const string AdminUser = "admin";
    private const string AdminPassword = "admin";

    /// <summary>The realm's built-in scopes alone, static because CA1861 is an error under ADR-019.</summary>
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

    /// <summary>A client-credentials token for <paramref name="clientId"/>, or not granted.</summary>
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

    /// <summary>A client lacking the <c>commerce-api</c> scope, created here rather than shipped (§11.5).</summary>
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
                // Built-ins alone: commerce-api's absence is the experiment.
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

    /// <summary>Polls until the imported realm answers, a later event than the process listening (§14.1).</summary>
    private async Task WaitForRealmAsync()
    {
        // A poll, as UntilHttpRequestIsSucceeded without a port probes the first of this image's two ports.
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

/// <summary>§12.4's per-assembly collection: one Keycloak, and a category every member class inherits.</summary>
[CollectionDefinition(nameof(KeycloakCollection))]
[Trait("Category", "Integration")]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>;

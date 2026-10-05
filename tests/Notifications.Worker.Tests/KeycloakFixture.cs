using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Infrastructure.Identity;
using Common.TestSupport;
using Common.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notifications.TestSupport;
using Testcontainers.Keycloak;
using Xunit;
using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>A real Keycloak importing the shipped realm, the owner ADR-052's contact read asks.</summary>
/// <remarks>
/// The user profile is loosened and internationalisation turned on, on this container alone, so a value the
/// shipped realm refuses or drops can still reach the adapter (ADR-052); the export is what every fixture imports.
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime
{
    public const string Realm = NotificationsWorkerFactory.LocalRealm;

    private const string ContactScope = NotificationsWorkerFactory.ContactScope;

    /// <summary>ADR-052's contact reader and its local default (§14.1), matching the realm.</summary>
    public const string ContactClient = "notifications-worker";
    public const string ContactSecret = "local-dev-notifications-secret";

    /// <summary>A credentialed client of the same realm holding no role on <c>realm-management</c> (§11.5).</summary>
    public const string UngrantedClient = "web-bff";
    public const string UngrantedSecret = "local-dev-secret";

    /// <summary>The bootstrap admin, literals the builder also sets, as the module exposes no accessor.</summary>
    private const string AdminUser = "admin";
    private const string AdminPassword = "admin";

    /// <summary>The one locale the container's realm offers, static because CA1861 is an error under ADR-019.</summary>
    private static readonly string[] SupportedLocales = ["en"];

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder()
        .WithImage(ComposeImage.Of("keycloak"))
        .WithUsername(AdminUser)
        .WithPassword(AdminPassword)
        .WithResourceMapping(
            new FileInfo(Path.Combine(RepositoryRoot.Locate(), "deploy", "compose", "keycloak", "realm-export.json")),
            "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    private readonly List<ContactHost> _hosts = [];

    public HttpClient Http { get; private set; } = null!;

    /// <summary>The server's root, where both the realm and its admin API live.</summary>
    public string BaseAddress => _keycloak.GetBaseAddress().TrimEnd('/') + "/";

    /// <summary>The realm's authority, as a host would configure it.</summary>
    public string Authority => $"{BaseAddress}realms/{Realm}";

    /// <summary>The real host holding the contact reader's credential, through its own grant check.</summary>
    public ContactHost Granted { get; private set; } = null!;

    /// <summary>The same host holding a credential with no grant, refused by its own check before any read.</summary>
    public ContactHost Ungranted { get; private set; } = null!;

    /// <summary>The ungranted credential with the check bypassed, so Keycloak's own refusal is what is met.</summary>
    public ContactHost UncheckedUngranted { get; private set; } = null!;

    /// <summary>The contact reader's id with a secret the realm does not hold.</summary>
    public ContactHost WrongSecret { get; private set; } = null!;

    /// <summary>The granted host reading a realm of a valid name that this server does not hold.</summary>
    public ContactHost WrongRealm { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _keycloak.StartAsync();
        Http = new HttpClient();

        await WaitForRealmAsync();
        await LoosenUserProfileAsync();
        await WarmAsync();

        Granted = Host(ContactClient, ContactSecret, grantChecked: true);
        Ungranted = Host(UngrantedClient, UngrantedSecret, grantChecked: true);
        UncheckedUngranted = Host(UngrantedClient, UngrantedSecret, grantChecked: false);
        WrongSecret = Host(ContactClient, "not-the-realms-secret", grantChecked: true);
        WrongRealm = Host(ContactClient, ContactSecret, grantChecked: true, realm: Realm + "x");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ContactHost host in _hosts)
            host.Dispose();

        Http?.Dispose();
        await _keycloak.DisposeAsync();
    }

    /// <summary>A client-credentials token, failing loudly if the realm refuses the client.</summary>
    public async Task<string> TokenAsync(string clientId, string secret, string scope)
    {
        using HttpResponseMessage response = await GrantAsync(clientId, secret, scope);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>A user created through the admin API, with a name the adapter must never bind; its id.</summary>
    public async Task<Guid> CreateUserAsync(string? email, bool enabled = true, string? locale = null)
    {
        Dictionary<string, object?> user = new()
        {
            ["username"] = $"customer-{Guid.CreateVersion7():N}",
            ["enabled"] = enabled,
            ["firstName"] = "Айгерім",
            ["lastName"] = "Сейітқызы"
        };

        if (email is not null)
            user["email"] = email;

        if (locale is not null)
            user["attributes"] = new Dictionary<string, string[]> { ["locale"] = [locale] };

        using HttpRequestMessage request = new(HttpMethod.Post, $"{BaseAddress}admin/realms/{Realm}/users")
        {
            Content = JsonContent.Create(user)
        };

        using HttpResponseMessage response = await AsAdminAsync(request);
        response.EnsureSuccessStatusCode();

        return Guid.Parse(response.Headers.Location!.Segments[^1]);
    }

    /// <summary>A user as the master admin reads it, so a test can tell staging from the adapter.</summary>
    public async Task<JsonElement> UserAsAdminAsync(Guid id)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{BaseAddress}admin/realms/{Realm}/users/{id:D}");
        using HttpResponseMessage response = await AsAdminAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private ContactHost Host(string clientId, string secret, bool grantChecked, string realm = Realm)
    {
        ContactHost host = new(BaseAddress, Authority, clientId, secret, grantChecked, realm);
        _hosts.Add(host);

        return host;
    }

    private async Task<HttpResponseMessage> GrantAsync(string clientId, string secret, string scope)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope
        });

        return await Http.PostAsync(
            $"{Authority}/protocol/openid-connect/token", form, TestContext.Current.CancellationToken);
    }

    /// <summary>Asks Keycloak each question a host will, as its first answers can outlast ContactHop's total.</summary>
    /// <remarks>Answers are discarded as the tests' subject (ADR-052); only their being first is this one's.</remarks>
    private async Task WarmAsync()
    {
        using HttpResponseMessage granted = await GrantAsync(ContactClient, ContactSecret, ContactScope);
        using HttpResponseMessage ungranted = await GrantAsync(UngrantedClient, UngrantedSecret, ContactScope);

        using HttpRequestMessage read = new(
            HttpMethod.Get, $"{BaseAddress}admin/realms/{Realm}/users/{Guid.CreateVersion7():D}");
        using HttpResponseMessage answered = await AsAdminAsync(read);
    }

    private async Task LoosenUserProfileAsync()
    {
        // Keycloak drops a user's locale while internationalisation is off, whatever the unmanaged policy.
        using HttpRequestMessage realm = new(HttpMethod.Put, $"{BaseAddress}admin/realms/{Realm}")
        {
            Content = JsonContent.Create(
                new { internationalizationEnabled = true, supportedLocales = SupportedLocales })
        };
        using HttpResponseMessage localised = await AsAdminAsync(realm);
        localised.EnsureSuccessStatusCode();

        string url = $"{BaseAddress}admin/realms/{Realm}/users/profile";

        using HttpRequestMessage read = new(HttpMethod.Get, url);
        using HttpResponseMessage current = await AsAdminAsync(read);
        current.EnsureSuccessStatusCode();

        JsonNode profile = (await current.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        profile["unmanagedAttributePolicy"] = "ADMIN_EDIT";

        JsonObject email = profile["attributes"]!.AsArray()
            .Single(a => (string?)a!["name"] == "email")!
            .AsObject();
        email["validations"]?.AsObject().Remove("email");

        using HttpRequestMessage write = new(HttpMethod.Put, url) { Content = JsonContent.Create(profile) };
        using HttpResponseMessage written = await AsAdminAsync(write);
        written.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> AsAdminAsync(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
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
            $"{BaseAddress}realms/master/protocol/openid-connect/token", form, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Polls until the imported realm answers, a later event than the process listening (§14.1).</summary>
    private async Task WaitForRealmAsync()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(
                    $"{Authority}/.well-known/openid-configuration", TestContext.Current.CancellationToken);

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
            $"Keycloak never served realm '{Realm}' at {Authority}; the import may have failed with the process up.");
    }

    /// <summary>The real host pointed at this container and holding one client's credential.</summary>
    /// <remarks>
    /// The address is passed and the authority captured, never both of one parameter (CS9107, an error under ADR-019).
    /// </remarks>
    public sealed class ContactHost(
        string baseAddress,
        string authority,
        string clientId,
        string secret,
        bool grantChecked,
        string realm)
        : ContactSourceTests.PatientFactory(baseAddress)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // A later setting replaces an earlier one, so these supersede the unreachable authority and the fake.
            builder
                .UseSetting(AuthenticationExtensions.AuthorityKey, authority)
                .UseSetting(ContactRegistration.RealmKey, realm)
                .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientId", clientId)
                .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", secret);
        }

        protected override void ConfigureTokens(IServiceCollection services)
        {
            // Program's own source is the grant check over CachingTokenClient; bypassed only when that is the subject.
            if (grantChecked)
                return;

            services.RemoveAll<ITokenCache>();
            services.AddSingleton<ITokenCache>(sp => sp.GetRequiredService<CachingTokenClient>());
        }
    }
}

/// <summary>§12.4's per-assembly collection: one Keycloak, and a category every member class inherits.</summary>
[CollectionDefinition(nameof(KeycloakCollection))]
[Trait("Category", "Integration")]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>;

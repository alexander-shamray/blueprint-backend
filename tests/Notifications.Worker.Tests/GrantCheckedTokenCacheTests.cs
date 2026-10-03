using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The grant ADR-052 names, checked on the token this host was issued.</summary>
public class GrantCheckedTokenCacheTests
{
    private static readonly string[] TheGrant = ["view-users", "query-users", "query-groups"];

    public static TheoryData<string[]> GrantsThatAreNotTheOne()
    {
        TheoryData<string[]> data = [];
        data.Add([]);
        data.Add(["view-users"]);
        data.Add(["query-users", "query-groups"]);
        data.Add(["view-users", "query-users", "query-groups", "manage-users"]);
        data.Add(["view-users", "query-users", "query-groups", "view-clients"]);
        data.Add(["realm-admin"]);
        data.Add(["view-users", "view-users", "query-users", "query-groups"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(GrantsThatAreNotTheOne))]
    public async Task A_token_whose_grant_is_not_exactly_the_one_the_record_names_is_refused(string[] roles)
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.ContactRefused(services);

        ContactSourceRefusedException thrown = await Should.ThrowAsync<ContactSourceRefusedException>(
            () => Cache(services, new FixedTokenCache(Jwt(Granted(roles)))).GetAsync("roles", Ct));

        // The count, never the names: a role set in a message is a configuration detail a log carries.
        thrown.Message.ShouldNotContain("manage-users");
        thrown.Message.ShouldNotContain("view-clients");
        counted.Value.ShouldBe(1);
    }

    public static TheoryData<string> PayloadsThatHoldNoGrant() =>
    [
        """{"iss":"x"}""",
        """{"resource_access":"realm-management"}""",
        """{"resource_access":{"account":{"roles":["view-profile"]}}}""",
        """{"resource_access":{"realm-management":{"roles":"view-users"}}}""",
        """{"resource_access":{"realm-management":{"roles":["view-users","query-users",7]}}}"""
    ];

    [Theory]
    [MemberData(nameof(PayloadsThatHoldNoGrant))]
    public async Task A_token_whose_claim_is_absent_or_misshapen_is_refused_rather_than_read_as_empty(string payload)
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.ContactRefused(services);

        await Should.ThrowAsync<ContactSourceRefusedException>(
            () => Cache(services, new FixedTokenCache(Jwt(payload))).GetAsync("roles", Ct));

        counted.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_token_carrying_exactly_the_grant_in_any_order_is_handed_on_unchanged()
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.ContactRefused(services);
        string issued = Jwt(Granted(["query-groups", "view-users", "query-users"]));

        (await Cache(services, new FixedTokenCache(issued)).GetAsync("roles", Ct)).ShouldBe(issued);

        counted.Value.ShouldBe(0);
    }

    [Fact]
    public async Task The_realms_default_roles_beside_the_grant_are_outside_the_check()
    {
        // ADR-052 names them so nobody widens the check to them: realm_access and the account client.
        using ServiceProvider services = Metrics();
        string issued = Jwt(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["realm_access"] = Roles(["default-roles-commerce", "offline_access", "uma_authorization"]),
            ["resource_access"] = new Dictionary<string, object>
            {
                ["account"] = Roles(["manage-account", "view-profile"]),
                ["realm-management"] = Roles(TheGrant)
            }
        }));

        (await Cache(services, new FixedTokenCache(issued)).GetAsync("roles", Ct)).ShouldBe(issued);
    }

    [Fact]
    public async Task A_refused_client_credential_is_a_refusal_and_a_transport_fault_is_not()
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.ContactRefused(services);

        // CachingTokenClient's split, relied on here: InvalidOperationException for a refused client (§11.5).
        await Should.ThrowAsync<ContactSourceRefusedException>(() =>
            Cache(services, new FixedTokenCache(new InvalidOperationException("refused"))).GetAsync("roles", Ct));
        await Should.ThrowAsync<HttpRequestException>(() =>
            Cache(services, new FixedTokenCache(new HttpRequestException("down"))).GetAsync("roles", Ct));

        counted.Value.ShouldBe(1, "the transport fault passes uncounted");
    }

    [Fact]
    public async Task A_token_that_is_not_a_jwt_is_a_refusal_that_quotes_nothing_of_it()
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.ContactRefused(services);

        ContactSourceRefusedException thrown = await Should.ThrowAsync<ContactSourceRefusedException>(() =>
            Cache(services, new FixedTokenCache("an-opaque-token-value")).GetAsync("roles", Ct));

        thrown.Message.ShouldNotContain("an-opaque-token-value");
        thrown.InnerException.ShouldBeNull("the parser's message can quote the token it could not read");
        counted.Value.ShouldBe(1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The meter factory and the one metrics type, from a container that outlives the assertions.</summary>
    private static ServiceProvider Metrics() =>
        new ServiceCollection().AddMetrics().AddSingleton<ContactMetrics>().BuildServiceProvider();

    private static GrantCheckedTokenCache Cache(IServiceProvider services, ITokenCache inner) =>
        new(inner, services.GetRequiredService<ContactMetrics>(), NullLogger<GrantCheckedTokenCache>.Instance);

    private static Dictionary<string, object> Roles(string[] roles) => new() { ["roles"] = roles };

    /// <summary>A payload carrying <paramref name="roles"/> on <c>realm-management</c> and nothing else.</summary>
    private static string Granted(string[] roles) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["resource_access"] = new Dictionary<string, object> { ["realm-management"] = Roles(roles) }
        });

    /// <summary>An unsigned token over a given payload, since the subject is a nested claim's shape.</summary>
    private static string Jwt(string payload) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")) + "." +
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + ".";

    /// <summary>A token source answering with one token or throwing one exception.</summary>
    private sealed class FixedTokenCache : ITokenCache
    {
        private readonly string? _token;
        private readonly Exception? _fault;

        public FixedTokenCache(string token) => _token = token;

        public FixedTokenCache(Exception fault) => _fault = fault;

        public Task<string> GetAsync(string scope, CancellationToken ct) =>
            _fault is null ? Task.FromResult(_token!) : Task.FromException<string>(_fault);
    }
}

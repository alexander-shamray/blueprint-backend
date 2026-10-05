using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shipping.Application.Addresses;
using Shipping.Infrastructure.Addresses;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>The grant ADR-052 names, checked on the token this host was issued.</summary>
public class GrantCheckedTokenCacheTests
{
    public static TheoryData<string[]> GrantsThatAreNotTheOne()
    {
        TheoryData<string[]> data = [];
        data.Add([]);
        data.Add(["orders:write"]);
        data.Add(["orders:delivery-address", "orders:write"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(GrantsThatAreNotTheOne))]
    public async Task A_token_whose_grant_is_not_exactly_the_one_record_names_is_refused(string[] permissions)
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.Refused(services);

        AddressSourceRefusedException thrown = await Should.ThrowAsync<AddressSourceRefusedException>(
            () => Cache(services, new FixedTokenCache(Jwt(permissions)))
                .GetAsync("commerce-api", TestContext.Current.CancellationToken));

        // The count, never the values: a permission set in a message is a
        // configuration detail, and the message is what a log carries.
        thrown.Message.ShouldNotContain("orders:");
        counted.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_token_carrying_exactly_the_grant_is_handed_on_unchanged()
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.Refused(services);
        string issued = Jwt(["orders:delivery-address"]);

        (await Cache(services, new FixedTokenCache(issued))
                .GetAsync("commerce-api", TestContext.Current.CancellationToken))
            .ShouldBe(issued);

        counted.Value.ShouldBe(0);
    }

    [Fact]
    public async Task A_refused_client_credential_is_a_refusal_and_a_transport_fault_is_not()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.Refused(services);

        // CachingTokenClient's own split, relied on here: InvalidOperationException
        // for a provider that refused this client, HttpRequestException for one
        // that failed as a server does (§11.5).
        await Should.ThrowAsync<AddressSourceRefusedException>(() =>
            Cache(services, new FixedTokenCache(new InvalidOperationException("refused")))
                .GetAsync("commerce-api", ct));
        await Should.ThrowAsync<HttpRequestException>(() =>
            Cache(services, new FixedTokenCache(new HttpRequestException("down")))
                .GetAsync("commerce-api", ct));

        counted.Value.ShouldBe(1, "the transport fault backs off uncounted");
    }

    [Fact]
    public async Task A_token_client_disposed_at_shutdown_is_not_a_refused_credential()
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.Refused(services);

        // An InvalidOperationException by inheritance, which CachingTokenClient throws once disposed.
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            Cache(services, new FixedTokenCache(new ObjectDisposedException("CachingTokenClient")))
                .GetAsync("commerce-api", TestContext.Current.CancellationToken));

        counted.Value.ShouldBe(0, "a read in flight at shutdown is not the deployment's to fix");
    }

    [Fact]
    public async Task A_token_that_is_not_a_jwt_is_a_refusal_rather_than_an_outage()
    {
        using ServiceProvider services = Metrics();
        using OutboundCount counted = OutboundCounter.Refused(services);

        AddressSourceRefusedException thrown = await Should.ThrowAsync<AddressSourceRefusedException>(() =>
            Cache(services, new FixedTokenCache("an-opaque-token-value"))
                .GetAsync("commerce-api", TestContext.Current.CancellationToken));

        // The token is a credential, and the message is what a log carries.
        thrown.Message.ShouldNotContain("an-opaque-token-value");
        thrown.InnerException.ShouldBeNull("the parser's message can quote the token it could not read");
        counted.Value.ShouldBe(1, "a realm that issues no JWT is the deployment's to fix, and no retry will");
    }

    /// <summary>The meter factory and the one metrics type, from a container that outlives the assertions.</summary>
    private static ServiceProvider Metrics() =>
        new ServiceCollection().AddMetrics().AddSingleton<AddressMetrics>().BuildServiceProvider();

    private static GrantCheckedTokenCache Cache(IServiceProvider services, ITokenCache inner) =>
        new(inner, services.GetRequiredService<AddressMetrics>(), NullLogger<GrantCheckedTokenCache>.Instance);

    /// <summary>An unsigned token with one <c>permission</c> claim per element; the class never validates it.</summary>
    private static string Jwt(string[] permissions) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://identity.invalid/realms/test",
            claims: [.. permissions.Select(p => new Claim("permission", p))]));

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

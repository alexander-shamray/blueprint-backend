using System.Net.Http.Json;
using Catalog.Pricing.V1;
using Catalog.TestSupport;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using Xunit;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Tests;

/// <summary>
/// §9.7's server half, driven over the real pipeline: authentication,
/// authorization, the dispatcher, the validator and Dapper on a real database.
/// </summary>
/// <remarks>Over <c>TestServer</c>: it is the right instrument for what the
/// application decides, and what the server decides — that a cleartext
/// endpoint must be declared <c>Http2</c> before a gRPC client can reach it —
/// belongs against a real Kestrel instead. <c>TestServer.CreateHandler()</c>
/// bypasses the network, so the h2c negotiation this host would otherwise
/// need never happens.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PricingServiceTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;
    private GrpcChannel _channel = null!;

    public async ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        _channel = GrpcChannel.ForAddress(
            fixture.Factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = fixture.Factory.Server.CreateHandler() });

        await fixture.ResetAsync();
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _client.Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The principal a validated client-credentials token becomes (§11.3), as
    /// call metadata.
    /// </summary>
    /// <remarks>
    /// Passed per call rather than baked into the channel, for
    /// <c>ProductEndpointsTests</c>' reason one transport over: a default
    /// grant is how a suite ends up proving a policy is applied by never once
    /// arriving without it. The anonymous test below is the one that would
    /// silently stop meaning anything.
    /// </remarks>
    private static Metadata Authenticated() =>
        [new Metadata.Entry(TestAuthHandler.UserHeader, "service-account-web-bff")];

    private PricingGrpc.PricingClient Pricing => new(_channel);

    private async Task<Guid> PublishAsync(string name, decimal amount, string currency)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/v1/catalog/products")
        {
            Content = JsonContent.Create(new
            {
                // Fresh per call: two publishes under one CommandId would
                // replay the first id rather than creating a second product.
                CommandId = Guid.CreateVersion7(),
                Name = name,
                ThumbnailUrl = (string?)null,
                Amount = amount,
                Currency = currency
            })
        };

        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, CatalogPermissions.Write);

        HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task It_prices_the_products_it_is_asked_about()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");
        Guid desk = await PublishAsync("Desk", 120.50m, "GBP");

        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add(chair.ToString());
        request.ProductId.Add(desk.ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        reply.Price.Count.ShouldBe(2);

        // The invariant form pricing.proto specifies, asserted as TEXT rather
        // than parsed back: the whole reason the contract carries a string is
        // that the two ends have to agree on the spelling, and a test that
        // parsed it would agree with itself. A host under a comma-decimal
        // culture fails here and nowhere else.
        //
        // "49.9900", not "49.99", and the trailing zeros are the column's
        // scale reaching the wire — PriceAmount is decimal(19,4) (§7.2), and
        // .NET's decimal carries scale through, so ToString emits it. Pinned
        // rather than trimmed, because trimming would be presentation logic in
        // a contract; what it obliges a consumer to do is PARSE the field
        // rather than compare it, which is what the BFF does and what
        // pricing.proto now says out loud.
        reply.Price
            .Single(p => p.ProductId == chair.ToString())
            .Amount
            .ShouldBe("49.9900");
    }

    [Fact]
    public async Task A_lower_case_currency_prices_the_same_products()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");

        GetPricesRequest request = new() { Currency = "gbp" };
        request.ProductId.Add(chair.ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Money.Of upper-cases on the way in, so the column only ever holds
        // "GBP" — and GetPricesValidator accepts [A-Za-z]{3}, so "gbp" is a
        // valid request.
        //
        // This one passes with the handler's ToUpperInvariant deleted, because
        // the fixture's collation is case-insensitive. It is here for the
        // request shape; the test below is the one that holds the
        // normalisation, and neither is a substitute for the other.
        reply.Price.Single().Currency.ShouldBe("GBP");
    }

    /// <summary>
    /// The same request against a case-sensitive column, which is the only
    /// configuration in which the handler's normalisation does anything.
    /// </summary>
    /// <remarks><c>ToUpperInvariant</c> exists for a deployment whose
    /// collation is case-sensitive, which SQL Server's default is not. The
    /// collation is changed on the column for this test and restored in a
    /// <c>finally</c>: <c>IntegrationCollection</c> runs serially, and
    /// <c>Respawn</c> resets rows and not schema.</remarks>
    [Fact]
    public async Task A_lower_case_currency_matches_under_a_case_sensitive_collation()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");

        // Read rather than assumed. Restoring to a hard-coded
        // SQL_Latin1_General_CP1_CI_AS would be right for the image this runs
        // against today and would silently re-collate the column on any server
        // configured differently — a test that repairs the schema into a state
        // it was never in is worse than one that leaves it broken loudly.
        string original = await CurrencyCollationAsync();

        await SetCurrencyCollationAsync("Latin1_General_CS_AS");

        try
        {
            GetPricesRequest request = new() { Currency = "gbp" };
            request.ProductId.Add(chair.ToString());

            GetPricesReply reply = await Pricing.GetPricesAsync(
                request,
                Authenticated(),
                cancellationToken: TestContext.Current.CancellationToken);

            // Without the handler's ToUpperInvariant this is empty: "gbp" is
            // compared to the stored "GBP" under a collation that tells them
            // apart, and a valid request is answered "product absent" — the
            // same answer a product that does not exist gets, which is what
            // makes the failure mode invisible in production.
            reply.Price.Single().Currency.ShouldBe("GBP");
        }
        finally
        {
            await SetCurrencyCollationAsync(original);
        }
    }

    /// <summary>The collation <c>PriceCurrency</c> currently carries.</summary>
    private Task<string> CurrencyCollationAsync() =>
        fixture.ScalarAsync<string>(
            // Value, and no terminator: ScalarAsync goes through
            // SqlQueryRaw, which wraps this as a subquery and reads one
            // column by that name. The repo's other scalar probes are
            // spelt the same way.
            """
            SELECT Value = collation_name
            FROM sys.columns
            WHERE object_id = OBJECT_ID('catalog.Products')
                AND name = 'PriceCurrency'
            """);

    /// <summary>
    /// Re-declares <c>PriceCurrency</c> with the named collation.
    /// </summary>
    /// <remarks>
    /// The type is restated because <c>ALTER COLUMN</c> takes a whole
    /// declaration rather than a patch, and dropping <c>NOT NULL</c> here
    /// would quietly relax a constraint the migration set. The collation name
    /// is interpolated because SQL Server does not accept a parameter in this
    /// position — it is a literal from this file and never from a caller,
    /// which is the same argument <c>OutboxTable</c> makes about a schema
    /// name.
    /// </remarks>
    private async Task SetCurrencyCollationAsync(string collation) =>
        await fixture.ExecuteAsync(
            $"ALTER TABLE catalog.Products ALTER COLUMN PriceCurrency nvarchar(3) COLLATE {collation} NOT NULL;");

    [Fact]
    public async Task A_product_priced_in_another_currency_is_absent_rather_than_zero()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");

        GetPricesRequest request = new() { Currency = "USD" };
        request.ProductId.Add(chair.ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        // Absent, never zero: a zero-amount entry would be a free product,
        // which is a different fact (pricing.proto).
        reply.Price.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_product_is_simply_absent()
    {
        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add(Guid.CreateVersion7().ToString());

        GetPricesReply reply = await Pricing.GetPricesAsync(
            request,
            Authenticated(),
            cancellationToken: TestContext.Current.CancellationToken);

        reply.Price.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        GetPricesRequest request = new() { Currency = "GBP" };

        // No principal: the channel with no interceptor. This is what makes
        // §11.5's client credentials load-bearing rather than ceremonial — if
        // this passed, the BFF's whole token mechanism could be missing and
        // every test would still be green.
        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(request, cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        thrown.StatusCode.ShouldBe(StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task A_malformed_product_id_is_InvalidArgument()
    {
        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add("not-a-guid");

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    request,
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // The index, and deliberately not the value: it is a caller-supplied
        // string arriving in a message that reaches the logs, and §13.4's
        // redactor cannot see a value interpolated into one.
        thrown.Status.Detail.ShouldContain("product_id[0]");
        thrown.Status.Detail.ShouldNotContain("not-a-guid");
    }

    [Theory]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("(11111111-1111-1111-1111-111111111111)")]
    public async Task A_non_canonical_guid_is_refused(string productId)
    {
        GetPricesRequest request = new() { Currency = "GBP" };
        request.ProductId.Add(productId);

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    request,
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // pricing.proto says "GUIDs in their canonical text form", and
        // Guid.TryParse accepts four more spellings than that — so every one
        // of these was a valid product id in a service whose contract says it
        // is not. Accepting more than the contract states is how two ends stop
        // agreeing about what the contract is.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Too_many_products_is_InvalidArgument_rather_than_Unknown()
    {
        GetPricesRequest request = new() { Currency = "GBP" };

        for (int i = 0; i <= Application.Products.GetPrices.GetPricesValidator.MaxProductIds; i++)
            request.ProductId.Add(Guid.CreateVersion7().ToString());

        RpcException thrown = await Should.ThrowAsync<RpcException>(
            () => Pricing
                .GetPricesAsync(
                    request,
                    Authenticated(),
                    cancellationToken: TestContext.Current.CancellationToken)
                .ResponseAsync);

        // ValidationInterceptor's whole job, and the status matters more than
        // it looks: untranslated this is Unknown, which the BFF maps to a 500 —
        // so a caller's malformed request would come back as this platform
        // having failed. Unknown rides an HTTP 200 like every other gRPC
        // status, so none of them is retried.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        thrown.Status.Detail.ShouldContain("ProductIds");
    }
}

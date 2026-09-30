using System.Net.Http.Json;
using Catalog.Pricing.V1;
using Catalog.TestSupport;
using Grpc.Core;
using Grpc.Net.Client;
using Shouldly;
using Xunit;
using PricingGrpc = Catalog.Pricing.V1.Pricing;

namespace Catalog.Api.Tests;

/// <summary>§9.7's server half over the real pipeline, from authentication to Dapper on a real database.</summary>
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
    /// A client-credentials principal (§11.3), per call rather than on the channel, so a call can arrive without it.
    /// </summary>
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

        // The invariant text pricing.proto specifies, not parsed back; the trailing zeros are decimal(19,4)'s
        // scale (§7.2), which is why a consumer parses the field rather than comparing it.
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

        // The request shape only: under the fixture's case-insensitive collation this holds without the handler's
        // ToUpperInvariant, which the test below holds.
        reply.Price.Single().Currency.ShouldBe("GBP");
    }

    /// <summary>
    /// The same request against a case-sensitive column, the one configuration where the normalisation acts.
    /// </summary>
    [Fact]
    public async Task A_lower_case_currency_matches_under_a_case_sensitive_collation()
    {
        Guid chair = await PublishAsync("Chair", 49.99m, "GBP");

        // Read rather than assumed, so the restore returns the column to whatever the server had.
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

            // Without the handler's ToUpperInvariant this is empty, the same answer an unknown product gets.
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
            // Value, and no terminator: ScalarAsync's SqlQueryRaw wraps this as a subquery and reads that column.
            """
            SELECT Value = collation_name
            FROM sys.columns
            WHERE object_id = OBJECT_ID('catalog.Products')
                AND name = 'PriceCurrency'
            """);

    /// <summary>
    /// Re-declares <c>PriceCurrency</c> whole, <c>NOT NULL</c> included.
    /// </summary>
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

        // No principal, which is what makes §11.5's client credentials load-bearing rather than ceremonial.
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

        // pricing.proto says "GUIDs in their canonical text form", and Guid.TryParse accepts these spellings too.
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

        // ValidationInterceptor's job: untranslated this is Unknown, which the BFF maps to a 500.
        thrown.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        thrown.Status.Detail.ShouldContain("ProductIds");
    }
}

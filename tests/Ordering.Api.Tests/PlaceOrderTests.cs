using System.Net;
using System.Net.Http.Json;
using Common.Contracts.Ordering.V1;
using Ordering.Application;
using Ordering.Application.Orders;
using Ordering.Application.Orders.PlaceOrder;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§6.4's slice end to end against a real database, over HTTP because it binds a subject (§12.4).</summary>
/// <remarks>
/// Prices are seeded straight into the read model, which has no aggregate a raw INSERT could drift from (§12.4).
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PlaceOrderTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Caller = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>The largest amount recorded, a hundredth below the ceiling since <c>Money.Of</c> rounds.</summary>
    private static readonly decimal LargestStorableAmount = OrderAmounts.Ceiling - 0.01m;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_with_no_priced_products_is_refused_as_a_rule_not_a_bad_request()
    {
        // No row, no price, no order (§6.6); 422 rather than 400, since the request itself was well-formed.
        HttpResponseMessage response = await PlaceAsync(Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_priced_product_commits_an_order_at_the_projected_price()
    {
        // The price comes off the projection and never off the request.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: 2);

        // 200 rather than 201: ToHttpResult maps a successful Result<T> to Results.Ok (§10.5).
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id = await IdOfAsync(response);

        // The handler never called SaveChanges, so a committed row is the transaction behaviour's half (§6.3).
        (await fixture.ScalarAsync<decimal>(
            "SELECT Value = UnitPriceAmount FROM ordering.OrderLines WHERE OrderId = {0}", id))
            .ShouldBe(19.99m);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = Quantity FROM ordering.OrderLines WHERE OrderId = {0}", id))
            .ShouldBe(2);

        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}", id))
            .ShouldBe("AwaitingStock", "stored by name, never by number (§7.2)");
    }

    [Fact]
    public async Task The_order_is_attributed_to_the_caller_and_not_to_anything_in_the_request()
    {
        // §11.4's subject rule: the command carries no CustomerId, so the owner is the request's principal.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 5m, "EUR");

        Guid id = await IdOfAsync(await PlaceAsync(product));

        (await fixture.ScalarAsync<Guid>(
            "SELECT Value = CustomerId FROM ordering.Orders WHERE Id = {0}", id))
            .ShouldBe(Caller);
    }

    [Fact]
    public async Task A_price_in_another_currency_does_not_satisfy_the_order()
    {
        // The projection is keyed by (ProductId, Currency), so a USD price does not match on the id alone.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "USD");

        (await PlaceAsync(product)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_order_past_the_money_columns_ceiling_is_refused_rather_than_placed()
    {
        // Refused before the aggregate exists, since after it OrderPlaced is out and every consumer storing the
        // total would fail on an order already in flight.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, LargestStorableAmount, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: OrderLimits.MaxQuantity);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_order_whose_total_is_exactly_the_ceiling_is_refused()
    {
        // The ceiling is the first amount a money column cannot hold, so a bound written with > would let it in.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, OrderAmounts.Ceiling / 2m, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: 2);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_order_whose_total_the_money_columns_still_hold_is_placed()
    {
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, LargestStorableAmount, "EUR");

        HttpResponseMessage response = await PlaceAsync(product);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unavailable_product_is_not_orderable_though_its_price_is_known()
    {
        // IsAvailable is the reader's filter rather than a deletion, so the price survives unpublishing.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR", available: false);

        (await PlaceAsync(product)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// Under a case-sensitive collation, the one configuration where <c>ProjectedPriceReader</c>'s normalisation
    /// matters; altered only while the collection runs its tests serially, and restored in a <c>finally</c>.
    /// </summary>
    [Fact]
    public async Task A_lower_case_currency_prices_under_a_case_sensitive_collation()
    {
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR");

        // Read rather than assumed, so the restore returns the column to the state it was in.
        string original = await CurrencyCollationAsync();

        await SetCurrencyCollationAsync("Latin1_General_CS_AS");

        try
        {
            HttpResponseMessage response = await PlaceAsync(product, currency: "eur");

            // Without the normalisation the lookup misses and this is a 422.
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await SetCurrencyCollationAsync(original);
        }
    }

    /// <summary>The collation <c>Currency</c> currently carries.</summary>
    private Task<string> CurrencyCollationAsync() =>
        fixture.ScalarAsync<string>(
            // Value, and no terminator: SqlQueryRaw wraps this as a subquery and reads one column by that name.
            """
            SELECT Value = collation_name
            FROM sys.columns
            WHERE object_id = OBJECT_ID('ordering.ProductPrices')
                AND name = 'Currency'
            """);

    [Fact]
    public async Task A_malformed_request_is_a_400_before_the_domain_sees_it()
    {
        // ValidationBehavior's half, translated by §10.5's handler.
        HttpClient client = Authenticated();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                Guid.CreateVersion7(),
                [],
                new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                "EURO"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private HttpClient Authenticated()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Caller.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Write);

        return client;
    }

    /// <summary>A fresh <c>CommandId</c> per call, or a second order would replay the first's result (§8.5).</summary>
    private Task<HttpResponseMessage> PlaceAsync(Guid product, int quantity = 1, string currency = "EUR") =>
        Authenticated().PostAsJsonAsync(
            "/v1/orders",
            new PlaceOrderCommand(
                Guid.CreateVersion7(),
                [new PlaceOrderItem(product, quantity)],
                new AddressDto("1 Test Street", null, "Almaty", "050000", "KZ"),
                currency),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// Re-declares <c>Currency</c> in full with the named collation, around the primary key SQL Server will not
    /// let it change under; the name is interpolated because no parameter is accepted there.
    /// </summary>
    private async Task SetCurrencyCollationAsync(string collation)
    {
        await fixture.ExecuteAsync("ALTER TABLE ordering.ProductPrices DROP CONSTRAINT PK_ProductPrices;");
        await fixture.ExecuteAsync(
            $"ALTER TABLE ordering.ProductPrices ALTER COLUMN Currency char(3) COLLATE {collation} NOT NULL;");
        await fixture.ExecuteAsync(
            """
            ALTER TABLE ordering.ProductPrices
            ADD CONSTRAINT PK_ProductPrices PRIMARY KEY (ProductId, Currency);
            """);
    }

    private static async Task<Guid> IdOfAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<Guid>(TestContext.Current.CancellationToken);

    private Task SeedPriceAsync(Guid product, decimal amount, string currency, bool available = true) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ordering.ProductPrices (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES ({0}, {1}, {2}, {3}, SYSDATETIMEOFFSET());
            """,
            product,
            currency,
            amount,
            available);
}

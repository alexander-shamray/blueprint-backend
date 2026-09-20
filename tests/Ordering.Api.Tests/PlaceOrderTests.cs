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

/// <summary>
/// §6.4's slice end to end against a real database — dispatcher, pipeline,
/// transaction behaviour, repository, EF — so what is proved is the slice, not
/// a re-wiring of it.
/// </summary>
/// <remarks>
/// <b>Here rather than in <c>Ordering.Application.Tests</c>, where §12.1 homes
/// handler tests, and the reason is <see cref="Common.Application.ICurrentUser"/>.</b>
/// Its implementation is <c>HttpContextCurrentUser</c>, so a handler resolved
/// in a bare service scope has no principal and <c>Id</c> throws before any
/// assertion is reached. Catalog's handler tests live at the application level
/// because <c>PublishProductHandler</c> takes no principal; the first handler
/// that binds a subject has to be driven by something that can supply one.
/// Faking an <c>HttpContext</c> in the other project was the alternative, and
/// it needs the framework reference §4.1 keeps out of a plain test project.
/// <para>
/// Prices are seeded straight into <c>ordering.ProductPrices</c>, and since
/// PR-20 that is a choice rather than the only option: the projection fills
/// that table from Catalog's events, and
/// <see cref="CatalogEventEndpointTests"/> drives it that way. This suite is
/// about the write path, so it arranges the read model directly and leaves the
/// broker out — a seed that went through a queue would make every assertion
/// here wait on a delivery it is not testing. A raw INSERT is allowed where
/// §12.4 asks for seeding through the aggregate because the table is a read
/// model with no aggregate behind it, so there is no domain type whose shape
/// it could drift from.
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class PlaceOrderTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Caller = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>
    /// The largest amount this service records: <c>Money.Of</c> rounds to two
    /// places, so the step below the ceiling is a hundredth rather than the
    /// ten-thousandth the column's scale would allow.
    /// </summary>
    private static readonly decimal LargestStorableAmount = OrderAmounts.Ceiling - 0.01m;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_with_no_priced_products_is_refused_as_a_rule_not_a_bad_request()
    {
        // The standing answer for a product this service has never been told a
        // price for in the asked-for currency, which is what §6.6's callout
        // says stays true after the projection exists: no row, no price, no
        // order. Note the condition is silence rather than an absent
        // ProductPublished — PriceChanged reaches the same insert branch, so
        // an id nothing has ever mentioned is what this test needs, and
        // CreateVersion7 below is exactly that. 422 rather than 400 is the
        // point — the request was well-formed and the validator passed it, and
        // products being unpriceable is a fact about this service's state.
        HttpResponseMessage response = await PlaceAsync(Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_priced_product_commits_an_order_at_the_projected_price()
    {
        // The price comes off the projection and never off the request, which
        // is what stops a caller naming their own price.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: 2);

        // 200 rather than 201, and it is the platform's answer rather than
        // this endpoint's: ToHttpResult maps a successful Result<T> to
        // Results.Ok (§10.5), which is what Catalog's POST returns too.
        // Changing it is a Common.Web decision affecting every service and
        // §10.5's table, not one a service PR takes on its own.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Guid id = await IdOfAsync(response);

        // The handler never called SaveChanges — a committed row is the
        // transaction behaviour doing its half (§6.3).
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
        // §11.4's subject rule, asserted where it is enforced. There is no
        // CustomerId on the command to override — the absence is the mechanism
        // — so what this checks is that the stored owner is the principal the
        // request arrived with.
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
        // The projection is keyed by (ProductId, Currency), so a product
        // priced only in USD is unpriceable in EUR — and must be refused
        // rather than matched on the id alone.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "USD");

        (await PlaceAsync(product)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_order_past_the_money_columns_ceiling_is_refused_rather_than_placed()
    {
        // Catalog admits a unit price just under OrderAmounts.Ceiling and
        // OrderLimits.MaxQuantity multiplies it far past it, so the validator
        // passes an order no money column here can record. Refused before the
        // aggregate exists, because after it the endpoint has succeeded and
        // OrderPlaced is out, and the saga insert and every consumer storing
        // the total fail on an order already in flight.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, LargestStorableAmount, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: OrderLimits.MaxQuantity);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_order_whose_total_is_exactly_the_ceiling_is_refused()
    {
        // The ceiling is the first amount a money column cannot hold, so the
        // refusal is inclusive of it. Two of half the ceiling reach it to the
        // hundredth, which is the one total a bound written with > would let
        // through while every larger order was still refused.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, OrderAmounts.Ceiling / 2m, "EUR");

        HttpResponseMessage response = await PlaceAsync(product, quantity: 2);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_order_whose_total_the_money_columns_still_hold_is_placed()
    {
        // The bound refuses what the column cannot hold and nothing short of
        // it, so the largest total the column takes is still an order.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, LargestStorableAmount, "EUR");

        HttpResponseMessage response = await PlaceAsync(product);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unavailable_product_is_not_orderable_though_its_price_is_known()
    {
        // IsAvailable is the reader's filter rather than a deletion, so the
        // history of what a product cost survives it being unpublished.
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR", available: false);

        (await PlaceAsync(product)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    /// <summary>
    /// A lower-case currency finds the projected price under a
    /// <b>case-sensitive</b> collation — the only configuration in which
    /// <c>ProjectedPriceReader</c>'s normalisation does anything at all.
    /// </summary>
    /// <remarks>
    /// <b>Without this the line was covered by nothing.</b> Every fixture here
    /// runs SQL Server's case-insensitive default, so deleting
    /// <c>ToUpperInvariant</c> left the whole suite green while a valid
    /// <c>[A-Za-z]{3}</c> request would answer
    /// <c>order.products_unavailable</c> on a case-sensitive deployment — the
    /// same answer a product nobody has priced gets, which is what makes it
    /// invisible.
    /// <para>
    /// The collation is changed for one test and restored in a
    /// <c>finally</c>. Safe here and nowhere else:
    /// <c>IntegrationCollection</c> is the only collection holding the
    /// fixture and xUnit runs a collection's tests serially, so nothing else
    /// reads this table while it is altered. Respawn resets rows, not schema,
    /// which is why the restore belongs to the test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_lower_case_currency_prices_under_a_case_sensitive_collation()
    {
        Guid product = Guid.CreateVersion7();
        await SeedPriceAsync(product, 19.99m, "EUR");

        // Read rather than assumed, for the reason Catalog's twin states: a
        // hard-coded restore re-collates the column into a state it may never
        // have been in on a server configured differently.
        string original = await CurrencyCollationAsync();

        await SetCurrencyCollationAsync("Latin1_General_CS_AS");

        try
        {
            HttpResponseMessage response = await PlaceAsync(product, currency: "eur");

            // 422 is what this returns with the normalisation removed: the
            // lookup misses, every line is unpriceable, and the order is
            // refused for a reason that has nothing to do with the request.
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
            // Value, and no terminator: ScalarAsync goes through
            // SqlQueryRaw, which wraps this as a subquery and reads one
            // column by that name. The repo's other scalar probes are
            // spelt the same way.
            """
            SELECT Value = collation_name
            FROM sys.columns
            WHERE object_id = OBJECT_ID('ordering.ProductPrices')
                AND name = 'Currency'
            """);

    [Fact]
    public async Task A_malformed_request_is_a_400_before_the_domain_sees_it()
    {
        // ValidationBehavior's half, translated by §10.5's handler. The domain
        // would also refuse an empty item list, and the difference in status
        // is §5.7's division: a bad request is the caller's phrasing, a rule
        // is the model's answer to a well-formed one.
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

    /// <summary>
    /// A fresh <c>CommandId</c> per call, and that is not incidental since
    /// §8.5's behaviour joined the pipeline: several tests here place two
    /// orders, and reusing one value would have the second replay the first's
    /// result instead of running.
    /// </summary>
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
    /// Re-declares <c>Currency</c> with the named collation, around the
    /// primary key that depends on it.
    /// </summary>
    /// <remarks>
    /// The constraint has to go first: SQL Server refuses to alter a column a
    /// key is built on. Catalog's equivalent needs none of this because its
    /// <c>PriceCurrency</c> is an ordinary column — the two services carry the
    /// same normalisation and the same test, and only the schema around it
    /// differs.
    /// <para>
    /// The declaration is restated in full because <c>ALTER COLUMN</c> takes
    /// one rather than a patch, and losing <c>char(3)</c> or <c>NOT NULL</c>
    /// here would silently relax what the migration set. The collation name is
    /// interpolated because SQL Server accepts no parameter in that position;
    /// it is a literal from this file and never from a caller.
    /// </para>
    /// </remarks>
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

using Common.Application;
using Common.Contracts.Catalog.V1;
using Ordering.Application.Orders;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Ordering.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§6.6's price projection against the real table, resolved through the interfaces §6.2 scans.</summary>
/// <remarks>Resolved, never constructed, since the §6.2 scan registers public classes only.</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class ProductPriceProjectionTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>Fixed instants, because every assertion here is about which of two timestamps is larger.</summary>
    private static readonly DateTimeOffset Published = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Later = Published.AddHours(1);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_published_product_becomes_an_available_row_at_its_price()
    {
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        (await AmountAsync(product)).ShouldBe(19.99m);
        (await IsAvailableAsync(product)).ShouldBeTrue(
            "the insert branch writes IsAvailable = 1 rather than leaning on the column default, so a " +
            "product republished after being discontinued comes back");
        (await UpdatedAtAsync(product)).ShouldBe(Published);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_leaves_one_row_untouched()
    {
        // At-least-once is the ordinary case (§9.4); the MATCHED branch's guard refuses an equal OccurredAt.
        Guid product = Guid.CreateVersion7();
        ProductPublished published = Publish(product, 19.99m, "EUR", Published);

        await HandleAsync(published);
        await HandleAsync(published);

        (await RowCountAsync(product)).ShouldBe(1);
        (await AmountAsync(product)).ShouldBe(19.99m);
    }

    [Fact]
    public async Task A_newer_price_replaces_an_older_one()
    {
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        await HandleAsync(PriceOf(product, 24.99m, "EUR", Later));

        (await AmountAsync(product)).ShouldBe(24.99m);
        (await UpdatedAtAsync(product)).ShouldBe(Later);
    }

    [Fact]
    public async Task A_stale_price_does_not_overwrite_a_newer_one()
    {
        // The out-of-order guard, whose failure would be silent: yesterday's amount on the write path.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 24.99m, "EUR", Later));

        await HandleAsync(PriceOf(product, 19.99m, "EUR", Published));

        (await AmountAsync(product)).ShouldBe(
            24.99m,
            "the MATCHED branch fires only when target.UpdatedAt < @OccurredAt, so the older event is a " +
            "no-op rather than the last writer");
        (await UpdatedAtAsync(product)).ShouldBe(Later);
    }

    [Fact]
    public async Task A_discontinued_product_stops_being_readable_without_losing_its_price()
    {
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        await HandleAsync(Discontinue(product, Later));

        (await IsAvailableAsync(product)).ShouldBeFalse();
        (await AmountAsync(product)).ShouldBe(
            19.99m,
            "§6.6 flags rather than deletes, because an order placed last month has to stay explicable");
        (await ReadPriceAsync(product, "EUR")).ShouldBeNull(
            "the reader filters on IsAvailable, so the customer meets the same ProductsUnavailable as for " +
            "a product this service holds no price for at all");
    }

    [Fact]
    public async Task A_stale_discontinue_does_not_withdraw_a_newer_price()
    {
        // The same guard as the MERGE's, on the statement that has no MERGE.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Later));

        await HandleAsync(Discontinue(product, Published));

        (await IsAvailableAsync(product)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_price_published_after_a_withdrawal_makes_the_product_orderable_again()
    {
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));
        await HandleAsync(Discontinue(product, Later));

        await HandleAsync(PriceOf(product, 29.99m, "EUR", Later.AddHours(1)));

        (await IsAvailableAsync(product)).ShouldBeTrue(
            "IsAvailable = 1 on the update branch is what makes re-listing a product a price event rather " +
            "than an operator's UPDATE");
        (await ReadPriceAsync(product, "EUR")).ShouldBe(Money.Of(29.99m, "EUR"));
    }

    [Fact]
    public async Task A_withdrawal_covers_every_currency_the_product_is_priced_in()
    {
        // ProductDiscontinued carries no currency, so a product is withdrawn whole.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));
        await HandleAsync(PriceOf(product, 17.99m, "GBP", Published));

        await HandleAsync(Discontinue(product, Later));

        (await RowCountAsync(product)).ShouldBe(2);
        (await ReadPriceAsync(product, "EUR")).ShouldBeNull();
        (await ReadPriceAsync(product, "GBP")).ShouldBeNull();
    }

    [Fact]
    public async Task A_withdrawal_that_arrives_before_any_price_still_withdraws_the_product()
    {
        // §9.4 guarantees no ordering, and the NOT MATCHED branch has no row to compare against, so the
        // withdrawal must survive having nothing to write to (§6.6).
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Discontinue(product, Later));
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        (await IsAvailableAsync(product)).ShouldBeFalse(
            "the product was withdrawn after this price was published, so the row the late publish " +
            "creates must not be orderable — a discontinued product back on sale is the failure");
        (await ReadPriceAsync(product, "EUR")).ShouldBeNull();
    }

    [Fact]
    public async Task A_withdrawal_reaches_a_currency_it_had_never_seen_a_price_for()
    {
        // A stale price in an unseen currency inserts a fresh row, which only a product-level withdrawal reaches.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 19.99m, "EUR", Published));

        await HandleAsync(Discontinue(product, Later));
        await HandleAsync(PriceOf(product, 17.99m, "GBP", Published.AddMinutes(1)));

        (await ReadPriceAsync(product, "GBP")).ShouldBeNull(
            "the GBP price predates the withdrawal, so projecting it late must not make the product " +
            "orderable in a currency the withdrawal never saw");
    }

    [Fact]
    public async Task A_price_published_after_a_withdrawal_relists_a_currency_that_was_never_priced()
    {
        // A comparison rather than a flag, so a price newer than the withdrawal re-lists the product.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Discontinue(product, Published));

        await HandleAsync(PriceOf(product, 17.99m, "GBP", Later));

        (await ReadPriceAsync(product, "GBP")).ShouldBe(Money.Of(17.99m, "GBP"));
    }

    /// <summary>A tie in <c>OccurredAt</c> ends withdrawn in either order, since only a later event re-lists.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_withdrawal_wins_a_tie_with_a_price_in_either_order(bool withdrawFirst)
    {
        // Seeded strictly earlier, so both events below apply to a row that already exists.
        Guid product = Guid.CreateVersion7();
        await HandleAsync(Publish(product, 9.99m, "EUR", Published));

        if (withdrawFirst)
        {
            await HandleAsync(Discontinue(product, Later));
            await HandleAsync(PriceOf(product, 19.99m, "EUR", Later));
        }
        else
        {
            await HandleAsync(PriceOf(product, 19.99m, "EUR", Later));
            await HandleAsync(Discontinue(product, Later));
        }

        (await IsAvailableAsync(product)).ShouldBeFalse(
            "a tie is not 'later', so it cannot re-list a withdrawn product — and the answer must not " +
            "depend on which of the two the broker happened to deliver first");
    }

    [Fact]
    public async Task Two_currencies_are_two_rows_rather_than_the_second_overwriting_the_first()
    {
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Publish(product, 19.99m, "EUR", Published));
        await HandleAsync(PriceOf(product, 17.99m, "GBP", Published));

        (await RowCountAsync(product)).ShouldBe(
            2,
            "the source clause matches on currency as well as product, which is what makes the composite " +
            "primary key mean something");
        (await ReadPriceAsync(product, "EUR")).ShouldBe(Money.Of(19.99m, "EUR"));
        (await ReadPriceAsync(product, "GBP")).ShouldBe(Money.Of(17.99m, "GBP"));
    }

    [Fact]
    public async Task A_lower_cased_contract_currency_is_stored_upper_cased_and_the_reader_finds_it()
    {
        // Nothing on the wire normalises, so the projection upper-cases what ProjectedPriceReader then looks up.
        Guid product = Guid.CreateVersion7();

        await HandleAsync(Publish(product, 19.99m, "eur", Published));

        (await CurrencyAsync(product)).ShouldBe("EUR");
        (await ReadPriceAsync(product, "EUR")).ShouldBe(Money.Of(19.99m, "EUR"));
    }

    /// <summary>Concurrent deliveries for one key converge on one row with the newest event's amount.</summary>
    /// <remarks>It cannot catch <c>WITH (HOLDLOCK)</c> being removed, a failure §9.8's retry would repair.</remarks>
    [Fact]
    public async Task One_product_and_currency_under_concurrent_delivery_is_still_one_row()
    {
        Guid product = Guid.CreateVersion7();

        await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(i => HandleAsync(PriceOf(product, 10m + i, "EUR", Published.AddMinutes(i)))));

        (await RowCountAsync(product)).ShouldBe(1);
        (await AmountAsync(product)).ShouldBe(
            17m,
            "the guard makes the newest event the winner whatever order the eight ran in");
    }

    private static ProductPublished Publish(Guid product, decimal amount, string currency, DateTimeOffset at) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = at,
            ProductId = product,
            Name = "A product",
            ThumbnailUrl = null,
            Amount = amount,
            Currency = currency
        };

    private static PriceChanged PriceOf(Guid product, decimal amount, string currency, DateTimeOffset at) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = at,
            ProductId = product,
            Amount = amount,
            Currency = currency
        };

    private static ProductDiscontinued Discontinue(Guid product, DateTimeOffset at) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = at,
            ProductId = product
        };

    /// <summary>One scope per delivery, as the consumer gives a handler.</summary>
    private async Task HandleAsync<TEvent>(TEvent integrationEvent)
        where TEvent : class
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider
            .GetRequiredService<IIntegrationEventHandler<TEvent>>()
            .HandleAsync(integrationEvent, TestContext.Current.CancellationToken);
    }

    /// <summary>The real §6.4 port over the row the projection wrote, so the table's two halves meet.</summary>
    private async Task<Money?> ReadPriceAsync(Guid product, string currency)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        IReadOnlyDictionary<ProductId, Money> prices = await scope.ServiceProvider
            .GetRequiredService<IProductPriceReader>()
            .GetAsync([new ProductId(product)], currency, TestContext.Current.CancellationToken);

        return prices.TryGetValue(new ProductId(product), out Money price) ? price : null;
    }

    private Task<int> RowCountAsync(Guid product) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<decimal> AmountAsync(Guid product) =>
        fixture.ScalarAsync<decimal>(
            "SELECT Value = Amount FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<bool> IsAvailableAsync(Guid product) =>
        fixture.ScalarAsync<bool>(
            "SELECT Value = IsAvailable FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<DateTimeOffset> UpdatedAtAsync(Guid product) =>
        fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = UpdatedAt FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);

    private Task<string> CurrencyAsync(Guid product) =>
        fixture.ScalarAsync<string>(
            "SELECT Value = Currency FROM ordering.ProductPrices WHERE ProductId = {0}",
            product);
}

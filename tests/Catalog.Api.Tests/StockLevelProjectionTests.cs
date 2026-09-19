using Catalog.TestSupport;
using Common.Application;
using Common.Contracts.Inventory.V1;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>
/// §3.2's Catalog projection against the real table, driven through the
/// handler interface the §6.2 scan registered it under. No broker: the
/// watermark and the <c>HOLDLOCK</c> are SQL Server's, not the transport's.
/// </summary>
/// <remarks>
/// Resolved, never constructed: a test that constructed the projection would
/// keep passing with the class made internal — the one change that silently
/// unregisters it.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class StockLevelProjectionTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task Apply(Guid product, int level, DateTimeOffset at)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IIntegrationEventHandler<StockLevelChanged> projection =
            scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler<StockLevelChanged>>();
        await projection.HandleAsync(
            new StockLevelChanged
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = product,
                OccurredAt = at,
                ProductId = product,
                QuantityAvailable = level
            },
            TestContext.Current.CancellationToken);
    }

    private Task<int?> Level(Guid product) =>
        fixture.ScalarAsync<int?>(
            "SELECT Value = QuantityAvailable FROM catalog.StockLevels WHERE ProductId = {0}",
            product);

    private Task<int> Rows(Guid product) =>
        fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.StockLevels WHERE ProductId = {0}", product);

    [Fact]
    public async Task A_first_level_inserts()
    {
        var product = Guid.CreateVersion7();

        await Apply(product, 5, T0);

        (await Level(product)).ShouldBe(5);
    }

    [Fact]
    public async Task A_newer_level_replaces_and_an_older_one_arriving_late_does_not()
    {
        var product = Guid.CreateVersion7();
        await Apply(product, 5, T0);

        await Apply(product, 3, T0.AddSeconds(10));
        await Apply(product, 9, T0.AddSeconds(5));

        (await Level(product)).ShouldBe(3, "§9.4 orders nothing; the watermark does");
    }

    [Fact]
    public async Task The_same_level_twice_is_one_row()
    {
        var product = Guid.CreateVersion7();

        await Apply(product, 5, T0);
        await Apply(product, 5, T0);

        (await Rows(product)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_first_deliveries_for_one_product_converge_on_one_row_with_the_newest_level()
    {
        var product = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Apply(product, 10 + i, T0.AddSeconds(i))));

        (await Rows(product)).ShouldBe(
            1,
            "HOLDLOCK on the MERGE is what keeps two NOT MATCHED branches from both inserting");
        (await Level(product)).ShouldBe(
            17,
            "the watermark makes the newest event the winner whatever order the eight ran in");
    }
}

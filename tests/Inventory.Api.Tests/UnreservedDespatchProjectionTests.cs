using System.Diagnostics.Metrics;
using Inventory.Application.Reservations;
using Inventory.Domain.Reservations;
using Inventory.Domain.Reservations.Events;
using Inventory.TestSupport;
using Common.Application;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// §13.3's claim: the projection that flips <c>UnreservedCounted</c> is the
/// one place <see cref="InventoryMetrics.UnreservedDespatch"/> fires, so a
/// row delivered twice counts once.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class UnreservedDespatchProjectionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Two_deliveries_of_one_unreserved_despatch_count_once()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.ExecuteAsync(
            "INSERT INTO inventory.Reservations " +
                "(OrderId, Status, UnavailableProductIds, CreatedAt, UpdatedAt, " +
                "DespatchedUnreservedAt, UnreservedCounted) " +
            "VALUES ({0}, 'Released', '[]', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), 0)",
            order);
        DespatchedUnreservedDomainEvent raised = new(new OrderId(order), DateTimeOffset.UtcNow);
        await fixture.StageOutboxAsync(
            OutboxMessage.Stage(raised, OutboxLane.Local, order, fixture.MessageTypes, fixture.OutboxJson),
            OutboxMessage.Stage(raised, OutboxLane.Local, order, fixture.MessageTypes, fixture.OutboxJson));
        long observed = 0;
        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Inventory.Reservations"
                    && instrument.Name == "inventory.fulfilment.unreserved")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref observed, value));
        listener.Start();

        await fixture.ProcessOutboxBatchAsync();

        Interlocked.Read(ref observed)
            .ShouldBe(1, "§13.3: a counter is a claim against the row, fired once per fact");
        (await fixture.ScalarAsync<int>(
            "SELECT Value = CAST(UnreservedCounted AS int) FROM inventory.Reservations WHERE OrderId = {0}",
            order))
            .ShouldBe(1);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Persistence;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The spec's section 7, against the engine the migrator ran on. The aggregate
/// lands here and nothing drives it until a later slice, so this is what makes
/// the migration real: a table nobody has inserted into is a table nobody has
/// checked.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShipmentsSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_shipments_table_holds_every_column_section_7_names()
    {
        string[] columns = await fixture.ColumnsAsync("shipping", "Shipments");

        columns.ShouldBe(
            [
                "Attempts", "CancellationRefusedAt", "CancellationRequestedAt", "CarrierReference", "CreatedAt",
                "Id", "LockedUntil", "NextAttemptAt", "NextPollAt", "OrderId", "RowVersion",
                "Status", "TerminalAt", "TrackingNumber", "UnfulfillableReason"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_writer_that_predates_CreatedAt_still_inserts_a_shipment_and_the_row_is_stamped()
    {
        // §7.4: while a release rolls out, the version still running inserts
        // with no CreatedAt, and the column's default is what lets it through.
        Guid order = Guid.CreateVersion7();
        DateTimeOffset before = await fixture.DatabaseNowAsync();

        await fixture.ExecuteAsync(
            "INSERT INTO shipping.Shipments (Id, OrderId, Status, Attempts, NextAttemptAt) " +
            "VALUES ({0}, {1}, 'Pending', 0, SYSDATETIMEOFFSET());",
            Guid.CreateVersion7(),
            order);

        DateTimeOffset created = await fixture.ScalarAsync<DateTimeOffset>(
            "SELECT Value = CreatedAt FROM shipping.Shipments WHERE OrderId = {0}", order);
        created.ShouldBeGreaterThanOrEqualTo(before);
    }

    [Fact]
    public async Task The_tracking_events_table_is_keyed_on_the_carriers_own_id()
    {
        string[] columns = await fixture.ColumnsAsync("shipping", "TrackingEvents");

        columns.ShouldBe(["CarrierEventId", "OccurredAt", "RecordedAt", "ShipmentId", "Status"], ignoreOrder: true);

        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.object_id = OBJECT_ID('shipping.TrackingEvents') AND i.is_primary_key = 1
            """))
            .ShouldBe(2, "the key is the pair, so a repeated page is free");
    }

    [Fact]
    public async Task The_carriers_event_id_is_compared_exactly_as_the_aggregate_compares_it()
    {
        // Half a key rather than text, as the inbox's endpoint is: under the
        // default case-insensitive collation two ids differing only by case are
        // one key, and a page the aggregate kept whole fails its commit.
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = collation_name
            FROM sys.columns
            WHERE object_id = OBJECT_ID('shipping.TrackingEvents') AND name = 'CarrierEventId'
            """))
            .ShouldBe("Latin1_General_BIN2");

        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        shipment.Record("ev7Ab", TrackingStatus.InTransit, Now, Now);
        shipment.Record("ev7aB", TrackingStatus.InTransit, Now, Now);

        await SaveAsync(shipment);

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.TrackingEvents WHERE ShipmentId = {0}", shipment.Id.Value))
            .ShouldBe(2);
    }

    [Fact]
    public async Task One_shipment_per_order_is_the_database_s_rule_and_not_only_the_aggregate_s()
    {
        OrderId order = new(Guid.CreateVersion7());

        await SaveAsync(Shipment.For(ShipmentId.New(), order, Now));

        // Two services decide nothing here — this is one consumer redelivered
        // past the inbox, and the unique index is what makes the second write
        // a failure rather than a second shipment nobody reconciles.
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(Shipment.For(ShipmentId.New(), order, Now)));
    }

    [Fact]
    public async Task A_shipment_round_trips_with_its_tracking_events()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        shipment.Record("e1", TrackingStatus.Collected, Now.AddHours(1), Now.AddHours(1));
        shipment.Record("e2", TrackingStatus.Unrecognised, Now.AddHours(2), Now.AddHours(2));

        await SaveAsync(shipment);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        Shipment read = await db.Shipments
            .Include(s => s.TrackingEvents)
            .SingleAsync(s => s.Id == shipment.Id, TestContext.Current.CancellationToken);

        read.Status.ShouldBe(ShipmentStatus.Dispatched);
        read.CarrierReference.ShouldBe("car_1");
        read.CreatedAt.ShouldBe(Now, "the aggregate's own stamp, never the column's default");
        read.Version.ShouldNotBeEmpty("the rowversion is what §6.3's concurrency check reads");
        read.TrackingEvents.Select(e => e.Status).ShouldBe(
            [TrackingStatus.Collected, TrackingStatus.Unrecognised], ignoreOrder: true);

        // By name, never by number (§7.2): an enum stored as an int makes the
        // member order a storage contract.
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM shipping.Shipments WHERE Id = {0}", shipment.Id.Value))
            .ShouldBe("Dispatched");
    }

    private async Task SaveAsync(Shipment shipment)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.Shipments.Add(shipment);
        // The domain events stay on the aggregate here: §7.5's dispatcher runs
        // inside the unit of work, and this test writes through the context
        // directly because the first command that does not comes later.
        shipment.ClearDomainEvents();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

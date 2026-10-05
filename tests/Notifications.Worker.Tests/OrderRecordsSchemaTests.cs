using Common.Contracts.Ordering.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Infrastructure.Persistence;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The order record's table against the engine the migrator ran on.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderRecordsSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_table_holds_the_customer_s_id_the_cancellation_and_nothing_else()
    {
        string[] columns = await fixture.ColumnsAsync("notifications", "OrderRecords");

        // Pseudonymous personal data, so the list is exact: a column added here owes §11.7's erasure path.
        string[] expected = ["CancelOrigin", "CancelReason", "CancelledAt", "CustomerId", "OrderId", "RecordedAt"];
        columns.ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task One_record_per_order_is_the_database_s_rule()
    {
        Guid order = Guid.CreateVersion7();

        await SaveAsync(OrderRecord.For(order, Guid.CreateVersion7(), Now));

        // The second of two first arrivals loses here, and its retried delivery finds the winner's row.
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(OrderRecord.For(order, Guid.CreateVersion7(), Now)));
    }

    [Fact]
    public async Task A_cancelled_record_round_trips_with_its_codes()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        record.Cancel(CancelReasons.CustomerRequest, CancelOrigins.User, Now.AddMinutes(2));

        await SaveAsync(record);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IOrderRecordRepository records = scope.ServiceProvider.GetRequiredService<IOrderRecordRepository>();

        OrderRecord read = (await records.GetAsync(record.OrderId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        read.CustomerId.ShouldBe(record.CustomerId);
        read.CancelledAt.ShouldBe(Now.AddMinutes(2));
        read.CancelReason.ShouldBe(CancelReasons.CustomerRequest);
        read.CancelOrigin.ShouldBe(CancelOrigins.User);
        read.RecordedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task The_notice_repository_finds_a_notice_by_its_unique_key()
    {
        Guid eventId = Guid.CreateVersion7();

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            db.NotificationLog.Add(
                Notification.Pending(
                    eventId,
                    Guid.CreateVersion7(),
                    "order-placed",
                    Guid.CreateVersion7(),
                    """{"v":1}""",
                    Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope reading = fixture.Factory.Services.CreateAsyncScope();
        INotificationRepository notifications = reading.ServiceProvider.GetRequiredService<INotificationRepository>();

        (await notifications.ExistsAsync(eventId, "order-placed", TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await notifications.ExistsAsync(eventId, "order-confirmed", TestContext.Current.CancellationToken))
            .ShouldBeFalse("the key is the event and the template together");
    }

    private async Task SaveAsync(OrderRecord record)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        db.OrderRecords.Add(record);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

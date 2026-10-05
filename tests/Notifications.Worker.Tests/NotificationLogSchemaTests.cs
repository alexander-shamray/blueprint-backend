using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Infrastructure.Persistence;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The record's table on the engine the migrator ran on: nothing writes it until its consumers do.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationLogSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_table_holds_every_column_the_record_names_and_no_mailbox()
    {
        string[] columns = await fixture.ColumnsAsync("notifications", "NotificationLog");

        // ADR-053 rule 4: the record holds no mailbox and no body, so the list is exact rather than a superset.
        columns.ShouldBe(
            [
                "Attempts", "CompletedAt", "CorrelationId", "CreatedAt", "CustomerId", "EventId", "Languages",
                "LockedUntil", "NextAttemptAt", "NotificationId", "OrderId", "Parameters", "Reason", "RowVersion",
                "SendStartedAt", "Status", "TemplateKey", "TemplateVersion"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task The_row_version_is_not_null_as_every_aggregate_table_s_is()
    {
        // A shadow byte[] is optional by convention, so the configuration has to say otherwise.
        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = 'notifications' AND TABLE_NAME = 'NotificationLog' AND COLUMN_NAME = 'RowVersion'
            """))
            .ShouldBe("NO");
    }

    [Fact]
    public async Task One_row_per_event_per_template_is_the_database_s_rule()
    {
        Guid eventId = Guid.CreateVersion7();

        await SaveAsync(Placed(eventId));

        // The inbox drops a redelivery first (§9.5); this is the line behind it.
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(Placed(eventId)));
    }

    [Fact]
    public async Task A_row_round_trips_with_its_status_by_name()
    {
        Guid correlation = Guid.CreateVersion7();
        Notification notification = Notification.Pending(
            Guid.CreateVersion7(),
            correlation,
            "payment-declined",
            Guid.CreateVersion7(),
            """{"v":1}""",
            Now);
        notification.AssignCustomer(Guid.CreateVersion7());
        notification.MarkUndeliverable(NotificationReasons.NoSuchCustomer, Now.AddMinutes(1));

        await SaveAsync(notification);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        Notification read = await db.NotificationLog
            .SingleAsync(n => n.NotificationId == notification.NotificationId, TestContext.Current.CancellationToken);

        read.Status.ShouldBe(NotificationStatus.Undeliverable);
        read.Reason.ShouldBe(NotificationReasons.NoSuchCustomer);
        read.CustomerId.ShouldBe(notification.CustomerId);
        read.CorrelationId.ShouldBe(correlation);

        // By name, never by number (§7.2).
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM notifications.NotificationLog WHERE NotificationId = {0}",
            notification.NotificationId))
            .ShouldBe("Undeliverable");
    }

    private static Notification Placed(Guid eventId) =>
        Notification.Pending(eventId, Guid.CreateVersion7(), "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now);

    private async Task SaveAsync(Notification notification)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        db.NotificationLog.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

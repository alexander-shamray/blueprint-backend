using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Retention;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-053's three windows against their tables, under the fixture's invented deployment.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationsRetentionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_terminal_notice_goes_past_the_log_window_and_a_pending_one_of_any_age_stays()
    {
        Notification old = await EndedAsync(TimeSpan.FromDays(1014));
        Notification inside = await EndedAsync(TimeSpan.FromDays(1012));
        Notification pending = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await fixture.AgeAsync(pending.NotificationId, TimeSpan.FromDays(2000));

        (int notifications, _, _) = await fixture.PurgeNotificationsRetentionAsync();

        notifications.ShouldBe(1, "1013 days is the invented log window");
        (await CountAsync("NotificationLog", "NotificationId", old.NotificationId)).ShouldBe(0);
        (await CountAsync("NotificationLog", "NotificationId", inside.NotificationId)).ShouldBe(1);
        (await CountAsync("NotificationLog", "NotificationId", pending.NotificationId))
            .ShouldBe(1, "a pending notice ends at its give-up age, never at a window");
    }

    [Fact]
    public async Task A_contact_not_refreshed_for_its_window_goes_and_a_fresher_one_stays()
    {
        Guid stale = Guid.CreateVersion7();
        Guid fresher = Guid.CreateVersion7();
        await fixture.StageContactAsync(stale, "aigerim@example.test", null, TimeSpan.FromDays(18));
        await fixture.StageContactAsync(fresher, "dana@example.test", "kk", TimeSpan.FromDays(16));

        (_, int contacts, _) = await fixture.PurgeNotificationsRetentionAsync();

        contacts.ShouldBe(1, "17 days is the invented contact window");
        (await fixture.ContactAsync(stale)).ShouldBeNull();
        (await fixture.ContactAsync(fresher)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_order_record_past_its_window_goes_only_once_no_pending_notice_names_its_order()
    {
        Guid unnamed = await AgedOrderAsync(TimeSpan.FromDays(72));
        Guid waitedOn = await AgedOrderAsync(TimeSpan.FromDays(72));
        Notification waiting = await fixture.PendingAsync(TemplateKeys.ShipmentDelivered, waitedOn);
        Guid finished = await AgedOrderAsync(TimeSpan.FromDays(72));
        await EndAsync(await fixture.PendingAsync(TemplateKeys.OrderPlaced, finished), TimeSpan.FromDays(1));
        Guid inside = await AgedOrderAsync(TimeSpan.FromDays(70));

        (_, _, int orders) = await fixture.PurgeNotificationsRetentionAsync();

        orders.ShouldBe(2);
        (await CountAsync("OrderRecords", "OrderId", unnamed)).ShouldBe(0);
        (await CountAsync("OrderRecords", "OrderId", finished)).ShouldBe(0, "a terminal notice waits on nothing");
        (await CountAsync("OrderRecords", "OrderId", waitedOn))
            .ShouldBe(1, "deleting it would turn the waiting notice into a give-up");
        (await CountAsync("OrderRecords", "OrderId", inside)).ShouldBe(1, "71 days is the invented order window");

        await EndAsync(waiting, TimeSpan.FromMinutes(1));
        (_, _, int later) = await fixture.PurgeNotificationsRetentionAsync();

        later.ShouldBe(1, "once nothing waits on it, the record goes on its window");
        (await CountAsync("OrderRecords", "OrderId", waitedOn)).ShouldBe(0);
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_within_the_pass()
    {
        const int backlog = NotificationsRetentionService.BatchSize + 100;
        await fixture.ExecuteAsync(
            """
            INSERT INTO notifications.ContactRecords (CustomerId, Email, Locale, FetchedAt)
            SELECT TOP ({0}) NEWID(), N'someone@example.test', NULL, DATEADD(day, -18, SYSDATETIMEOFFSET())
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """,
            backlog);

        (_, int contacts, _) = await fixture.PurgeNotificationsRetentionAsync();

        contacts.ShouldBe(backlog);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM notifications.ContactRecords")).ShouldBe(0);
    }

    [Fact]
    public async Task No_line_of_the_purge_holds_a_mailbox()
    {
        await fixture.StageContactAsync(
            Guid.CreateVersion7(),
            "aigerim.private@example.test",
            "kk",
            TimeSpan.FromDays(18));

        await fixture.PurgeNotificationsRetentionAsync();

        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("Notifications retention deleted", StringComparison.Ordinal),
            "the pass's own line must be in the capture, or the absence below proves nothing");
        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => line.Contains("aigerim.private", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Notification> EndedAsync(TimeSpan ago)
    {
        Notification notification = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await EndAsync(notification, ago);

        return notification;
    }

    private Task EndAsync(Notification notification, TimeSpan ago) =>
        fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Status = 'Sent', " +
            "CompletedAt = DATEADD(minute, -{1}, SYSDATETIMEOFFSET()) WHERE NotificationId = {0};",
            notification.NotificationId,
            (int)ago.TotalMinutes);

    private async Task<Guid> AgedOrderAsync(TimeSpan ago)
    {
        Guid order = Guid.CreateVersion7();
        await fixture.OrderAsync(order, Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            "UPDATE notifications.OrderRecords SET RecordedAt = DATEADD(minute, -{1}, SYSDATETIMEOFFSET()) " +
            "WHERE OrderId = {0};",
            order,
            (int)ago.TotalMinutes);

        return order;
    }

    private Task<int> CountAsync(string table, string key, Guid id) =>
        fixture.ScalarAsync<int>($"SELECT Value = COUNT(*) FROM notifications.{table} WHERE {key} = {{0}}", id);
}

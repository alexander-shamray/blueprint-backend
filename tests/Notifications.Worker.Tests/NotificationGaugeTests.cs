using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Observability;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Notices past their first backoff by step, and the wait of the longest-due notice no pass holds.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationGaugeTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Each_waiting_notice_is_counted_under_the_step_it_waits_on()
    {
        Notification noRecord = await fixture.PendingAsync(TemplateKeys.ShipmentDispatched, Guid.CreateVersion7());

        Guid declined = Guid.CreateVersion7();
        await fixture.OrderAsync(declined, Guid.CreateVersion7());
        Notification noCancellation = await fixture.PendingAsync(TemplateKeys.PaymentDeclined, declined);

        Guid placed = Guid.CreateVersion7();
        await fixture.OrderAsync(placed, Guid.CreateVersion7());
        Notification noContact = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, placed);
        Notification noRelay = await fixture.PendingAsync(TemplateKeys.OrderPlaced, placed);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET SendStartedAt = SYSDATETIMEOFFSET() WHERE NotificationId = {0};",
            noRelay.NotificationId);

        foreach (Notification waiting in new[] { noRecord, noCancellation, noContact, noRelay })
            await fixture.SetAttemptsAsync(waiting.NotificationId, 1);

        ReadGauge("notifications.waiting").ShouldBe(
            [("step=order_record", 2), ("step=contact", 1), ("step=relay", 1)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_notice_no_pass_has_failed_and_a_finished_one_wait_on_nothing()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.OrderAsync(order, Guid.CreateVersion7());
        await fixture.PendingAsync(TemplateKeys.OrderPlaced, order);
        Notification finished = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Status = 'Sent', Attempts = 2, " +
            "CompletedAt = SYSDATETIMEOFFSET() WHERE NotificationId = {0};",
            finished.NotificationId);

        ReadGauge("notifications.waiting").ShouldBe(
            [("step=order_record", 0), ("step=contact", 0), ("step=relay", 0)],
            ignoreOrder: true,
            "every step reports, as a step missing from a max reads as a healthy zero");
    }

    [Fact]
    public async Task A_due_notice_no_pass_holds_reads_as_its_wait_past_two_ticks()
    {
        Notification due = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await DueAsync(due, TimeSpan.FromSeconds(120));

        (string tags, double overdue) = ReadGauge("notifications.overdue").ShouldHaveSingleItem();

        tags.ShouldBeEmpty("one pass, so no pass attribute");
        double grace = NotificationStats.OverdueGrace.TotalSeconds;
        overdue.ShouldBeInRange(120 - grace, 180 - grace);
    }

    [Fact]
    public async Task A_notice_due_inside_two_ticks_reads_as_no_wait()
    {
        Notification due = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await DueAsync(due, TimeSpan.FromSeconds(3));

        ReadGauge("notifications.overdue").ShouldHaveSingleItem().Value.ShouldBe(0);
    }

    [Fact]
    public async Task A_notice_a_pass_holds_has_no_wait()
    {
        // Due long ago and being sent: a MIN over the due column alone would read it.
        Notification held = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await DueAsync(held, TimeSpan.FromMinutes(10));
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET LockedUntil = DATEADD(second, 30, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            held.NotificationId);

        ReadGauge("notifications.overdue").ShouldHaveSingleItem().Value.ShouldBe(0);
    }

    [Fact]
    public void A_stats_reader_that_throws_reports_neither_gauge_rather_than_a_healthy_reading()
    {
        List<(string Tags, double Value)> measured = Read(new Throwing(), "notifications.waiting", allowEmpty: true);
        List<(string Tags, double Value)> overdue = Read(new Throwing(), "notifications.overdue", allowEmpty: true);

        measured.ShouldBeEmpty();
        overdue.ShouldBeEmpty();
    }

    private Task DueAsync(Notification notification, TimeSpan ago) =>
        fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET NextAttemptAt = DATEADD(second, -{1}, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notification.NotificationId,
            (int)ago.TotalSeconds);

    /// <summary>One of the gauges, read once over this suite's own stats reader on the fixture's database.</summary>
    private List<(string Tags, double Value)> ReadGauge(string instrument)
    {
        using NotificationStats stats = new(new SqlConnectionFactory(fixture.ConnectionString));

        return Read(stats, instrument, allowEmpty: false);
    }

    private static List<(string Tags, double Value)> Read(INotificationStats stats, string instrument, bool allowEmpty)
    {
        // The factory has to outlive the collection: a Meter disposed with its factory publishes nothing.
        using ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory factory = services.GetRequiredService<IMeterFactory>();
        NotificationMetrics metrics = new(factory, stats, NullLogger<NotificationMetrics>.Instance);
        metrics.ShouldNotBeNull();

        // The same Meter the constructor used, since IMeterFactory caches by name.
        Meter mine = factory.Create(OutboundMeter.Name);
        List<(string Tags, double Value)> measured = [];
        bool enabled = false;
        using MeterListener listener = new();

        listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == instrument)
            {
                l.EnableMeasurementEvents(published);
                enabled = true;
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            measured.Add((string.Join(",", Pairs(tags)), value)));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: with nothing enabled, an assertion over an empty list asserts nothing.
        enabled.ShouldBeTrue($"the listener enabled no {instrument} on this meter");
        if (!allowEmpty)
            measured.ShouldNotBeEmpty();

        return measured;
    }

    private static List<string> Pairs(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        List<string> pairs = [];
        foreach (KeyValuePair<string, object?> tag in tags)
            pairs.Add($"{tag.Key}={tag.Value}");

        return pairs;
    }

    private sealed class Throwing : INotificationStats
    {
        public IReadOnlyDictionary<string, int> WaitingByStep() => throw new InvalidOperationException("Staged.");

        public double OverdueSeconds() => throw new InvalidOperationException("Staged.");
    }
}

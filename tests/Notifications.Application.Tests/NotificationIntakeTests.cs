using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.Logging;
using Notifications.Application.Intake;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>
/// §3.2's Consumes column at the command: each event writes one pending row and calls nothing, and Ordering's three
/// write the order record in whichever order they arrive, to one final state (ADR-049).
/// </summary>
public class NotificationIntakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cancelled = Now.AddMinutes(7);

    private readonly FakeNotifications _notifications = new();
    private readonly FakeOrderRecords _orders = new();
    private readonly CapturingLogger<RecordNotificationHandler> _log = new();

    private RecordNotificationHandler Handler() => new(_notifications, _orders, new FixedClock(Now), _log);

    private static NotificationParameters For(Guid order) => new() { OrderId = order, OccurredAt = Now };

    private static RecordNotificationCommand Placed(Guid order, Guid customer) =>
        new(
            Guid.CreateVersion7(),
            TemplateKeys.OrderPlaced,
            For(order) with { Amount = 42.10m, Currency = "KZT" },
            new OrderFact(customer, Cancellation: null));

    private static RecordNotificationCommand Confirmed(Guid order, Guid customer) =>
        new(
            Guid.CreateVersion7(),
            TemplateKeys.OrderConfirmed,
            For(order) with { Amount = 42.10m, Currency = "KZT" },
            new OrderFact(customer, Cancellation: null));

    private static RecordNotificationCommand CancelledBy(Guid order, Guid customer, string reason, string? origin) =>
        new(
            Guid.CreateVersion7(),
            TemplateKeys.OrderCancelled,
            For(order) with { CancelReason = reason },
            new OrderFact(customer, new OrderCancellation(reason, origin, Cancelled)));

    private static RecordNotificationCommand Declined(Guid order) =>
        new(Guid.CreateVersion7(), TemplateKeys.PaymentDeclined, For(order), Order: null);

    /// <summary>Every order the three of Ordering's events can arrive in, since §9.4 orders none of them.</summary>
    public static TheoryData<string[]> EveryArrivalOrder => new()
    {
        { ["placed", "confirmed", "cancelled"] },
        { ["placed", "cancelled", "confirmed"] },
        { ["confirmed", "placed", "cancelled"] },
        { ["confirmed", "cancelled", "placed"] },
        { ["cancelled", "placed", "confirmed"] },
        { ["cancelled", "confirmed", "placed"] }
    };

    [Fact]
    public async Task An_event_owes_one_pending_notice_naming_its_order_and_nobody_yet()
    {
        Guid order = Guid.CreateVersion7();
        RecordNotificationCommand command = Placed(order, Guid.CreateVersion7());

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        Notification row = _notifications.Added.ShouldHaveSingleItem();
        row.EventId.ShouldBe(command.EventId);
        row.TemplateKey.ShouldBe(TemplateKeys.OrderPlaced);
        row.OrderId.ShouldBe(order);
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.CustomerId.ShouldBeNull("the worker copies the customer from the order record");
        row.CreatedAt.ShouldBe(Now);
        ParametersFormat.Read(row.Parameters).ShouldBe(command.Parameters);
    }

    [Fact]
    public async Task A_redelivery_past_the_inbox_records_no_second_notice()
    {
        RecordNotificationCommand command = Placed(Guid.CreateVersion7(), Guid.CreateVersion7());

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);
        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        _notifications.Added.Count.ShouldBe(1, "the unique key is the line behind §9.5's inbox, and it is not reached");
        _orders.Added.Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(EveryArrivalOrder))]
    public async Task Ordering_s_three_events_leave_one_record_in_every_arrival_order(string[] arrivals)
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        foreach (string arrival in arrivals)
        {
            RecordNotificationCommand command = arrival switch
            {
                "placed" => Placed(order, customer),
                "confirmed" => Confirmed(order, customer),
                _ => CancelledBy(order, customer, CancelReasons.PaymentDeclined, CancelOrigins.Workflow)
            };

            await Handler().HandleAsync(command, TestContext.Current.CancellationToken);
        }

        OrderRecord record = _orders.Added.ShouldHaveSingleItem("whichever arrives first creates it, alone");
        record.CustomerId.ShouldBe(customer);
        record.CancelledAt.ShouldBe(Cancelled, "a cancellation is never cleared by a later Placed or Confirmed");
        record.CancelReason.ShouldBe(CancelReasons.PaymentDeclined);
        record.CancelOrigin.ShouldBe(CancelOrigins.Workflow);
        _notifications.Added.Select(n => n.TemplateKey).ShouldBe(
            [TemplateKeys.OrderPlaced, TemplateKeys.OrderConfirmed, TemplateKeys.OrderCancelled],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_cancellation_first_is_a_tombstone_the_late_placement_leaves_alone()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await Handler().HandleAsync(
            CancelledBy(order, customer, CancelReasons.CustomerRequest, CancelOrigins.User),
            TestContext.Current.CancellationToken);

        OrderRecord tombstone = _orders.Added.ShouldHaveSingleItem();
        tombstone.CancelOrigin.ShouldBe(CancelOrigins.User);

        await Handler().HandleAsync(Placed(order, customer), TestContext.Current.CancellationToken);

        _orders.Added.ShouldHaveSingleItem().ShouldBeSameAs(tombstone);
        tombstone.CancelledAt.ShouldBe(Cancelled);
        tombstone.CancelOrigin.ShouldBe(CancelOrigins.User);
    }

    [Fact]
    public async Task A_second_cancellation_keeps_the_first_and_says_so()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        await Handler().HandleAsync(
            CancelledBy(order, customer, CancelReasons.PaymentDeclined, CancelOrigins.Workflow),
            TestContext.Current.CancellationToken);

        await Handler().HandleAsync(
            CancelledBy(order, customer, CancelReasons.CustomerRequest, CancelOrigins.User),
            TestContext.Current.CancellationToken);

        _orders.Added.ShouldHaveSingleItem().CancelOrigin.ShouldBe(CancelOrigins.Workflow);
        _log.Entries.ShouldContain(e => e.EventId.Name == "CancellationKept");
    }

    [Fact]
    public async Task An_event_naming_no_customer_writes_no_order_record()
    {
        await Handler().HandleAsync(Declined(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        _notifications.Added.ShouldHaveSingleItem().TemplateKey.ShouldBe(TemplateKeys.PaymentDeclined);
        _orders.Added.ShouldBeEmpty("four events name an order alone, and the row waits for Ordering's to name one");
    }

    [Fact]
    public async Task A_tracking_number_that_fails_the_check_is_dropped_and_the_notice_still_recorded()
    {
        // A right-to-left override, which would display a stranger's text in an order nobody wrote.
        string spoofed = $"1Z999{(char)0x202E}AA1";
        RecordNotificationCommand command = new(
            Guid.CreateVersion7(),
            TemplateKeys.ShipmentDispatched,
            For(Guid.CreateVersion7()) with { TrackingNumber = spoofed },
            Order: null);

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        Notification row = _notifications.Added.ShouldHaveSingleItem();
        ParametersFormat.Read(row.Parameters).TrackingNumber.ShouldBeNull();
        row.Parameters.ShouldNotContain("1Z999");

        (LogLevel level, EventId id, string message) = _log.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        id.Name.ShouldBe("Dropped");
        message.ShouldContain(nameof(NotificationParameters.TrackingNumber));
        message.ShouldNotContain("1Z999", Case.Sensitive, "the log names the member, never another service's value");
    }

    [Fact]
    public async Task A_currency_and_a_cancellation_s_codes_that_fail_the_check_are_dropped()
    {
        Guid order = Guid.CreateVersion7();
        RecordNotificationCommand command = new(
            Guid.CreateVersion7(),
            TemplateKeys.OrderCancelled,
            For(order) with { Currency = "kzt", CancelReason = "Out Of Stock" },
            new OrderFact(Guid.CreateVersion7(), new OrderCancellation("Out Of Stock", "SYSTEM", Cancelled)));

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        NotificationParameters stored = ParametersFormat.Read(_notifications.Added.ShouldHaveSingleItem().Parameters);
        stored.Currency.ShouldBeNull();
        stored.CancelReason.ShouldBeNull("the renderer phrases an absent code with its map's generic phrase");

        OrderRecord record = _orders.Added.ShouldHaveSingleItem();
        record.CancelledAt.ShouldBe(Cancelled, "the cancellation is the fact; a malformed reason does not unmake it");
        record.CancelReason.ShouldBeNull();
        record.CancelOrigin.ShouldBeNull();
    }

    private sealed class FakeNotifications : INotificationRepository
    {
        public List<Notification> Added { get; } = [];

        public Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct) =>
            Task.FromResult(Added.Exists(n => n.EventId == eventId && n.TemplateKey == templateKey));

        public Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct) =>
            Task.FromResult(Added.Find(n => n.NotificationId == notificationId));

        public void Add(Notification notification) => Added.Add(notification);
    }

    private sealed class FakeOrderRecords : IOrderRecordRepository
    {
        public List<OrderRecord> Added { get; } = [];

        public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
            Task.FromResult(Added.Find(r => r.OrderId == orderId));

        public void Add(OrderRecord record) => Added.Add(record);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The formatted message rather than the raw state, so an assertion can read what a log store would hold.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}

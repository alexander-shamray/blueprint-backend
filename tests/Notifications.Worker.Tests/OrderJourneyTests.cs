using Common.Contracts.Ordering.V1;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>An order's events, as their contracts carry them, through the queue and the worker to the relay.</summary>
/// <remarks>
/// The service's half of the journey (§3.2); that each publisher sends these shapes is its own suite's to hold, and
/// §12.6's contract tests between them.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderJourneyTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_placed_confirmed_despatched_and_delivered_is_four_sent_notices_and_four_messages()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, At.AddMinutes(1)));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At.AddDays(1)));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At.AddDays(2)));

        await fixture.SendUntilSettledAsync();

        IReadOnlyList<Notification> rows = await fixture.NotificationsAsync(order);
        rows.Select(n => n.TemplateKey).ShouldBe(
            [
                TemplateKeys.OrderPlaced,
                TemplateKeys.OrderConfirmed,
                TemplateKeys.ShipmentDispatched,
                TemplateKeys.ShipmentDelivered
            ],
            ignoreOrder: true);
        rows.ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.CustomerId == customer);

        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(4, Ct);
        delivered.Select(m => m.MessageId.Trim('<', '>')).ShouldBe(
            rows.Select(n => $"{n.EventId:N}.{n.TemplateKey}@commerce.test"),
            ignoreOrder: true,
            "one message per event, each under its own row's Message-ID");
        delivered.ShouldAllBe(m => m.To.Single().Address == Mailbox);

        MailpitMessage despatch = await fixture.Relay.MessageAsync(
            delivered.Single(m => m.MessageId.Contains(TemplateKeys.ShipmentDispatched, StringComparison.Ordinal)).Id,
            Ct);
        despatch.Text.ShouldContain("ZZ-0042", Case.Sensitive, "the carrier's tracking number reaches the customer");
    }

    [Fact]
    public async Task A_cancelled_order_is_told_of_its_cancellation_and_why()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(
                order,
                customer,
                At.AddMinutes(5),
                CancelReasons.CustomerRequest,
                CancelOrigins.User));

        await fixture.SendUntilSettledAsync();

        (await fixture.NotificationsAsync(order)).ShouldAllBe(n => n.Status == NotificationStatus.Sent);
        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(2, Ct);
        MailpitMessage cancellation = await fixture.Relay.MessageAsync(
            delivered.Single(m => m.MessageId.Contains(TemplateKeys.OrderCancelled, StringComparison.Ordinal)).Id,
            Ct);

        string phrase = TemplateSet.Embedded.Reasons(1, "en")![CancelReasons.CustomerRequest];
        cancellation.Text.ShouldContain(phrase, Case.Sensitive, "a cancellation says why, in Ordering's codes");
    }
}

using System.Text.Json;
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>§3.2's Consumes column over a real broker and the real tables, as the narrow account runs them.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationsEventsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Account = "notifications-svc";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The grant the broker is imported from, read where it is owned.</summary>
    private static (string Configure, string Write, string Read) ImportedGrant(string user)
    {
        string path = Path.Combine(RepositoryRoot.Locate(), "deploy", "compose", "rabbitmq", "definitions.json");
        using JsonDocument definitions = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement grant = definitions.RootElement
            .GetProperty("permissions")
            .EnumerateArray()
            .Single(p => p.GetProperty("user").GetString() == user && p.GetProperty("vhost").GetString() == "/");

        return (
            grant.GetProperty("configure").GetString()!,
            grant.GetProperty("write").GetString()!,
            grant.GetProperty("read").GetString()!);
    }

    [Fact]
    public async Task The_narrow_account_binds_every_event_in_the_consumes_column_to_the_queue()
    {
        // Healthy first, since an endpoint declares its bindings as it starts, and a refused bind never starts (§13.5).
        BusHealthStatus health = await fixture.Factory.Services
            .GetRequiredService<IBusControl>()
            .WaitForHealthStatus(BusHealthStatus.Healthy, ServiceFixture.StepDeadline);
        health.ShouldBe(BusHealthStatus.Healthy, "a refused exchange.bind closes the channel and the endpoint with it");

        string[] bound = await fixture.BindingsAsync(MessagingRegistration.EventsQueue);

        string[] exchanges =
        [
            "Common.Contracts.Ordering.V1:OrderPlaced",
            "Common.Contracts.Ordering.V1:OrderConfirmed",
            "Common.Contracts.Ordering.V1:OrderCancelled",
            "Common.Contracts.Payments.V1:PaymentDeclined",
            "Common.Contracts.Payments.V1:PaymentRefunded",
            "Common.Contracts.Shipping.V1:ShipmentDispatched",
            "Common.Contracts.Shipping.V1:ShipmentDelivered"
        ];
        foreach (string exchange in exchanges)
        {
            bound.ShouldContain(exchange, $"{exchange} is in §3.2's Consumes column and is not bound");
        }
    }

    [Fact]
    public async Task The_account_holds_exactly_the_grant_definitions_json_ships()
    {
        // The binding measurement holds only if nothing widened the grant, so the broker's own answer is read.
        (string configure, string write, string read) = await fixture.BrokerPermissionsAsync(Account);

        (configure, write, read).ShouldBe(ImportedGrant(Account));
        write.ShouldNotContain("Common", Case.Sensitive, "ADR-036: nothing to publish, so no contract to write");
    }

    [Fact]
    public async Task Each_of_the_seven_events_owes_one_pending_notice_under_its_key()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.PaymentDeclined, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Refunded(order, At));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At));

        IReadOnlyList<Notification> owed = await fixture.NotificationsAsync(order);

        owed.Select(n => n.TemplateKey).ShouldBe(TemplateKeys.Placeholders.Keys, ignoreOrder: true);
        owed.ShouldAllBe(n => n.Status == NotificationStatus.Pending && n.CustomerId == null);
        ParametersFormat
            .Read(owed.Single(n => n.TemplateKey == TemplateKeys.PaymentDeclined).Parameters)
            .ShouldBe(new NotificationParameters { OrderId = order, OccurredAt = At });
    }

    [Fact]
    public async Task A_cancellation_first_is_a_tombstone_the_late_placement_leaves_alone_over_the_broker()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.CustomerRequest, CancelOrigins.User));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At.AddMinutes(1)));

        OrderRecord record = (await fixture.OrderRecordAsync(order)).ShouldNotBeNull();
        record.CustomerId.ShouldBe(customer);
        record.CancelledAt.ShouldBe(At);
        record.CancelReason.ShouldBe(CancelReasons.CustomerRequest);
        record.CancelOrigin.ShouldBe(CancelOrigins.User);
        (await fixture.NotificationsAsync(order)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_placement_first_is_cancelled_by_the_late_cancellation()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At.AddMinutes(1), CancelReasons.PaymentTimeout, origin: null));

        OrderRecord record = (await fixture.OrderRecordAsync(order)).ShouldNotBeNull();
        record.CancelledAt.ShouldBe(At.AddMinutes(1));
        record.CancelReason.ShouldBe(CancelReasons.PaymentTimeout);
        record.CancelOrigin.ShouldBeNull("an older publisher's cancellation, which ADR-049's reader interprets");
    }

    [Fact]
    public async Task A_tracking_number_with_a_bidi_override_is_dropped()
    {
        Guid order = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At, trackingNumber: $"ZZ{(char)0x202E}0042"));

        Notification owed = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        ParametersFormat.Read(owed.Parameters).TrackingNumber.ShouldBeNull();
    }
}

using System.Text.Json;
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Web.Bff.Orders;
using Xunit;
using MessagingRegistration = Web.Bff.Messaging.DependencyInjection;

namespace Web.Bff.Tests;

/// <summary>The BFF's row in §3.2 over a real broker and the real tables, as the narrow account runs them.</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class BffOrderEventsTests(BffServiceFixture fixture) : IAsyncLifetime
{
    private const string Account = "bff-svc";

    private static readonly DateTimeOffset At = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The grant the broker is imported from, read where it is owned.</summary>
    private static (string Configure, string Write, string Read) ImportedGrant(string user)
    {
        using JsonDocument definitions = JsonDocument.Parse(
            File.ReadAllText(RepositoryFile.Locate("deploy/compose/rabbitmq/definitions.json")));
        JsonElement grant = definitions.RootElement.GetProperty("permissions")
            .EnumerateArray()
            .Single(p => p.GetProperty("user").GetString() == user && p.GetProperty("vhost").GetString() == "/");

        return (
            grant.GetProperty("configure").GetString()!,
            grant.GetProperty("write").GetString()!,
            grant.GetProperty("read").GetString()!);
    }

    [Fact]
    public async Task The_narrow_account_binds_every_event_in_the_bff_row_to_the_queue()
    {
        // Healthy first, since an endpoint declares its bindings as it starts, and a refused bind never starts.
        BusHealthStatus health = await fixture.Factory.Services.GetRequiredService<IBusControl>()
            .WaitForHealthStatus(BusHealthStatus.Healthy, BffServiceFixture.StepDeadline);
        health.ShouldBe(BusHealthStatus.Healthy, "a refused exchange.bind closes the channel and the endpoint with it");

        string[] bound = await fixture.BindingsAsync(MessagingRegistration.EventsQueue);

        foreach (Type consumed in MessagingRegistrationTests.Consumed)
        {
            string exchange = $"{consumed.Namespace}:{consumed.Name}";
            bound.ShouldContain(exchange, $"{exchange} is in the BFF's Consumes cell and is not bound");
        }
    }

    [Fact]
    public async Task The_account_holds_exactly_the_grant_definitions_json_ships()
    {
        (string configure, string write, string read) = await fixture.BrokerPermissionsAsync(Account);

        (configure, write, read).ShouldBe(ImportedGrant(Account));
        write.ShouldNotContain("Common", Case.Sensitive, "ADR-036: nothing to publish, so no contract to write");
    }

    [Fact]
    public async Task An_order_s_events_through_the_queue_make_the_row_section_10_7_describes()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Published(OrderEvents.Lamp, "Walnut desk lamp", At.AddDays(-30)));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At.AddDays(1)));
        await fixture.DeliverAsync(OrderEvents.Authorised(order, At.AddSeconds(5)));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, At.AddSeconds(7)));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At.AddDays(2)));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CustomerId.ShouldBe(customer);
        row.TrackingNumber.ShouldBe(OrderEvents.TrackingNumber);
        BuyerStatus.Of(
            new OrderSteps(
                row.PlacedAt,
                row.ConfirmedAt,
                row.DispatchedAt,
                row.DeliveredAt,
                row.CancelledAt,
                row.CancelOutcome)).ShouldBe(BuyerStatuses.Delivered);

        (await fixture.LinesAsync(order)).ShouldHaveSingleItem().ProductId.ShouldBe(OrderEvents.Lamp);
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Name FROM bff.Products WHERE ProductId = {0}",
            OrderEvents.Lamp)).ShouldBe("Walnut desk lamp");
    }

    [Fact]
    public async Task A_cancelled_and_refunded_order_reads_its_member_with_the_refund_beside_it()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Refunded(order, At.AddMinutes(3)));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(
                order,
                customer,
                At.AddMinutes(2),
                CancelReasons.PaymentDeclined,
                CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));

        ProjectedOrder row = (await fixture.OrderAsync(order)).ShouldNotBeNull();
        row.CancelOutcome.ShouldBe(BuyerStatuses.Declined);
        row.RefundedAt.ShouldBe(At.AddMinutes(3), "a refund is a flag beside the status, never a member of it (§10.7)");
    }
}

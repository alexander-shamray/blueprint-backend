using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>§9.6's saga in <c>AwaitingConfirmation</c>.</summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaAwaitingConfirmationTests
{
    [Fact]
    public async Task A_cancellation_before_the_confirmation_lands_releases_the_stock()
    {
        // Nothing downstream has been told yet, so the reservation is released rather than stranded.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-126"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // No review row: Payments voids off OrderCancelled itself (§9.6).
            harness.Sent
                .Select<FlagOrderForReview>(Spent())
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_confirmation_that_never_arrives_escalates_rather_than_hanging()
    {
        // The command never consumed at all, with the card authorised and the stock held.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-128"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            // Driven rather than waited out.
            await Publish(harness, new ConfirmationExpired(orderId));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.NotConfirmed))
                .ShouldBeTrue();

            // No CancelOrder: §3.2 gives Ordering no refund command to compensate with.
            harness.Sent
                .Select<CancelOrder>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(0);

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_that_beats_the_confirmation_still_marks_the_order_shipped()
    {
        // Shipping also consumes OrderConfirmed (§3.2), so a despatch proves the confirmation committed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-early"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingConfirmation)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-EARLY"));

            (await Sent<MarkOrderShipped>(harness, m => m.OrderId == orderId && m.TrackingNumber == "TRACK-EARLY"))
                .ShouldBeTrue();

            ConsumeFaults<ShipmentDispatched>(harness).ShouldAllBe(e => e == null);

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_confirmation_after_an_early_release_escalates_on_its_way_to_Confirmed()
    {
        // The transition still happens, because the aggregate committed the status; the guard adds the row.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-3"));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull(
                "the confirmation is a fact and the machine still records it");
        }
    }

    [Fact]
    public async Task A_despatch_beating_the_confirmation_after_an_early_release_escalates_too()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-2"));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRK-10"));

            (await Sent<MarkOrderShipped>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();
        }
    }
}

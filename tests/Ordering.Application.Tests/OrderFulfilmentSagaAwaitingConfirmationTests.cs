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

/// <summary>
/// §9.6's saga in <c>AwaitingConfirmation</c>: what each event it can
/// receive there does.
/// </summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaAwaitingConfirmationTests
{
    [Fact]
    public async Task A_cancellation_before_the_confirmation_lands_releases_the_stock()
    {
        // AwaitingConfirmation means ConfirmOrder is in flight and nothing
        // downstream has been told, so Shipping has no despatch to prepare and
        // the reservation is released rather than stranded.
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

            // No review row: Payments voids off OrderCancelled itself (§3.2),
            // and what makes the confirmed case a human's problem is a despatch
            // that might already be moving, of which there is none here.
            harness.Sent
                .Select<FlagOrderForReview>(Spent())
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task A_confirmation_that_never_arrives_escalates_rather_than_hanging()
    {
        // The aggregate refusing ConfirmOrder is not this case — that is a Rule
        // failure CommandConsumer acks, and the cancellation behind it reaches
        // the saga on its own event. This is the command never being consumed
        // at all, with the card authorised and the stock held.
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

            // Driven rather than waited out: the schedule is ten minutes, and a
            // test that slept for it is a test nobody runs.
            await Publish(harness, new ConfirmationExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.NotConfirmed))
                .ShouldBeTrue();

            // No CancelOrder: §3.2 gives Ordering no refund command, so there
            // is nothing to compensate with.
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
        // §3.2 gives Shipping OrderConfirmed too, so the aggregate's one
        // publish fans out to two consumers with no ordering between them
        // (§9.4), and the saga's own copy can be behind the despatch. Handled
        // rather than ignored, because ignoring loses MarkOrderShipped; safe,
        // because Shipping learns of the order only from OrderConfirmed, so a
        // despatch arriving proves the confirmation committed.
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

            // No OrderConfirmed published at all — the despatch arrives first.
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-EARLY"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRACK-EARLY"))
                .ShouldBeTrue();

            ConsumeFaults<ShipmentDispatched>(harness).ShouldAllBe(e => e == null);

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_confirmation_after_an_early_release_escalates_on_its_way_to_Confirmed()
    {
        // A StockReleased in AwaitingConfirmation records the cancellation; the
        // OrderConfirmed that follows would otherwise arm a three-day despatch
        // wait whose expiry raises not_despatched, and nothing would ever say
        // the order was cancelled. The transition still happens — the aggregate
        // committed the status, so the machine may not claim a state the order
        // has left — and the guard adds the row.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-3"));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
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
        // The same interleaving one state earlier: §3.2 gives Shipping
        // OrderConfirmed too, so a despatch can reach this saga before its own
        // acknowledgement, and that branch finalises as well.
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

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();
        }
    }
}

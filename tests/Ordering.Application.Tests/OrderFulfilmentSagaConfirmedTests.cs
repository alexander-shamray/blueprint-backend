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
/// §9.6's saga in <c>Confirmed</c>: what each event it can receive there
/// does.
/// </summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaConfirmedTests
{
    [Fact]
    public async Task Despatch_marks_the_order_shipped_and_finalises()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-2"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // The acknowledgement is what puts the saga in Confirmed.
            // AwaitingConfirmation binds ShipmentDispatched too, so this wait
            // is what keeps the test on the ordinary path.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> confirmed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await confirmed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-9"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRACK-9"))
                .ShouldBeTrue();

            // SetCompletedWhenFinalized deletes the instance, which is why
            // §9.6's diagram has no Shipped state: it would be one no saga is
            // ever observed in.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_timeout_escalates_rather_than_compensating()
    {
        // The wait with no automatic compensation: payment is taken and stock
        // is gone, so the timeout escalates to a human instead, and "no
        // timeout" is not the alternative (§9.6).
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-3"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // DespatchTimeout is armed on entering Confirmed, and the expiry
            // below is discarded unless the token is set, so waiting for
            // Confirmed is what makes this drive a transition rather than a
            // no-op.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> armed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await armed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            await Publish(harness, new DespatchExpired(orderId));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.NotDespatched))
                .ShouldBeTrue();

            // Not cancelled, and this is what separates an escalation from a
            // compensation: the customer has paid and the parcel may yet leave.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_cancellation_after_confirmation_escalates_rather_than_compensating()
    {
        // A cancellation this machine cannot compensate itself: undoing an
        // authorisation is a refund §3.2 gives Ordering no command for, and
        // whether Payments has voided off OrderCancelled is not knowable here
        // (§9.4). What the row escalates is shipping: a confirmed order may
        // still despatch. What stops a false not_despatched review three days
        // later is the Finalize, not the Unschedule beside it: ADR-021's
        // scheduler cannot cancel, so the timeout stays queued and is discarded
        // for want of an instance.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-4"));

            // A send is all a send establishes: the harness registers no
            // command consumer, so the acknowledgement has to be driven for the
            // state below to mean what its name says.
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            // The Confirmed code, not Compensating's: the row persists nothing
            // else, and the runbook selects its procedure on it after the saga
            // state is gone.
            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            // Not a compensation: the reservation is being picked, and telling
            // Inventory to drop it is not this machine's call to make.
            (await NotYetSent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_release_arriving_after_the_confirmation_is_absorbed_rather_than_faulted()
    {
        // Confirmed's OrderCancelled branch deliberately sends no release, and
        // Inventory releases on the event regardless (§3.2), so the release
        // arrives here too. Elsewhere the retry envelope rescues an early
        // arrival by finding the instance moved on; here the branch finalises,
        // so a second attempt finds no instance and is discarded in silence.
        // What this buys is a clean first delivery, and absorbing loses
        // nothing: the cancellation raises cancelled_after_confirmation on its
        // own branch.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "PSP-REF-129"));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            StockReleased released = SagaContracts.StockReleased(orderId);
            await Publish(harness, released);

            (await Consumed<StockReleased>(harness, m => m.MessageId == released.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Nothing sent and nothing moved: a despatch is still expected and
            // this event is not evidence against it.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_second_confirmation_in_Confirmed_is_absorbed_rather_than_faulted()
    {
        // The rollout case: a replica that entered Confirmed on the send
        // publishes OrderConfirmed moments after the instance is already there,
        // and the binding is durable and queue-scoped, so the first new replica
        // copies those into the saga queue for instances an old one advanced —
        // §15.5's canary runs both releases for the length of its ladder. The
        // same line covers §9.5's unrecorded redelivery.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-dup"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));
            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            // Two facts with two ids, not one object published twice: two
            // deliveries of one message share an id by design, which is §9.5's
            // inbox's problem and not this barrier's.
            OrderConfirmed duplicate = SagaContracts.OrderConfirmed(orderId, Customer);
            await Publish(harness, duplicate);

            (await Consumed<OrderConfirmed>(harness, m => m.MessageId == duplicate.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_after_an_early_release_marks_the_order_shipped_and_escalates()
    {
        // Confirmed's ShipmentDispatched finalises, so a cancellation in flight
        // then reaches a deleted instance and nothing records it.
        // MarkOrderShipped still goes and the aggregate refuses it: the flag is
        // set only by a StockReleased published off an OrderCancelled staged in
        // the cancelling transaction (ADR-029), so MarkOrderShippedHandler
        // answers order.not_shippable. The assertion is that the command is
        // sent — §5.4 gives the aggregate the transition — not that the order
        // records a despatch, which this harness could not see.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-1"));
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));
            await Publish(harness, SagaContracts.StockReleased(orderId));
            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRK-9"));

            (await Sent<MarkOrderShipped>(harness, m =>
                m.OrderId == orderId &&
                m.TrackingNumber == "TRK-9"))
                .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }
}

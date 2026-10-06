using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>§9.6's saga in <c>Confirmed</c>.</summary>
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

            // AwaitingConfirmation binds ShipmentDispatched too, so this wait keeps the test on the ordinary path.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> confirmed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await confirmed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.ShipmentDispatched(orderId, "TRACK-9"));

            (await Sent<MarkOrderShipped>(harness, m => m.OrderId == orderId && m.TrackingNumber == "TRACK-9"))
                .ShouldBeTrue();

            // SetCompletedWhenFinalized deletes the instance, which is why §9.6's diagram has no Shipped state.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_despatch_timeout_escalates_rather_than_compensating()
    {
        // Payment is taken and stock is gone, so the timeout escalates to a human rather than compensating (§9.6).
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

            // The expiry below is discarded unless DespatchTimeout was armed on entering Confirmed.
            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> armed =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await armed.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();

            await Publish(harness, new DespatchExpired(orderId));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.NotDespatched))
                .ShouldBeTrue();

            // Not cancelled: the customer has paid and the parcel may yet leave.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_cancellation_after_confirmation_escalates_rather_than_compensating()
    {
        // The Finalize, not the Unschedule, stops a false not_despatched: ADR-021's scheduler cannot cancel.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-4"));

            // The harness registers no command consumer, so the acknowledgement is driven below.
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            // The Confirmed code, not Compensating's, since the runbook selects its procedure on it.
            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            // Not a compensation: the reservation is being picked.
            (await NotYetSent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_release_arriving_after_the_confirmation_is_absorbed_rather_than_faulted()
    {
        // Inventory releases on the cancellation regardless (ADR-029), so the release arrives here too.
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

            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.Exists(orderId, x => x.Confirmed)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_second_confirmation_in_Confirmed_is_absorbed_rather_than_faulted()
    {
        // §15.5's canary runs both releases at once, so an OrderConfirmed can reach an instance already here.
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

            // Two facts with two ids, since one message delivered twice is §9.5's inbox's problem.
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
        // The command is sent and §5.4's aggregate decides; this harness cannot see whether it records a despatch.
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

            (await Sent<MarkOrderShipped>(harness, m => m.OrderId == orderId && m.TrackingNumber == "TRK-9"))
                .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }
}

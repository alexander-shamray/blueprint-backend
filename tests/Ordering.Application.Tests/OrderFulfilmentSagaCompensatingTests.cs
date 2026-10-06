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

/// <summary>§9.6's saga in <c>Compensating</c>.</summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaCompensatingTests
{
    [Fact]
    public async Task A_confirmation_arriving_after_compensation_began_escalates()
    {
        // §9.4 orders nothing between Ordering's own OrderConfirmed and OrderCancelled, so either may land first.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReserved(orderId));
            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "psp-ref-127"));
            (await Sent<ConfirmOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.OrderConfirmed(orderId, Customer));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_release_timeout_cancels_the_order_and_escalates_the_stock()
    {
        // The customer must not wait on Inventory, so the order is cancelled and the reservation escalated apart.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId && m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.StockNotReleased))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_payment_authorised_while_compensating_escalates_rather_than_being_ignored()
    {
        // Money arriving after a cancellation must not be swallowed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-late"));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            ConsumeFaults<PaymentAuthorised>(harness).ShouldAllBe(e => e == null);

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_release_does_not_finalise_while_Payments_still_owes_a_verdict()
    {
        // An unconditional Finalize on the release would leave the late authorisation correlating to nothing.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId && m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-release"));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_with_no_authorisation_outstanding_still_finalises_on_the_release()
    {
        // Cancelled in AwaitingStock, AuthorisePayment was never sent, so nothing is owed.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_verdict_that_never_arrives_bounds_the_wait_and_escalates_nothing()
    {
        // No FlagOrderForReview: Payments consumes OrderCancelled (§3.2), so this is the healthy path.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, SagaContracts.StockReleased(orderId));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // The wait armed when AuthorisePayment was sent, which the cancellation does not unschedule.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_payment_timeout_door_re_arms_its_own_wait_and_the_second_expiry_ends_it()
    {
        // Entered through PaymentTimeout.Received, the wait it would rely on has already fired, so it re-arms.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            // A timeout is not a verdict, so the obligation stays outstanding.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId && m.Reason == CancelReasons.PaymentTimeout))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // The second expiry, the one the branch armed on its way in.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_decline_after_the_release_settles_the_join_without_escalating()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, SagaContracts.StockReleased(orderId));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // Alive when the decline arrives, since a decline reaching no instance also leaves no review row.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_release_timeout_holds_the_instance_while_a_verdict_is_outstanding()
    {
        // Giving up on the release settles the stock half as StockReleased does, so one order can carry both rows.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.StockNotReleased))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-timeout"));

            (await Sent<FlagOrderForReview>(
                harness,
                m => m.OrderId == orderId && m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_decline_arriving_after_a_cancellation_is_absorbed_rather_than_faulted()
    {
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));

            (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            PaymentDeclined declined = SagaContracts.PaymentDeclined(orderId, "do_not_honour");
            await Publish(harness, declined);

            (await Consumed<PaymentDeclined>(harness, m => m.MessageId == declined.MessageId))
                .ShouldBeTrue();

            // The assertion that tells a missing branch from a handled one.
            ConsumeFaults<PaymentDeclined>(harness).ShouldAllBe(e => e == null);

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_while_compensating_changes_nothing()
    {
        // Compensating already ends in a cancellation, so the request is an explicit Ignore.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

            OrderCancelled cancelled =
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest);
            await Publish(harness, cancelled);

            (await Consumed<OrderCancelled>(harness, m => m.MessageId == cancelled.MessageId))
                .ShouldBeTrue();
            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // The reason recorded on entry, which a cancellation arriving mid-flight must not rewrite.
            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId && m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();

            harness.Sent
                .Select<ReleaseStock>(Spent())
                .Count(m => m.Context.Message.OrderId == orderId)
                .ShouldBe(1);
        }
    }

    [Fact]
    public async Task A_release_that_overtakes_the_cancellation_is_absorbed_and_the_compensation_still_ends()
    {
        // Inventory consumes OrderCancelled directly (ADR-029), so its StockReleased can beat the saga's own copy.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            StockReleased overtaking = SagaContracts.StockReleased(orderId);
            await Publish(harness, overtaking);

            (await Consumed<StockReleased>(harness, m => m.MessageId == overtaking.MessageId))
                .ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // ADR-024's first guarantee: Inventory answers the command although it already released.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m => m.OrderId == orderId && m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }
}

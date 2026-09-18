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

/// <summary>
/// §9.6's saga in <c>Compensating</c>: what each event it can receive
/// there does.
/// </summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaCompensatingTests
{
    [Fact]
    public async Task A_confirmation_arriving_after_compensation_began_escalates()
    {
        // OrderConfirmed and OrderCancelled are both Ordering's outbox rows and
        // §9.4 orders nothing between them, so the cancellation can reach the
        // saga first; the confirmation landing afterwards is the only evidence
        // that Shipping has been told and the ReleaseStock in flight is for
        // stock somebody may be picking.
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

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.CancelledAfterConfirmation))
                .ShouldBeTrue();

            ConsumeFaults<OrderConfirmed>(harness).ShouldAllBe(e => e == null);

            // Still waiting on Inventory — the exits own the cancellation, so
            // this branch adds the row and nothing else.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_release_timeout_cancels_the_order_and_escalates_the_stock()
    {
        // Two sends from one transition, answering different people: the
        // customer must not wait on Inventory, so the order is cancelled
        // regardless; the stranded reservation is Inventory's to resolve, so it
        // is escalated separately.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReserved(orderId));
            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));
            await Publish(harness, new StockReleaseExpired(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
                .ShouldBeTrue();

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.StockNotReleased))
                .ShouldBeTrue();
        }
    }

    [Fact]
    public async Task A_payment_authorised_while_compensating_escalates_rather_than_being_ignored()
    {
        // Money arriving after a cancellation must not be swallowed. It is
        // Confirmed's case by the other door — the same symptom under a
        // different code — and the difference is shipping: this state cannot
        // despatch and Confirmed may. Not the refund: Payments voids off
        // OrderCancelled (§3.2) on both, and whether it has is not knowable
        // from either state, since §9.4 orders nothing between independent
        // consumers.
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

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            // And it did not reach the error queue: the point is that this is
            // handled, not merely that it is loud.
            ConsumeFaults<PaymentAuthorised>(harness).ShouldAllBe(e => e == null);

            // The saga is still running: this row is raised mid-wait and can
            // sit beside a live instance until StockReleased or the
            // ReleaseTimeout, which is what the runbook has to say.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_release_does_not_finalise_while_Payments_still_owes_a_verdict()
    {
        // The ordinary interleaving: Inventory answers promptly and the PSP is
        // slow. An unconditional Finalize on the release deletes the instance,
        // and the authorisation still in flight then correlates to nothing —
        // consumed cleanly, no review row, nothing on §13.6's pager. §9.4
        // orders nothing between the two services, so neither order may be
        // assumed.
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

            // The order is cancelled on this transition — that command does not
            // wait on Payments — but the instance is held, because the
            // authorisation can still land.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // Everything below is unreachable unless the instance survives the
            // release.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-release"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            // Both halves settled, so the saga ends: holding the instance is
            // the mechanism, and a saga that never finalised would fire §13.6's
            // unfinalised-saga alert on every cancelled order.
            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_with_no_authorisation_outstanding_still_finalises_on_the_release()
    {
        // Cancelling in AwaitingStock means AuthorisePayment was never sent, so
        // nothing is owed and the release ends the saga: the conditional
        // Finalize is a condition, not a delay.
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
        // Holding the instance for a verdict needs something that ends the hold
        // when none comes, or a slow PSP parks the saga until §13.6's
        // unfinalised-saga alert pages. No FlagOrderForReview, by decision:
        // §3.2 has Payments consuming OrderCancelled, so an authorisation
        // abandoned on a cancelled order is the healthy path, and a row here
        // would escalate it once per cancelled order.
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

            // The wait armed when AuthorisePayment was sent, deliberately not
            // unscheduled by the cancellation branch.
            await Publish(harness, new PaymentAuthorisationExpired(orderId));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task The_payment_timeout_door_re_arms_its_own_wait_and_the_second_expiry_ends_it()
    {
        // Cancelling in AwaitingPayment leaves the original wait armed, so that
        // door gets its bound for free; reaching Compensating through
        // PaymentTimeout.Received does not, because the wait it would rely on
        // is the one that just fired. Without the re-arm
        // PaymentVerdictOutstanding stays set with nothing left to clear it,
        // and the instance is held until §13.6's unfinalised-saga alert pages.
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

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentTimeout))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            // The stock half is settled and the saga still will not end,
            // because this door arrived owing a verdict.
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
        // A decline is an answer: no money moved, so it raises nothing, but it
        // discharges the obligation the cancellation carried in and the saga
        // ends on it rather than waiting out the payment window.
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

            // The instance has to be observed alive when the decline arrives: a
            // decline reaching no instance is discarded, and "no instance, no
            // review row" reads identically from both sides.
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentDeclined(orderId, "do_not_honour"));

            (await saga.NotExists(orderId)).ShouldBeNull();

            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_release_timeout_holds_the_instance_while_a_verdict_is_outstanding()
    {
        // Giving up on the release settles the stock half exactly as
        // StockReleased does, so it asks the same question about the other.
        // "Settled" means come to rest: this branch escalates
        // stock_not_released and the instance stays for the verdict, so one
        // order can carry both rows.
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

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.StockNotReleased))
                .ShouldBeTrue();

            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.PaymentAuthorised(orderId, "auth-after-timeout"));

            (await Sent<FlagOrderForReview>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == ReviewReasons.PaymentAuthorisedDuringCompensation))
                .ShouldBeTrue();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task A_decline_arriving_after_a_cancellation_is_absorbed_rather_than_faulted()
    {
        // Cancelling from AwaitingPayment arrives in Compensating with
        // AuthorisePayment already sent and unanswered, so the PSP's verdict
        // can still be either. A decline means no money moved, which is where
        // compensation was heading, so it is an explicit Ignore rather than an
        // error-queue entry.
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

            // Not faulted, and this is the assertion that tells a missing
            // branch from an explicit Ignore; nothing else here would.
            ConsumeFaults<PaymentDeclined>(harness).ShouldAllBe(e => e == null);

            // Nothing escalated: there is no money for a human to chase.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

            // The compensation is untouched and still waiting on Inventory.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();
        }
    }

    [Fact]
    public async Task A_cancellation_while_compensating_changes_nothing()
    {
        // Compensating already ends in a cancellation, so the request adds
        // nothing to do; it is Ignored explicitly rather than left to
        // OnUnhandledEvent because a reader cannot tell a decision from an
        // omission.
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

            // Ignored means the state is untouched, not merely that nothing
            // was sent — the compensation still has to be waiting on Inventory
            // when the release arrives.
            ISagaStateMachineTestHarness<OrderFulfilmentSaga, OrderFulfilmentState> saga =
                harness.GetSagaStateMachineHarness<OrderFulfilmentSaga, OrderFulfilmentState>();

            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            await Publish(harness, SagaContracts.StockReleased(orderId));

            // payment_declined, not customer_request: the reason recorded on
            // entry is the one that caused the compensation, and a cancellation
            // arriving mid-flight must not rewrite it.
            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.PaymentDeclined))
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
        // §3.2 has Inventory consuming OrderCancelled directly, so one
        // publication starts two races to this endpoint: the saga's own copy of
        // the event and the StockReleased Inventory derives from it. Absorbing
        // the early release is safe only because ADR-024 has Inventory answer
        // the saga's own ReleaseStock whatever it already did on the event;
        // otherwise the instance waits out ReleaseTimeout and files a
        // stock_not_released review for stock that came back. So the second
        // publish drives the contract, not merely the branch.
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

            // Without this branch the arrival faults and spends §9.8's five
            // retries hoping the cancellation lands first.
            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Absorbed rather than acted on — the wait is untouched.
            (await saga.Exists(orderId, x => x.AwaitingStock)).ShouldNotBeNull();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orderId, Customer, CancelReasons.CustomerRequest));

            (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
            (await saga.Exists(orderId, x => x.Compensating)).ShouldNotBeNull();

            // ADR-024's first guarantee, driven: Inventory answers the command
            // although it released on the event a moment ago, because
            // StockReleased reports the postcondition rather than a state
            // change.
            await Publish(harness, SagaContracts.StockReleased(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.CustomerRequest))
                .ShouldBeTrue();

            // Nothing escalated: the point of the ADR is that this ordinary
            // interleaving does not reach a human.
            (await NotYetSent<FlagOrderForReview>(harness, m => m.OrderId == orderId))
                .ShouldBeFalse();

            (await saga.NotExists(orderId)).ShouldBeNull();
        }
    }
}

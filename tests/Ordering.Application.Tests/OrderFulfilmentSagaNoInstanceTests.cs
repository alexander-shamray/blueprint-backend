using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>
/// §9.6's saga given an event whose order has no instance: which are
/// discarded and which fault.
/// </summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaNoInstanceTests
{
    [Fact]
    public async Task A_cancellation_the_saga_itself_caused_finds_no_instance_and_is_discarded()
    {
        // The routine echo: this order's CancelOrder went out of a branch that
        // finalises, so the OrderCancelled the aggregate then publishes reaches
        // a deleted instance. It must be discarded rather than faulted, or
        // every cancelled order pages someone (§13.6). StockReservationFailed
        // is driven because its branch finalises unconditionally, and
        // CancelOrigins.Workflow is what makes the publish the echo rather than
        // its type alone.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orderId = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
            await Publish(harness, SagaContracts.StockReservationFailed(orderId));

            (await Sent<CancelOrder>(harness, m =>
                m.OrderId == orderId &&
                m.Reason == CancelReasons.OutOfStock))
                .ShouldBeTrue();

            OrderCancelled echo = SagaContracts.OrderCancelled(
                orderId,
                Customer,
                CancelReasons.OutOfStock,
                CancelOrigins.Workflow);
            await Publish(harness, echo);

            (await Consumed<OrderCancelled>(harness, m => m.MessageId == echo.MessageId)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);
        }
    }

    [Fact]
    public async Task An_event_for_an_order_with_no_instance_is_discarded_in_silence()
    {
        // MassTransit's policy for a non-initial event correlating to no
        // instance is not the unhandled-event path: the default consumes
        // cleanly and drops it, with nothing on §13.6's pager. That default is
        // what makes OrderCancelled's explicit Discard cheap, and pinning it
        // means an upgrade that changes it is reported here. StockReleased is
        // the subject because ADR-024 has Inventory publish it for every
        // release including a no-op, so reaching a finalised instance is its
        // ordinary case; PaymentAuthorised overrides the default and would
        // measure the override instead.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.StockReleased(orphan));

            (await Consumed<StockReleased>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            // Nothing sent: no transition ran, so the machine never saw the
            // event.
            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orphan)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task An_authorisation_for_an_order_with_no_instance_faults_rather_than_vanishing()
    {
        // PaymentVerdictOutstanding holds the instance while a verdict can
        // still arrive; this covers the arrival after the machine stopped
        // waiting. The override is safe on provenance, not timing: Payments
        // produces PaymentAuthorised, so it can never be Ordering's own echo,
        // where OrderCancelled and StockReleased can be and keep the silent
        // default. The fault is what puts the arrival on §13.6's pager.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.PaymentAuthorised(orphan, "auth-orphan"));

            (await Consumed<PaymentAuthorised>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<PaymentAuthorised>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_this_workflow_did_not_cause_faults_when_no_instance_exists()
    {
        // A customer's cancellation that overtakes its own OrderPlaced
        // correlates to nothing; consumed cleanly, the placement that follows
        // starts a live saga for a cancelled order. Faulting spends §9.8's
        // retry envelope, about seventy seconds for the OrderPlaced to land,
        // and only then is it an error-queue entry.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.CustomerRequest,
                    CancelOrigins.User));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_carrying_this_workflows_own_reason_still_faults_if_it_did_not_cause_it()
    {
        // Reason is not the discriminator: §11.4's endpoint parses all five
        // CancelReasons codes, so a caller may send out_of_stock, and a
        // Reason-based branch would discard this in silence. Origin says User
        // here whatever the reason says.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.OutOfStock,
                    CancelOrigins.User));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_carrying_an_unknown_origin_faults_rather_than_being_discarded()
    {
        // What tells an allow-list from a deny-list: null, workflow and user
        // are answered identically by a branch reading "fault only when Origin
        // is user", which would then discard every spelling nobody thought of —
        // a later vocabulary member, a producer on another version, a truncated
        // field.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(
                    orphan,
                    Customer,
                    CancelReasons.CustomerRequest,
                    origin: "operations_console"));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldContain(e => e != null);
        }
    }

    [Fact]
    public async Task A_cancellation_published_before_the_origin_field_existed_is_discarded()
    {
        // A rolling deploy has instances publishing before they populate
        // Origin, and faulting on absent would file an error-queue entry for
        // every cancellation for the length of the deploy. The tolerance is
        // permanent for this contract version: an old payload can arrive from
        // the error queue or a replay long after, and a required Origin would
        // fail deserialisation before this branch ran. This test goes when V1
        // does.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(
                harness,
                SagaContracts.OrderCancelled(orphan, Customer, CancelReasons.CustomerRequest, origin: null));

            (await Consumed<OrderCancelled>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<OrderCancelled>(harness).ShouldAllBe(e => e == null);
        }
    }
}

using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using static Ordering.Application.Tests.OrderFulfilmentSagaHarness;

namespace Ordering.Application.Tests;

/// <summary>§9.6's saga given an event whose order has no instance: which are discarded and which fault.</summary>
[Collection(nameof(OrderFulfilmentSagaCollection))]
public class OrderFulfilmentSagaNoInstanceTests
{
    [Fact]
    public async Task A_cancellation_the_saga_itself_caused_finds_no_instance_and_is_discarded()
    {
        // The saga's own echo reaches a finalised instance, and faulting it would page on every cancellation.
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
        // Pins MassTransit's default for a missing instance: consumed cleanly and dropped.
        (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
        await using (provider)
        {
            var orphan = Guid.CreateVersion7();

            await Publish(harness, SagaContracts.StockReleased(orphan));

            (await Consumed<StockReleased>(harness, m => m.OrderId == orphan)).ShouldBeTrue();

            ConsumeFaults<StockReleased>(harness).ShouldAllBe(e => e == null);

            (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orphan)).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task An_authorisation_for_an_order_with_no_instance_faults_rather_than_vanishing()
    {
        // Safe to fault on provenance: Payments produces PaymentAuthorised, so it is never Ordering's own echo.
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
        // Faulted into §9.8's retries, so a cancellation that overtook its OrderPlaced is not dropped.
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
        // Reason is not the discriminator, since §11.4's endpoint accepts every CancelReasons code.
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
        // What tells the allow-list from a deny-list that faults only on user.
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
        // An absent Origin is V1's permanent tolerance (§9.2).
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

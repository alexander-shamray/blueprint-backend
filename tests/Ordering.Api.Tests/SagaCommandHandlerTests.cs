using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.ConfirmStock;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Domain.Orders;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>A system cancellation, and saga-only refusals whose <c>ErrorType</c> decides retry or ack (§9.8).</summary>
/// <remarks>Dispatched, not sent, since the endpoint turns the error into a retry that hides it (§9.8).</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class SagaCommandHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Customer = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Confirming_an_order_still_awaiting_stock_is_retryable()
    {
        // The reservation and the authorisation arrive on two endpoints with nothing sequencing them.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        Result result = await DispatchAsync(
            new ConfirmOrderCommand(orderId, PaymentReference.Of("psp-race-1")));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(OrderErrors.StockNotConfirmed);
        result.Error.Type.ShouldBe(
            ErrorType.Unavailable,
            "CommandConsumer retries only this type — a Rule error would ack a paid order's " +
            "confirmation permanently (§9.8)");

        (await StatusAsync(orderId)).ShouldBe("AwaitingStock", "a refusal mutates nothing");
    }

    [Fact]
    public async Task Confirming_an_order_that_has_moved_on_is_a_rejection()
    {
        // The other side of the branch: no retry changes the answer, so retrying ends in the error queue.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        await DispatchAsync(new ConfirmStockCommand(orderId));
        await DispatchAsync(new ConfirmOrderCommand(orderId, PaymentReference.Of("psp-first")));

        Result result = await DispatchAsync(
            new ConfirmOrderCommand(orderId, PaymentReference.Of("psp-second")));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(OrderErrors.NotAwaitingPayment);
        result.Error.Type.ShouldBe(ErrorType.Rule, "no retry makes a confirmed order confirmable again");
    }

    [Fact]
    public async Task Marking_an_unconfirmed_order_shipped_is_retryable()
    {
        // A despatch arriving first means the confirmation exists and has not landed, which time fixes.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        Result result = await DispatchAsync(
            new MarkOrderShippedCommand(orderId, TrackingNumber.Of("TRACK-EARLY")));

        result.Error.ShouldBe(OrderErrors.NotConfirmed);
        result.Error.Type.ShouldBe(ErrorType.Unavailable);
    }

    [Fact]
    public async Task Marking_a_cancelled_order_shipped_is_a_rejection()
    {
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        await DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.System));

        Result result = await DispatchAsync(
            new MarkOrderShippedCommand(orderId, TrackingNumber.Of("TRACK-LATE")));

        result.Error.ShouldBe(OrderErrors.NotShippable);
        result.Error.Type.ShouldBe(
            ErrorType.Rule,
            "a cancelled order refuses identically on every attempt — §9.8 keeps that out of the " +
            "error queue, where depth greater than zero pages a human");
    }

    [Fact]
    public async Task A_reservation_for_an_order_that_moved_on_is_rejected_and_changes_nothing()
    {
        // §9.6's stock-timeout residual from the aggregate's side: a reservation arriving after the cancellation.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        await DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.StockTimeout, CommandOrigin.System));

        Result result = await DispatchAsync(new ConfirmStockCommand(orderId));

        result.Error.ShouldBe(OrderErrors.NotAwaitingStock);
        result.Error.Type.ShouldBe(
            ErrorType.Rule,
            "no attempt from here releases the reservation, so retrying is a queue entry and not a fix");

        (await StatusAsync(orderId)).ShouldBe("Cancelled", "the refusal must not move a cancelled order");
    }

    [Fact]
    public async Task A_command_for_an_order_that_does_not_exist_is_not_found()
    {
        // All three handlers share this first branch, the one a misrouted correlation id reaches.
        var missing = Guid.CreateVersion7();

        (await DispatchAsync(new ConfirmStockCommand(missing))).Error.ShouldBe(OrderErrors.NotFound);
        (await DispatchAsync(new ConfirmOrderCommand(missing, PaymentReference.Of("psp-x"))))
            .Error
            .ShouldBe(OrderErrors.NotFound);
        (await DispatchAsync(new MarkOrderShippedCommand(missing, TrackingNumber.Of("TRACK-X"))))
            .Error
            .ShouldBe(OrderErrors.NotFound);
    }

    [Fact]
    public async Task A_system_initiated_cancellation_publishes_the_workflow_origin()
    {
        // The System case alone: a User-origin command has no principal in a bare scope, so §11.4's guard
        // refuses it first. Read off the outbox row, the payload a consumer sees.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        Result cancelled = await DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.System));

        cancelled.IsSuccess.ShouldBeTrue();

        // The Broker row; §6.6's projection stages the domain event on the Local lane beside it.
        OutboxMessage row = (await fixture.OutboxAsync())
            .Where(r => r.Lane == OutboxLane.Broker)
            .ShouldHaveSingleItem();

        row.Payload.ShouldContain(
            $"\"Origin\":\"{CancelOrigins.Workflow}\"",
            Case.Sensitive,
            "the saga's own CancelOrder must echo back as this workflow's doing");
    }

    private async Task<Result> DispatchAsync(ICommand<Result> command)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, TestContext.Current.CancellationToken);
    }

    private async Task<string> StatusAsync(Guid orderId) =>
        await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            orderId);
}

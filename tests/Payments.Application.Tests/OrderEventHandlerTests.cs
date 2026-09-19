using Common.Application;
using Common.Contracts.Ordering.V1;
using Payments.Application.Orders.RecordOrderCancelled;
using Payments.Application.Orders.RecordOrderPlaced;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class OrderEventHandlerTests
{
    private static readonly DateTimeOffset Occurred = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct)
        {
            Sent.Add(command);
            return Task.FromResult((TResult)(object)Result.Success());
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task OrderPlaced_records_the_payer_the_total_and_the_currency_at_the_events_instant()
    {
        RecordingDispatcher dispatcher = new();
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await new OrderPlacedHandler(dispatcher).HandleAsync(
            new OrderPlaced
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = Occurred,
                OrderId = order,
                CustomerId = customer,
                TotalAmount = 42.10m,
                Currency = "EUR",
                Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 42.10m)]
            },
            TestContext.Current.CancellationToken);

        dispatcher.Sent.ShouldHaveSingleItem()
            .ShouldBe(new RecordOrderPlacedCommand(order, customer, 42.10m, "EUR", Occurred));
    }

    [Fact]
    public async Task OrderCancelled_records_the_cancellation_at_the_events_instant()
    {
        RecordingDispatcher dispatcher = new();
        Guid order = Guid.CreateVersion7();

        await new OrderCancelledHandler(dispatcher).HandleAsync(
            new OrderCancelled
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = order,
                OccurredAt = Occurred,
                OrderId = order,
                CustomerId = Guid.CreateVersion7(),
                Reason = CancelReasons.CustomerRequest
            },
            TestContext.Current.CancellationToken);

        dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordOrderCancelledCommand(order, Occurred));
    }
}

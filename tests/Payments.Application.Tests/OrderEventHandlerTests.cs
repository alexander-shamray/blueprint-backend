using Common.Application;
using Common.Contracts.Ordering.V1;
using Payments.Application.Orders.EndErasedOrder;
using Payments.Application.Orders.RecordOrderCancelled;
using Payments.Application.Orders.RecordOrderPlaced;
using Payments.Application.Privacy;
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

        await new OrderPlacedHandler(dispatcher, new Erased(false)).HandleAsync(
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

        dispatcher.Sent
            .ShouldHaveSingleItem()
            .ShouldBe(new RecordOrderPlacedCommand(order, customer, 42.10m, "EUR", Occurred, []));
    }

    [Fact]
    public async Task The_requests_the_order_was_placed_after_are_stored_and_handed_to_the_check()
    {
        RecordingDispatcher dispatcher = new();
        Erased erased = new(false);
        Guid request = Guid.CreateVersion7();

        await new OrderPlacedHandler(dispatcher, erased)
            .HandleAsync(Placed(Guid.CreateVersion7(), [request]), TestContext.Current.CancellationToken);

        dispatcher.Sent.OfType<RecordOrderPlacedCommand>().ShouldHaveSingleItem().ErasedUnder.ShouldBe([request]);
        erased.PlacedAfter.ShouldHaveSingleItem().ShouldBe([request]);
    }

    [Fact]
    public async Task A_customer_the_audit_rows_name_has_the_one_order_ended_after_it_is_recorded()
    {
        RecordingDispatcher dispatcher = new();
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        Erased erased = new(true)
        {
            OnAsked = () => dispatcher.Sent.ShouldHaveSingleItem().ShouldBeOfType<RecordOrderPlacedCommand>()
        };

        await new OrderPlacedHandler(dispatcher, erased)
            .HandleAsync(Placed(order, [], customer), TestContext.Current.CancellationToken);

        dispatcher.Sent.Count.ShouldBe(2, "the record has to commit before the check");
        dispatcher.Sent[1].ShouldBe(new EndErasedOrderCommand(order, customer));
        erased.Asked.ShouldBe([customer]);
    }

    [Fact]
    public async Task A_customer_nobody_erased_is_recorded_and_nothing_more()
    {
        RecordingDispatcher dispatcher = new();

        await new OrderPlacedHandler(dispatcher, new Erased(false))
            .HandleAsync(Placed(Guid.CreateVersion7(), []), TestContext.Current.CancellationToken);

        dispatcher.Sent.ShouldHaveSingleItem().ShouldBeOfType<RecordOrderPlacedCommand>();
    }

    private static OrderPlaced Placed(Guid order, IReadOnlyList<Guid> erasedUnder, Guid? customer = null) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = Occurred,
        OrderId = order,
        CustomerId = customer ?? Guid.CreateVersion7(),
        TotalAmount = 42.10m,
        Currency = "EUR",
        Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 42.10m)],
        ErasedUnder = erasedUnder
    };

    private sealed class Erased(bool erased) : IErasedSubjects
    {
        public List<Guid> Asked { get; } = [];

        public List<IReadOnlyCollection<Guid>> PlacedAfter { get; } = [];

        public Action? OnAsked { get; set; }

        public Task<bool> IsErasedAsync(Guid subjectId, IReadOnlyCollection<Guid> placedAfter, CancellationToken ct)
        {
            Asked.Add(subjectId);
            PlacedAfter.Add(placedAfter);
            OnAsked?.Invoke();

            return Task.FromResult(erased);
        }
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

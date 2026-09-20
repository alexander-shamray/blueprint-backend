using Payments.Application.Orders;
using Payments.Application.Orders.RecordOrderCancelled;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class RecordOrderCancelledHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeOrderStore _orders = new();
    private readonly FakeIntents _intents;
    private readonly FakeRefunds _refunds = new();
    private readonly FakeProvider _provider = new();

    public RecordOrderCancelledHandlerTests()
    {
        _intents = new FakeIntents(_orders);
    }

    private RecordOrderCancelledHandler Handler() =>
        new(_orders, _intents, _refunds, _provider, new FixedClock(Now));

    [Fact]
    public async Task An_authorised_intent_is_voided_under_the_void_key_and_refunded()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));

        await Handler().HandleAsync(
            new RecordOrderCancelledCommand(order.Value, Now), TestContext.Current.CancellationToken);

        _orders.Cancelled.ShouldBe([(order, Now)]);
        _provider.Voids.ShouldHaveSingleItem().ShouldBe(new VoidRequest(order, "psp_1"));

        Refund refund = _refunds.Added.ShouldHaveSingleItem();
        refund.Reference.ShouldBe("psp_1");
        refund.Amount.ShouldBe(42.10m);
        refund.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task An_intent_already_refunded_is_not_voided_twice()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));
        _refunds.Existing.Add(order);

        await Handler().HandleAsync(
            new RecordOrderCancelledCommand(order.Value, Now), TestContext.Current.CancellationToken);

        _orders.Cancelled.ShouldHaveSingleItem();
        _provider.Voids.ShouldBeEmpty();
        _refunds.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_declined_intent_is_stamped_and_nothing_is_voided()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Decline(order, 42.10m, "EUR", "card_declined", Now));

        await Handler().HandleAsync(
            new RecordOrderCancelledCommand(order.Value, Now), TestContext.Current.CancellationToken);

        _orders.Cancelled.ShouldHaveSingleItem();
        _provider.Voids.ShouldBeEmpty("ADR-047: no money was taken, so nothing moved back");
        _refunds.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_intent_is_stamped_and_nothing_is_voided()
    {
        OrderId order = OrderId.New();

        await Handler().HandleAsync(
            new RecordOrderCancelledCommand(order.Value, Now), TestContext.Current.CancellationToken);

        _orders.Cancelled.ShouldHaveSingleItem("the stamp creates the tombstone record when none exists");
        _provider.Voids.ShouldBeEmpty();
        _refunds.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_stamp_is_taken_before_the_intent_is_read()
    {
        OrderId order = OrderId.New();

        await Handler().HandleAsync(
            new RecordOrderCancelledCommand(order.Value, Now), TestContext.Current.CancellationToken);

        _intents.GetCallsBeforeStamp.ShouldBe(
            0, "the stamp's lock is what serialises this against AuthorisePayment");
    }

    [Fact]
    public async Task A_provider_that_cannot_void_throws_and_records_no_refund()
    {
        OrderId order = OrderId.New();
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));
        _provider.VoidFault = new PaymentProviderUnavailableException("down");

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Handler().HandleAsync(
                new RecordOrderCancelledCommand(order.Value, Now), TestContext.Current.CancellationToken));
        _refunds.Added.ShouldBeEmpty("the unit rolls back and §9.8 retries it whole");
    }

    private sealed class FakeOrderStore : IPaymentOrderStore
    {
        public List<(OrderId Order, DateTimeOffset CancelledAt)> Cancelled { get; } = [];

        public Task RecordPlacedAsync(
            OrderId id,
            Guid customerId,
            decimal total,
            string currency,
            DateTimeOffset placedAt,
            CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the cancellation path.");

        public Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct)
        {
            Cancelled.Add((id, cancelledAt));
            return Task.CompletedTask;
        }

        public Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct) =>
            throw new NotSupportedException("The stamp is this path's lock; it reads no record.");
    }

    // Holds a reference to the order store so a call made before the stamp is
    // taken can be told apart from one made after.
    private sealed class FakeIntents(FakeOrderStore orders) : IPaymentIntentRepository
    {
        private readonly Dictionary<OrderId, PaymentIntent> _byOrder = [];

        public int GetCallsBeforeStamp { get; private set; }

        public void Seed(PaymentIntent intent) => _byOrder[intent.Id] = intent;

        public Task<PaymentIntent?> GetAsync(OrderId id, CancellationToken ct)
        {
            if (orders.Cancelled.Count == 0)
                GetCallsBeforeStamp++;

            return Task.FromResult(_byOrder.GetValueOrDefault(id));
        }

        public void Add(PaymentIntent intent) =>
            throw new NotSupportedException("The cancellation path creates no intent.");
    }

    private sealed class FakeRefunds : IRefundRepository
    {
        public HashSet<OrderId> Existing { get; } = [];

        public List<Refund> Added { get; } = [];

        public Task<bool> ExistsAsync(OrderId id, CancellationToken ct) => Task.FromResult(Existing.Contains(id));

        public void Add(Refund refund)
        {
            Added.Add(refund);
            Existing.Add(refund.Id);
        }
    }

    private sealed class FakeProvider : IPaymentProvider
    {
        public List<VoidRequest> Voids { get; } = [];

        public Exception? VoidFault { get; set; }

        public Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the cancellation path.");

        public Task VoidAsync(VoidRequest request, CancellationToken ct)
        {
            if (VoidFault is not null)
                return Task.FromException(VoidFault);

            Voids.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

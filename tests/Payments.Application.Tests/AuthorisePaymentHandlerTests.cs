using Common.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Application.Intents;
using Payments.Application.Intents.AuthorisePayment;
using Payments.Application.Orders;
using Payments.Application.Provider;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class AuthorisePaymentHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Payer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly FakeOrderStore _orders = new();
    private readonly FakeIntents _intents;
    private readonly FakeProvider _provider = new();

    public AuthorisePaymentHandlerTests()
    {
        _intents = new FakeIntents(_orders);
    }

    private AuthorisePaymentHandler Handler() =>
        new(_orders, _intents, _provider, new FixedClock(Now), NullLogger<AuthorisePaymentHandler>.Instance);

    private static PaymentOrderRecord Placed(OrderId id, decimal total = 42.10m, DateTimeOffset? cancelledAt = null) =>
        new(id, Payer, total, "EUR", Now.AddMinutes(-1), cancelledAt);

    [Fact]
    public async Task A_placed_order_is_authorised_at_the_provider_as_its_recorded_payer()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);
        _provider.Answer = new AuthorisationResult.Authorised("psp_1");

        Result result = await Handler().HandleAsync(
            new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _provider.Requests.ShouldHaveSingleItem().ShouldBe(new AuthorisationRequest(order, Payer, 42.10m, "EUR"));
        _intents.Added.ShouldHaveSingleItem().Status.ShouldBe(PaymentIntentStatus.Authorised);
    }

    [Fact]
    public async Task A_provider_decline_is_recorded_as_a_declined_intent()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);
        _provider.Answer = new AuthorisationResult.Declined("card_declined");

        await Handler().HandleAsync(
            new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), TestContext.Current.CancellationToken);

        PaymentIntent intent = _intents.Added.ShouldHaveSingleItem();
        intent.Status.ShouldBe(PaymentIntentStatus.Declined);
        intent.DeclineReason.ShouldBe("card_declined");
    }

    [Fact]
    public async Task No_record_is_a_wait_and_calls_nobody()
    {
        _orders.Record = null;

        await Should.ThrowAsync<PaymentOrderNotYetKnownException>(() =>
            Handler().HandleAsync(
                new AuthorisePaymentCommand(Guid.CreateVersion7(), 1m, "EUR"), TestContext.Current.CancellationToken));
        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_order_is_declined_order_cancelled_without_calling_the_provider()
    {
        OrderId order = OrderId.New();
        _orders.Record = new PaymentOrderRecord(order, null, null, null, null, Now.AddMinutes(-2));

        await Handler().HandleAsync(
            new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), TestContext.Current.CancellationToken);

        _provider.Requests.ShouldBeEmpty("ADR-047: a cancelled order is never charged");
        PaymentIntent intent = _intents.Added.ShouldHaveSingleItem();
        intent.Status.ShouldBe(PaymentIntentStatus.Declined);
        intent.DeclineReason.ShouldBe(DeclineReasons.OrderCancelled);
    }

    [Fact]
    public async Task A_placed_then_cancelled_order_is_declined_order_cancelled_too()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order, cancelledAt: Now.AddSeconds(-5));

        await Handler().HandleAsync(
            new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), TestContext.Current.CancellationToken);

        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldHaveSingleItem().DeclineReason.ShouldBe(DeclineReasons.OrderCancelled);
    }

    [Fact]
    public async Task A_placed_then_cancelled_order_with_other_money_is_a_mismatch_not_a_decline()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order, cancelledAt: Now.AddSeconds(-5));

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Handler().HandleAsync(
                new AuthorisePaymentCommand(order.Value, 99.99m, "USD"), TestContext.Current.CancellationToken));
        _intents.Added.ShouldBeEmpty(
            "no customer-facing decline is published for a command that disagrees with the order");
    }

    [Theory]
    [InlineData(42.11, "EUR")]
    [InlineData(42.10, "USD")]
    public async Task A_mismatch_is_a_fault_and_charges_nothing(decimal amount, string currency)
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Handler().HandleAsync(
                new AuthorisePaymentCommand(order.Value, amount, currency), TestContext.Current.CancellationToken));
        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_resend_with_other_money_is_a_mismatch_even_when_an_intent_exists()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order);
        _intents.Seed(PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1)));

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Handler().HandleAsync(
                new AuthorisePaymentCommand(order.Value, 99.99m, "EUR"), TestContext.Current.CancellationToken));
        _provider.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_existing_intent_is_acknowledged_without_a_second_verdict_even_after_a_cancellation()
    {
        OrderId order = OrderId.New();
        _orders.Record = Placed(order, cancelledAt: Now);
        PaymentIntent existing = PaymentIntent.Authorise(order, 42.10m, "EUR", "psp_1", Now.AddMinutes(-1));
        existing.ClearDomainEvents();
        _intents.Seed(existing);

        Result result = await Handler().HandleAsync(
            new AuthorisePaymentCommand(order.Value, 42.10m, "EUR"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _provider.Requests.ShouldBeEmpty();
        _intents.Added.ShouldBeEmpty();
        existing.DomainEvents.ShouldBeEmpty(
            "the first verdict is already staged, and a second is not idempotent downstream");
    }

    [Fact]
    public async Task The_record_is_locked_before_anything_else_is_read()
    {
        _orders.Record = null;

        await Should.ThrowAsync<PaymentOrderNotYetKnownException>(() =>
            Handler().HandleAsync(
                new AuthorisePaymentCommand(Guid.CreateVersion7(), 1m, "EUR"), TestContext.Current.CancellationToken));
        _orders.LockCalls.ShouldBe(1);
        _intents.GetCallsBeforeLock.ShouldBe(0);
    }

    private sealed class FakeOrderStore : IPaymentOrderStore
    {
        public PaymentOrderRecord? Record { get; set; }

        public int LockCalls { get; private set; }

        public Task RecordPlacedAsync(
            OrderId id,
            Guid customerId,
            decimal total,
            string currency,
            DateTimeOffset placedAt,
            CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the authorise path.");

        public Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the authorise path.");

        public Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct)
        {
            LockCalls++;
            return Task.FromResult(Record);
        }
    }

    // Holds a reference to the order store so a call made before the lock is
    // taken can be told apart from one made after.
    private sealed class FakeIntents(FakeOrderStore orders) : IPaymentIntentRepository
    {
        private readonly Dictionary<OrderId, PaymentIntent> _byOrder = [];

        public List<PaymentIntent> Added { get; } = [];

        public int GetCallsBeforeLock { get; private set; }

        public void Seed(PaymentIntent intent) => _byOrder[intent.Id] = intent;

        public Task<PaymentIntent?> GetAsync(OrderId id, CancellationToken ct)
        {
            if (orders.LockCalls == 0)
                GetCallsBeforeLock++;

            return Task.FromResult(_byOrder.GetValueOrDefault(id));
        }

        public void Add(PaymentIntent intent)
        {
            Added.Add(intent);
            _byOrder[intent.Id] = intent;
        }
    }

    private sealed class FakeProvider : IPaymentProvider
    {
        public AuthorisationResult Answer { get; set; } = new AuthorisationResult.Authorised("psp_default");

        public List<AuthorisationRequest> Requests { get; } = [];

        public Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }

        public Task VoidAsync(VoidRequest request, CancellationToken ct) =>
            throw new NotSupportedException("Not exercised on the authorise path.");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Orders;
using Payments.Domain.Orders;
using Payments.Infrastructure.Persistence;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class PaymentOrderStoreTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Placed = new(2026, 9, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cancelled = Placed.AddMinutes(3);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<T> InTransaction<T>(Func<IPaymentOrderStore, Task<T>> act)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        PaymentsDbContext db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        IPaymentOrderStore store = scope.ServiceProvider.GetRequiredService<IPaymentOrderStore>();
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        T result = await act(store);
        await tx.CommitAsync(TestContext.Current.CancellationToken);
        return result;
    }

    // Task<int>, not Task: CA1859 flags a wrapper that only ever returns the
    // Task<int> overload's result as one that should say so in its signature.
    private Task<int> InTransaction(Func<IPaymentOrderStore, Task> act) =>
        InTransaction(async s => { await act(s); return 0; });

    [Fact]
    public async Task Placed_then_cancelled_and_cancelled_then_placed_leave_the_same_row()
    {
        OrderId first = OrderId.New();
        OrderId second = OrderId.New();
        Guid customer = Guid.CreateVersion7();

        await InTransaction(s => s.RecordPlacedAsync(first, customer, 25.50m, "EUR", Placed, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordCancelledAsync(first, Cancelled, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordCancelledAsync(second, Cancelled, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordPlacedAsync(second, customer, 25.50m, "EUR", Placed, TestContext.Current.CancellationToken));

        PaymentOrderRecord? a = await InTransaction(s => s.LockAsync(first, TestContext.Current.CancellationToken));
        PaymentOrderRecord? b = await InTransaction(s => s.LockAsync(second, TestContext.Current.CancellationToken));

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        (b with { OrderId = a.OrderId }).ShouldBe(a, "the two events commute: each fills only its own columns");
        a.IsPlaced.ShouldBeTrue();
        a.IsCancelled.ShouldBeTrue();
        a.CustomerId.ShouldBe(customer);
        a.TotalAmount.ShouldBe(25.50m);
        a.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task A_cancellation_with_no_placement_is_a_tombstone_that_is_not_placed()
    {
        OrderId order = OrderId.New();

        await InTransaction(s => s.RecordCancelledAsync(order, Cancelled, TestContext.Current.CancellationToken));
        PaymentOrderRecord? record = await InTransaction(s => s.LockAsync(order, TestContext.Current.CancellationToken));

        record.ShouldNotBeNull();
        record.IsPlaced.ShouldBeFalse();
        record.IsCancelled.ShouldBeTrue();
        record.CustomerId.ShouldBeNull();
    }

    [Fact]
    public async Task A_redelivered_placement_writes_the_same_values_and_one_row()
    {
        OrderId order = OrderId.New();
        Guid customer = Guid.CreateVersion7();

        await InTransaction(s => s.RecordPlacedAsync(order, customer, 10m, "EUR", Placed, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordPlacedAsync(order, customer, 10m, "EUR", Placed, TestContext.Current.CancellationToken));

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM payments.PaymentOrders WHERE OrderId = {0}", order.Value))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_second_cancellation_keeps_the_first_instant()
    {
        OrderId order = OrderId.New();

        await InTransaction(s => s.RecordCancelledAsync(order, Cancelled, TestContext.Current.CancellationToken));
        await InTransaction(s => s.RecordCancelledAsync(order, Cancelled.AddHours(1), TestContext.Current.CancellationToken));
        PaymentOrderRecord? record = await InTransaction(s => s.LockAsync(order, TestContext.Current.CancellationToken));

        record!.CancelledAt.ShouldBe(Cancelled, "the order was cancelled once; a redelivery does not move when");
    }

    [Fact]
    public async Task An_unknown_order_locks_nothing_and_reads_null()
    {
        (await InTransaction(s => s.LockAsync(OrderId.New(), TestContext.Current.CancellationToken))).ShouldBeNull();
    }

    [Fact]
    public async Task The_store_refuses_to_run_outside_a_transaction()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IPaymentOrderStore store = scope.ServiceProvider.GetRequiredService<IPaymentOrderStore>();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            store.LockAsync(OrderId.New(), TestContext.Current.CancellationToken));
    }
}

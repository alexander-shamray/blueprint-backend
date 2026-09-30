using System.Collections.Concurrent;
using System.Diagnostics;
using Payments.Application.Orders;
using Payments.Domain.Orders;

namespace Payments.TestSupport;

/// <summary>Latching signals for when a consumer reaches Payments' record of an order, observed not timed.</summary>
public sealed class ObservedOrderStore
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _locked = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _stamping = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<long>> _lockedAt = new();

    /// <summary>Completes once <see cref="IPaymentOrderStore.LockAsync"/> holds the order's lock.</summary>
    public Task Locked(Guid order) => Signal(_locked, order).Task;

    /// <summary>
    /// Each entry to <see cref="IPaymentOrderStore.LockAsync"/> for the order, as a <see cref="Stopwatch"/> timestamp.
    /// </summary>
    public IReadOnlyList<long> LockedAt(Guid order) => [.. _lockedAt.GetOrAdd(order, _ => new())];

    /// <summary>Completes once <see cref="IPaymentOrderStore.RecordCancelledAsync"/> is entered.</summary>
    public Task Stamping(Guid order) => Signal(_stamping, order).Task;

    /// <summary>The registered store, observed.</summary>
    public IPaymentOrderStore Wrap(IPaymentOrderStore inner) => new Observed(this, inner);

    private static TaskCompletionSource Signal(ConcurrentDictionary<Guid, TaskCompletionSource> signals, Guid order) =>
        signals.GetOrAdd(order, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private sealed class Observed(ObservedOrderStore observer, IPaymentOrderStore inner) : IPaymentOrderStore
    {
        public Task RecordPlacedAsync(
            OrderId id,
            Guid customerId,
            decimal total,
            string currency,
            DateTimeOffset placedAt,
            CancellationToken ct) =>
            inner.RecordPlacedAsync(id, customerId, total, currency, placedAt, ct);

        public Task RecordCancelledAsync(OrderId id, DateTimeOffset cancelledAt, CancellationToken ct)
        {
            Signal(observer._stamping, id.Value).TrySetResult();
            return inner.RecordCancelledAsync(id, cancelledAt, ct);
        }

        public async Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct)
        {
            observer._lockedAt.GetOrAdd(id.Value, _ => new()).Enqueue(Stopwatch.GetTimestamp());
            PaymentOrderRecord? record = await inner.LockAsync(id, ct);

            // Raised once the lock is held, so a caller that writes the record next queues behind it.
            Signal(observer._locked, id.Value).TrySetResult();
            return record;
        }
    }
}

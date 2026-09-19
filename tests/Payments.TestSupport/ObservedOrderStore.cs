using System.Collections.Concurrent;
using System.Diagnostics;
using Payments.Application.Orders;
using Payments.Domain.Orders;

namespace Payments.TestSupport;

/// <summary>
/// Signals when a consumer reaches Payments' record of an order, so a caller
/// can prove a unit is at the lock (spec, section 6) rather than infer it from
/// elapsed time. The stamp signal is raised as the call is entered, before its
/// SQL runs, and the lock signal once the lock is held; both latch, so a caller
/// that asks after the fact still sees them. Test support only; the host never
/// registers it.
/// </summary>
public sealed class ObservedOrderStore
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _locked = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _stamping = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<long>> _lockedAt = new();

    /// <summary>Completes once <see cref="IPaymentOrderStore.LockAsync"/> holds the order's lock.</summary>
    public Task Locked(Guid order) => Signal(_locked, order).Task;

    /// <summary>
    /// Each entry to <see cref="IPaymentOrderStore.LockAsync"/> for the order, in order, as a
    /// <see cref="Stopwatch"/> timestamp: the gap between two is how long a redelivery held the
    /// message, which a signal that latches on the first cannot show.
    /// </summary>
    public IReadOnlyList<long> LockedAt(Guid order) => [.. _lockedAt.GetOrAdd(order, _ => new())];

    /// <summary>
    /// Completes once <see cref="IPaymentOrderStore.RecordCancelledAsync"/> is entered for the order.
    /// </summary>
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

            // Raised once the lock is held, not on entry: a caller that writes
            // the record next must queue behind the lock rather than beat it.
            Signal(observer._locked, id.Value).TrySetResult();
            return record;
        }
    }
}

using System.Collections.Concurrent;
using Payments.Application.Orders;
using Payments.Domain.Orders;

namespace Payments.TestSupport;

/// <summary>
/// Signals when a consumer reaches Payments' record of an order, so a caller
/// can prove a unit is at the lock (spec, section 6) rather than infer it from
/// elapsed time. Each signal is raised as the call is entered, before its SQL
/// runs, and latches: a caller that asks after the fact still sees it. Test
/// support only; the host never registers it.
/// </summary>
public sealed class ObservedOrderStore
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _locked = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _stamping = new();

    /// <summary>Completes once <see cref="IPaymentOrderStore.LockAsync"/> is entered for the order.</summary>
    public Task Locked(Guid order) => Signal(_locked, order).Task;

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

        public Task<PaymentOrderRecord?> LockAsync(OrderId id, CancellationToken ct)
        {
            Signal(observer._locked, id.Value).TrySetResult();
            return inner.LockAsync(id, ct);
        }
    }
}

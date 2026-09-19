using Microsoft.EntityFrameworkCore;
using Payments.Domain.Intents;
using Payments.Domain.Orders;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// A plain read: the handler has already locked the order's record, which is
/// the lock that serialises every write for the order (spec, section 6).
/// </summary>
internal sealed class PaymentIntentRepository(PaymentsDbContext db) : IPaymentIntentRepository
{
    public Task<PaymentIntent?> GetAsync(OrderId id, CancellationToken ct) =>
        db.PaymentIntents.SingleOrDefaultAsync(i => i.Id == id, ct);

    public void Add(PaymentIntent intent) => db.Add(intent);
}

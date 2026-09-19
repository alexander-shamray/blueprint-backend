using Payments.Domain.Orders;

namespace Payments.Domain.Intents;

/// <summary>The authorise path's write side: one lookup and one staged add (spec, section 5).</summary>
public interface IPaymentIntentRepository
{
    Task<PaymentIntent?> GetAsync(OrderId id, CancellationToken ct);

    void Add(PaymentIntent intent);
}

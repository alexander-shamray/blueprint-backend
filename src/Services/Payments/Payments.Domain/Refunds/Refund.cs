using Common.Domain;
using Payments.Domain.Intents;
using Payments.Domain.Orders;
using Payments.Domain.Refunds.Events;

namespace Payments.Domain.Refunds;

/// <summary>§3.2's money voided back, only from an authorised intent: its event reports an act (ADR-049).</summary>
public sealed class Refund : AggregateRoot<OrderId>
{
    public string Reference { get; private set; } = "";
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public DateTimeOffset VoidedAt { get; private set; }

    private Refund() { }

    public static Refund Voided(PaymentIntent authorised, DateTimeOffset now)
    {
        if (authorised.Status != PaymentIntentStatus.Authorised || authorised.Reference is null)
            throw new DomainException("Only an authorised payment can be refunded.");

        Refund refund = new()
        {
            Id = authorised.Id,
            Reference = authorised.Reference,
            Amount = authorised.Amount,
            Currency = authorised.Currency,
            VoidedAt = now
        };
        refund.Raise(new PaymentRefundedDomainEvent(refund.Id, refund.Reference, refund.Amount, refund.Currency, now));
        return refund;
    }
}

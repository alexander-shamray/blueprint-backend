using Common.Domain;
using Payments.Domain.Intents.Events;
using Payments.Domain.Orders;

namespace Payments.Domain.Intents;

/// <summary>
/// §3.2's <c>PaymentIntent</c>: the provider's verdict on one order. Created in
/// a terminal state, because the provider answers inside the unit that creates
/// it (spec, section 4); there is no pending row.
/// </summary>
public sealed class PaymentIntent : AggregateRoot<OrderId>
{
    public PaymentIntentStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "";
    public string? Reference { get; private set; }
    public string? DeclineReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private PaymentIntent() { }

    private PaymentIntent(OrderId id, PaymentIntentStatus status, decimal amount, string currency, DateTimeOffset now)
    {
        Id = id;
        Status = status;
        Amount = amount;
        Currency = currency;
        CreatedAt = now;
    }

    public static PaymentIntent Authorise(OrderId id, decimal amount, string currency, string reference, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reference))
            throw new DomainException("An authorisation needs the provider's reference.");

        PaymentIntent intent = new(id, PaymentIntentStatus.Authorised, amount, currency, now) { Reference = reference };
        intent.Raise(new PaymentAuthorisedDomainEvent(id, reference, amount, currency, now));
        return intent;
    }

    public static PaymentIntent Decline(OrderId id, decimal amount, string currency, string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("A decline needs a reason.");

        PaymentIntent intent = new(id, PaymentIntentStatus.Declined, amount, currency, now) { DeclineReason = reason };
        intent.Raise(new PaymentDeclinedDomainEvent(id, reason, now));
        return intent;
    }
}

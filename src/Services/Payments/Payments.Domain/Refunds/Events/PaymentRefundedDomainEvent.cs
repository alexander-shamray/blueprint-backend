using Common.Domain;
using Payments.Domain.Orders;

namespace Payments.Domain.Refunds.Events;

/// <summary>Raised when a <see cref="Refund"/> records money voided back (spec, section 5).</summary>
public sealed record PaymentRefundedDomainEvent(
    OrderId OrderId,
    string Reference,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

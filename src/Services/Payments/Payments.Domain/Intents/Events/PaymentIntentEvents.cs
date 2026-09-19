using Common.Domain;
using Payments.Domain.Orders;

namespace Payments.Domain.Intents.Events;

/// <summary>Raised when a <see cref="PaymentIntent"/> is created authorised (spec, section 5).</summary>
public sealed record PaymentAuthorisedDomainEvent(
    OrderId OrderId,
    string Reference,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a <see cref="PaymentIntent"/> is created declined (spec, section 5).</summary>
public sealed record PaymentDeclinedDomainEvent(
    OrderId OrderId,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

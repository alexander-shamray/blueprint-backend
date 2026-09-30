using Common.Domain;
using Payments.Domain.Orders;

namespace Payments.Domain.Intents.Events;

/// <summary>Raised when a <see cref="PaymentIntent"/> is created authorised.</summary>
public sealed record PaymentAuthorisedDomainEvent(
    OrderId OrderId,
    string Reference,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Raised when a <see cref="PaymentIntent"/> is created declined.</summary>
public sealed record PaymentDeclinedDomainEvent(
    OrderId OrderId,
    string Reason,
    DateTimeOffset OccurredAt) : IDomainEvent;

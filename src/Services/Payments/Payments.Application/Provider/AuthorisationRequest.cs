using Payments.Domain.Orders;

namespace Payments.Application.Provider;

/// <summary>
/// The payer is the order record's <c>CustomerId</c>, never a field the
/// command carried (ADR-028).
/// </summary>
public sealed record AuthorisationRequest(OrderId OrderId, Guid PayerId, decimal Amount, string Currency)
{
    public string IdempotencyKey => $"authorise:{OrderId.Value}";
}

using Payments.Domain.Orders;

namespace Payments.Application.Provider;

/// <summary>The reversal of an authorisation already recorded against the order.</summary>
public sealed record VoidRequest(OrderId OrderId, string Reference)
{
    public string IdempotencyKey => $"void:{OrderId.Value}";
}

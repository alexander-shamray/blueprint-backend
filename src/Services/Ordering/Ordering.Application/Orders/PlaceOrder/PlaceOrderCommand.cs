using Common.Application;
using Ordering.Domain.Common;

namespace Ordering.Application.Orders.PlaceOrder;

/// <summary>Place an order (§6.4), returning its id.</summary>
/// <remarks>
/// No <c>CustomerId</c>: the subject is bound from the principal, never the request (§11.4). Idempotent, since a
/// duplicate is a second order, reservation and payment (§8.5).
/// </remarks>
public sealed record PlaceOrderCommand(
    Guid CommandId,
    IReadOnlyList<PlaceOrderItem> Items,
    AddressDto ShippingAddress,
    string Currency) : ICommand<Result<Guid>>, IIdempotentCommand
{
    /// <summary>Declared rather than read off the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "ordering.order.place";
}

public sealed record PlaceOrderItem(Guid ProductId, int Quantity);

/// <summary>The wire shape of an address, since <see cref="Address"/>'s factory throws on bad input.</summary>
public sealed record AddressDto(
    string Line1,
    string? Line2,
    string City,
    string PostalCode,
    string Country)
{
    /// <summary>Called after validation; the factory's guards still run, as a backstop (§5.3).</summary>
    public Address ToDomain() => Address.Of(Line1, Line2, City, PostalCode, Country);
}

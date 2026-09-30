using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.ConfirmOrder;

/// <summary>Record that payment for an order was authorised; sent only by §9.6's saga.</summary>
/// <remarks>
/// No <c>CommandOrigin</c>, since no HTTP route maps it (§11.4). <see cref="Reference"/> is parsed in the mapper, so
/// a malformed one reaches the error queue on the first attempt (§9.4).
/// </remarks>
public sealed record ConfirmOrderCommand(Guid OrderId, PaymentReference Reference) : ICommand<Result>;

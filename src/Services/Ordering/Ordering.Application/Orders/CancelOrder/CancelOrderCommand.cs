using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.CancelOrder;

/// <summary>Cancel an order; two entry points reach it, so the origin is on the command (§11.4).</summary>
/// <remarks>
/// No <c>CustomerId</c>: the owner is read off the loaded aggregate (§11.4). Not <c>IIdempotentCommand</c>, since
/// <c>Order.Cancel</c> is idempotent (§5.4) and §9.5's inbox absorbs a redelivered <c>CancelOrder</c>.
/// </remarks>
public sealed record CancelOrderCommand(
    Guid OrderId,
    CancellationReason Reason,
    CommandOrigin InitiatedBy) : ICommand<Result>
{
    /// <summary>The trusted path, a positive claim rather than the absence of a principal (§11.4).</summary>
    public bool IsSystemInitiated => InitiatedBy is CommandOrigin.System;
}

/// <summary>Who asked, written as a literal at each entry point and never bound from a request (§11.4).</summary>
public enum CommandOrigin
{
    /// <summary>An HTTP request with a principal; the ownership check applies.</summary>
    /// <remarks>The zero value, so an origin nobody set fails closed (§11.4).</remarks>
    User,

    /// <summary>§9.6's saga compensating, already authorised at the endpoint that started it.</summary>
    System
}

using Common.Application;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.MarkOrderShipped;

/// <summary>Record that an order was despatched: Shipping's fact, recorded by Ordering's decision (§9.6).</summary>
public sealed record MarkOrderShippedCommand(Guid OrderId, TrackingNumber Tracking) : ICommand<Result>;

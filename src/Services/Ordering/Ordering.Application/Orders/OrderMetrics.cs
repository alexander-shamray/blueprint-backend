using System.Diagnostics.Metrics;
using Ordering.Domain.Common;

namespace Ordering.Application.Orders;

/// <summary>§13.3's business instruments, recorded by §6.6's projection after commit, never by a handler.</summary>
/// <remarks>Not in Common.Application, because <c>Placed</c> takes a domain <see cref="Money"/> (§13.3).</remarks>
public sealed class OrderMetrics
{
    private readonly Counter<long> _placed;
    private readonly Counter<long> _cancelled;
    private readonly Histogram<double> _value;
    private readonly Histogram<double> _fulfilmentSeconds;

    public OrderMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Ordering.Orders");

        _placed = meter.CreateCounter<long>(
            "orders.placed",
            unit: "{order}",
            description: "Orders successfully placed.");
        _cancelled = meter.CreateCounter<long>("orders.cancelled", unit: "{order}");
        _value = meter.CreateHistogram<double>("orders.value", unit: "EUR");
        _fulfilmentSeconds = meter.CreateHistogram<double>(
            "orders.fulfilment.duration",
            unit: "s",
            description: "Placed to confirmed.");
    }

    public void Placed(Money total)
    {
        _placed.Add(1, new KeyValuePair<string, object?>("currency", total.Currency));
        _value.Record((double)total.Amount, new KeyValuePair<string, object?>("currency", total.Currency));
    }

    /// <summary>Tagged with a <c>CancellationReasons</c> code, a bounded set, never an id (§13.3).</summary>
    public void Cancelled(string reason) =>
        _cancelled.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Fulfilled(TimeSpan placedToConfirmed) =>
        _fulfilmentSeconds.Record(placedToConfirmed.TotalSeconds);
}

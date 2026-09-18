using System.Diagnostics.Metrics;

namespace Inventory.Application.Reservations;

/// <summary>
/// The one business-shaped counter (spec §13). Fired by the projection that
/// claims the row, never by the handler that writes it.
/// </summary>
public sealed class InventoryMetrics
{
    private readonly Counter<long> _unreserved;

    public InventoryMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Inventory.Reservations");
        _unreserved = meter.CreateCounter<long>(
            "inventory.fulfilment.unreserved",
            unit: "{reservation}",
            description: "Despatches that met a reservation already released (ADR-029's open case).");
    }

    public void UnreservedDespatch() => _unreserved.Add(1);
}

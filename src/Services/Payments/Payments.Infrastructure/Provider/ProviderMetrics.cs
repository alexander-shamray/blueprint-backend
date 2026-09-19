using System.Diagnostics.Metrics;

namespace Payments.Infrastructure.Provider;

/// <summary>
/// One attempt that met a failing provider: a fault rather than a verdict,
/// whether the pipeline saw it or the adapter read it from the answer. A fact
/// about the provider rather than an order, so §13.3's claim rule does not
/// reach it: a unit that rolls back still met a failing provider.
/// </summary>
public sealed class ProviderMetrics
{
    public const string MeterName = "Payments.Provider";

    private readonly Counter<long> _unavailable;

    public ProviderMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(MeterName);
        _unavailable = meter.CreateCounter<long>(
            "payments.provider.unavailable",
            unit: "{attempt}",
            description:
                "Provider attempts that ended in a fault rather than a verdict; " +
                "the error queue sees only exhausted units.");
    }

    public void Unavailable() => _unavailable.Add(1);
}

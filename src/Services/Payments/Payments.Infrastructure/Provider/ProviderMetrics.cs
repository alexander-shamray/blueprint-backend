using System.Diagnostics.Metrics;

namespace Payments.Infrastructure.Provider;

/// <summary>Attempts that met a failing provider, whether the pipeline or the adapter saw the fault.</summary>
/// <remarks>A fact about the provider, not an order, so §13.3's claim rule does not reach it.</remarks>
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

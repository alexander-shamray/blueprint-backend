namespace Privacy.Infrastructure.Observability;

/// <summary>The questions <see cref="ErasureMetrics"/>' gauges ask of <c>privacy.ErasureRequests</c>.</summary>
public interface IErasureStats
{
    /// <summary>Requests that are overdue now.</summary>
    int OverdueCount();

    /// <summary>For each holder missing from at least one overdue request, how many such requests it is missing from.</summary>
    IReadOnlyDictionary<string, int> OverdueMissingByResponder();
}

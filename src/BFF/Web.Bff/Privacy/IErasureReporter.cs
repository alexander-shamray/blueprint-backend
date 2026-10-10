namespace Web.Bff.Privacy;

/// <summary>Tells Privacy this service has erased what it holds for a request, after the erasure commits (ADR-094).</summary>
public interface IErasureReporter
{
    Task ReportAsync(Guid requestId, int count, CancellationToken ct);
}

namespace Common.Application;

/// <summary>§8.5's durable marker, read and written inside the command's own transaction (ADR-037).</summary>
/// <remarks>Its one caller is §6.3's <c>TransactionBehavior</c>, the only code holding the transaction.</remarks>
public interface IIdempotencyMarkerStore
{
    /// <summary>Whether a previous attempt under this key committed.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>Stages the marker, so it commits or rolls back with the work it guards.</summary>
    Task MarkAsync(string key, CancellationToken ct);
}

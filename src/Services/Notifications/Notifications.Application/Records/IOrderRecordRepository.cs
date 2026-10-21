namespace Notifications.Application.Records;

/// <summary>§5.6's repository for <see cref="OrderRecord"/>, keyed by the order as every event names it.</summary>
public interface IOrderRecordRepository
{
    Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct);

    /// <summary>
    /// Reads the record under an update lock held to the commit, which an erasure's held read waits on (ADR-097).
    /// </summary>
    Task<OrderRecord?> GetHeldAsync(Guid orderId, CancellationToken ct);

    void Add(OrderRecord record);
}

namespace Notifications.Application.Records;

/// <summary>§6.3's repository for <see cref="OrderRecord"/>, keyed by the order as every event names it.</summary>
public interface IOrderRecordRepository
{
    Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct);

    void Add(OrderRecord record);
}

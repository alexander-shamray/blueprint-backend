namespace Notifications.Application.Records;

/// <summary>§5.6's repository for the push deliveries a notice fans out into (the push design's section 3).</summary>
public interface IPushDeliveryRepository
{
    /// <summary>One delivery by its id, tracked, for the push send worker to move and commit.</summary>
    Task<PushDelivery?> GetAsync(Guid deliveryId, CancellationToken ct);

    void Add(PushDelivery delivery);
}

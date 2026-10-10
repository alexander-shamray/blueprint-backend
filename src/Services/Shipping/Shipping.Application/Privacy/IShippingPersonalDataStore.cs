namespace Shipping.Application.Privacy;

/// <summary>What Shipping holds of a customer, erased in a raw statement on the unit of work's transaction.</summary>
public interface IShippingPersonalDataStore
{
    /// <summary>Deletes the subject's delivery addresses, which ADR-052 keeps apart from the shipments.</summary>
    Task<int> DeleteAddressesAsync(Guid subjectId, CancellationToken ct);
}

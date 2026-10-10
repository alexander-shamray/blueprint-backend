namespace Shipping.Application.Privacy;

/// <summary>What Shipping holds of a customer, which ADR-052 keeps in a table apart from the shipments.</summary>
public interface IShippingPersonalDataStore
{
    /// <summary>The orders whose delivery address is the subject's, read before the rows go.</summary>
    Task<IReadOnlyList<Guid>> AddressedOrdersAsync(Guid subjectId, CancellationToken ct);

    /// <summary>Deletes the subject's delivery addresses on the unit of work's transaction.</summary>
    Task<int> DeleteAddressesAsync(Guid subjectId, CancellationToken ct);
}

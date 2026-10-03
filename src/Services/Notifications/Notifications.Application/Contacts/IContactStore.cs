namespace Notifications.Application.Contacts;

/// <summary>ADR-052's contact row, the one table in this service a mailbox lands in.</summary>
/// <remarks>A port, not <c>IUnitOfWork.ExecuteRawAsync</c>, as <see cref="GetAsync"/> returns what it read.</remarks>
public interface IContactStore
{
    Task SaveAsync(Guid customerId, ContactLookup.Found contact, DateTimeOffset fetchedAt, CancellationToken ct);

    Task<ContactRecord?> GetAsync(Guid customerId, CancellationToken ct);

    /// <summary>ADR-052's delete when the owner says the customer does not exist, and §11.7's erasure.</summary>
    Task DeleteAsync(Guid customerId, CancellationToken ct);
}

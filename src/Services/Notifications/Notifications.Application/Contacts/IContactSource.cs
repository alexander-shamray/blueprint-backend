namespace Notifications.Application.Contacts;

/// <summary>ADR-052's read of a customer's mailbox and locale from Keycloak, the one place either is held.</summary>
/// <remarks>A fault throws, and a refused credential throws <see cref="ContactSourceRefusedException"/>.</remarks>
public interface IContactSource
{
    Task<ContactLookup> GetAsync(Guid customerId, CancellationToken ct);
}

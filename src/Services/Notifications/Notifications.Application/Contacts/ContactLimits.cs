namespace Notifications.Application.Contacts;

/// <summary>What a contact may carry and still be stored, the widths of <c>notifications.ContactRecords</c>.</summary>
public static class ContactLimits
{
    /// <summary>Keycloak's own column for a user's email, so nothing the owner can hold is refused here.</summary>
    public const int MaxEmailLength = 255;
}

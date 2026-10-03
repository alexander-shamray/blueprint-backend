namespace Notifications.Application.Contacts;

/// <summary>The owner or the identity provider refused this host's credential, a defect ADR-052 counts.</summary>
public sealed class ContactSourceRefusedException : Exception
{
    public ContactSourceRefusedException()
    {
    }

    public ContactSourceRefusedException(string message)
        : base(message)
    {
    }

    public ContactSourceRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

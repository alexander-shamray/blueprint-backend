namespace Shipping.Application.Addresses;

/// <summary>The owner or the identity provider refused this host's credential, a defect ADR-052 counts.</summary>
public sealed class AddressSourceRefusedException : Exception
{
    public AddressSourceRefusedException()
    {
    }

    public AddressSourceRefusedException(string message)
        : base(message)
    {
    }

    public AddressSourceRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

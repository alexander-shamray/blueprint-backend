namespace Shipping.Application.Addresses;

/// <summary>
/// The owner refused this host's credential, or the identity provider refused
/// the host (ADR-052). The worker's backoff owns it, and it is counted because
/// somebody has to see it.
/// </summary>
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

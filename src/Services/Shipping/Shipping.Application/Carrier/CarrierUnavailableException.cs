namespace Shipping.Application.Carrier;

/// <summary>
/// A fault from the carrier that is not an answer: the worker's backoff owns
/// it, and it never becomes a refusal (spec, section 9).
/// </summary>
public sealed class CarrierUnavailableException : Exception
{
    public CarrierUnavailableException()
    {
    }

    public CarrierUnavailableException(string message)
        : base(message)
    {
    }

    public CarrierUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

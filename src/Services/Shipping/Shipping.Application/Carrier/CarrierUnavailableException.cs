namespace Shipping.Application.Carrier;

/// <summary>A fault from the carrier that is not an answer, so it backs the row off and never refuses it.</summary>
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

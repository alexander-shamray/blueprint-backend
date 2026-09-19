namespace Payments.Application.Intents;

/// <summary>
/// The order record has not yet arrived from <c>RecordOrderPlaced</c>. A wait,
/// not a fault (§3.2): thrown so the endpoint's delayed redelivery retries the
/// whole unit rather than answering a verdict for an order Payments cannot
/// yet see.
/// </summary>
public sealed class PaymentOrderNotYetKnownException : Exception
{
    public PaymentOrderNotYetKnownException()
    {
    }

    public PaymentOrderNotYetKnownException(string message)
        : base(message)
    {
    }

    public PaymentOrderNotYetKnownException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

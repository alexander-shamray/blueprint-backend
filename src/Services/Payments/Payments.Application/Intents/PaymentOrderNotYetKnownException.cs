namespace Payments.Application.Intents;

/// <summary>The order record has not arrived yet: a wait for delayed redelivery, not a verdict (§3.2).</summary>
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

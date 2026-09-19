namespace Payments.Application;

/// <summary>
/// The command's figures, or a replayed key's, disagree with what Payments
/// holds. A fault, not a verdict (spec, section 1): excluded from retry, so
/// it reaches the error queue §13.6 pages on.
/// </summary>
public sealed class PaymentMismatchException : Exception
{
    public PaymentMismatchException()
    {
    }

    public PaymentMismatchException(string message)
        : base(message)
    {
    }

    public PaymentMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

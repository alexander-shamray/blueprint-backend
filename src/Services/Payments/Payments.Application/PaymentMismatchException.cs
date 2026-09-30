namespace Payments.Application;

/// <summary>Figures that disagree with what Payments holds: a fault, not retried, which §13.6 pages on.</summary>
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

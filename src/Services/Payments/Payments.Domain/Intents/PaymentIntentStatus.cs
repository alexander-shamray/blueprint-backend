namespace Payments.Domain.Intents;

/// <summary>The two terminal verdicts a <see cref="PaymentIntent"/> is created in.</summary>
public enum PaymentIntentStatus
{
    Authorised,
    Declined,
}

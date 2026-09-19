namespace Payments.Domain.Intents;

/// <summary>The two terminal verdicts a <see cref="PaymentIntent"/> is created in (spec, section 5).</summary>
public enum PaymentIntentStatus
{
    Authorised,
    Declined,
}

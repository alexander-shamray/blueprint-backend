namespace Payments.Domain.Intents;

/// <summary>ADR-047's one reason of Payments' own; every other decline carries the provider's code.</summary>
public static class DeclineReasons
{
    public const string OrderCancelled = "order_cancelled";
}

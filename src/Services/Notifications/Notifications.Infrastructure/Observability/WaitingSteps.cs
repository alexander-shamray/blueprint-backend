namespace Notifications.Infrastructure.Observability;

/// <summary>What a waiting notice waits on, the <c>step</c> attribute of <c>notifications.waiting</c>.</summary>
public static class WaitingSteps
{
    /// <summary>Ordering's record of the order, or a decline's cancellation on it (ADR-049).</summary>
    public const string OrderRecord = "order_record";

    /// <summary>The owner's answer about the customer, or anything else before the intent (ADR-052).</summary>
    public const string Contact = "contact";

    /// <summary>The relay, once the intent is stamped.</summary>
    public const string Relay = "relay";

    public static IReadOnlyList<string> All { get; } = [OrderRecord, Contact, Relay];
}

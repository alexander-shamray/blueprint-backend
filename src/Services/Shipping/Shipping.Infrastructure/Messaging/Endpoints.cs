namespace Shipping.Infrastructure.Messaging;

/// <summary>The queues this service sends to, which a send needs by address and a publish does not.</summary>
internal static class Endpoints
{
    /// <summary>Where a holder reports an erasure (ADR-094).</summary>
    public static readonly Uri PrivacyCompletionsQueue = new("queue:privacy-completions");
}

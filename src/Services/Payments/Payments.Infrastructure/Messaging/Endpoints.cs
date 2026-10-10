namespace Payments.Infrastructure.Messaging;

/// <summary>The queues this service sends to, explicit rather than <c>EndpointConvention.Map</c>.</summary>
/// <remarks>
/// <c>queue:</c> is MassTransit's short-address form. The name must match Privacy's receive endpoint; an
/// undeclared queue is silence (§9.4).
/// </remarks>
internal static class Endpoints
{
    /// <summary>Where a holder reports an erasure (ADR-094).</summary>
    public static readonly Uri PrivacyCompletionsQueue = new("queue:privacy-completions");
}

namespace Payments.Infrastructure.Messaging;

/// <summary>The endpoints this service sends to, explicit rather than <c>EndpointConvention.Map</c>.</summary>
/// <remarks>
/// <c>exchange:</c> is MassTransit's short-address form for an exchange alone. The name must match Privacy's
/// receive endpoint; an exchange no queue is bound to is silence (§9.4).
/// </remarks>
internal static class Endpoints
{
    /// <summary>Where a holder reports an erasure (ADR-094): Privacy's exchange.</summary>
    /// <remarks>A <c>queue:</c> send binds, which takes the read that consumes the queue (ADR-190).</remarks>
    public static readonly Uri PrivacyCompletionsExchange = new("exchange:privacy-completions");
}

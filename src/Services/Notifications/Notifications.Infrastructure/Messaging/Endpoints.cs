namespace Notifications.Infrastructure.Messaging;

/// <summary>The endpoints this service sends to, which a send needs by address and a publish does not.</summary>
internal static class Endpoints
{
    /// <summary>Where a holder reports an erasure (ADR-094): Privacy's exchange.</summary>
    /// <remarks>A <c>queue:</c> send binds, which takes the read that consumes the queue (ADR-190).</remarks>
    public static readonly Uri PrivacyCompletionsExchange = new("exchange:privacy-completions");
}

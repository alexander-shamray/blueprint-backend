namespace Common.Infrastructure.Messaging;

/// <summary>ADR-191's rule for the exchange a delivery may come through, apart from the filter.</summary>
public static class SourceExchange
{
    /// <summary>MassTransit's suffix for the delayed exchange in front of an endpoint's (ADR-021).</summary>
    public const string DelaySuffix = "_delay";

    /// <summary>A publish names the type's exchange, a send the endpoint's, a timeout its delay's.</summary>
    /// <remarks>The broker sets the exchange a delivery names, so the publisher cannot choose it (ADR-191).</remarks>
    public static bool Admits(string exchange, string typeExchange, string endpoint) =>
        string.Equals(exchange, typeExchange, StringComparison.Ordinal) ||
        string.Equals(exchange, endpoint, StringComparison.Ordinal) ||
        string.Equals(exchange, endpoint + DelaySuffix, StringComparison.Ordinal);
}

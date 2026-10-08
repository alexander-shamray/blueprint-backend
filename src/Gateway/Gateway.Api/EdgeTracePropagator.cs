using System.Diagnostics;

namespace Gateway.Api;

/// <summary>The hosting layer's propagator at the edge, which reads no trace context from a caller.</summary>
/// <remarks>
/// The edge's callers are the internet, so a traceparent, tracestate or baggage they send would choose the trace
/// every service, outbox row and worker span joins. Every request starts a root trace here instead (ADR-083).
/// </remarks>
public sealed class EdgeTracePropagator : DistributedContextPropagator
{
    // The outbound members are abstract, so they delegate; YARP injects through DistributedContextPropagator.Current.
    private readonly DistributedContextPropagator _outbound = CreateDefaultPropagator();

    public override IReadOnlyCollection<string> Fields => _outbound.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) =>
        _outbound.Inject(activity, carrier, setter);

    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState)
    {
        traceId = null;
        traceState = null;
    }

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter) => null;
}

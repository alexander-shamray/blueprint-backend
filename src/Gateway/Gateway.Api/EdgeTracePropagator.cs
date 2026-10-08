using System.Diagnostics;

namespace Gateway.Api;

/// <summary>Reads no trace context from a caller, and writes the edge's own downstream as the default does.</summary>
/// <remarks>
/// The edge's callers are the internet, so a traceparent, tracestate or baggage they send would choose the trace
/// every service, outbox row and worker span joins. Every request starts a root trace here instead (§13.2).
/// </remarks>
public sealed class EdgeTracePropagator : DistributedContextPropagator
{
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

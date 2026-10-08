# ADR-083 — The edge starts a root trace, and a caller's trace context stops there

**Decision.** The gateway reads no W3C trace context from a request:
`EdgeTracePropagator`, registered as the hosting layer's
`DistributedContextPropagator`, extracts no `traceparent`, `tracestate` or
`baggage`, and the gateway's OpenTelemetry propagator is `TraceContextPropagator`
alone, under which the ASP.NET Core instrumentation leaves extraction to that
hosting layer. Every request therefore starts a root trace at the edge, and
what the gateway sends downstream is that trace, injected as the default
propagator would. Services behind it keep honouring the context they receive,
which is now always the edge's.
**Why.** The edge's callers are the internet. A `traceparent` they chose became
the parent of the gateway's span, travelled to every service, was stored on
outbox rows ([§9.4](../09-messaging.md)) and was replayed as the parent of the
dispatcher's and the workers' spans, so a caller could pick the trace its
traffic joins, or collide with someone else's. Nothing read baggage, but it was
forwarded too. A trace is how an incident is followed across services, so the
id that joins them has to be one the platform minted. OpenTelemetry's
instrumentation re-extracts the headers itself under any propagator but
`TraceContextPropagator`, which is why the second half exists: with the hosting
propagator alone, the client's trace id still reached the service under a new
span id, and `EdgeTraceContextTests` failed on it.
**Consequences.** A partner whose own tracing calls the platform no longer sees
its trace continue inside it; the edge's trace id is what a support exchange
quotes, and §10.4's correlation ID, which a caller may still choose, is the
field that crosses the boundary. The propagator setting is process-wide, which
is safe only because the gateway is its own process. An upstream edge that
should be trusted with trace context, such as a mesh, would need a narrower rule
than this one, and none exists.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

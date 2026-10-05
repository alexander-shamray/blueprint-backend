# ADR-066 — A request past its host's deadline is answered 504

**Decision.** Every host ends a request that outlives its deadline and answers
it `504 Gateway Timeout` in [§10.5](../10-api-gateway.md)'s problem shape, with
`code` `request.timed_out`. The deadline is
`ServiceOptions.OperationTimeout` for a service and the BFF, and
`GatewayLimits.RequestTimeout` for the gateway, which is longer, so §9.7's
timeouts still decrease inwards. `AddCommonWebDefaults` registers ASP.NET
Core's request-timeout middleware with that deadline and the writer, and each
`Program.cs` places `UseRequestTimeouts` below `UseExceptionHandler`.
An endpoint that legitimately runs longer opts out with
`DisableRequestTimeout`, beside its mapping, where a reviewer sees it.
**Why.** §9.7 named four timeout layers and two of them never fired: the
service operation total was a number the outbound budgets were checked
against, not a deadline a request met, and the gateway set none at all. A
stalled handler held its connection and its thread until the client gave up,
which is exactly what the decreasing-inwards rule exists to prevent. 504 and
not 503: the host is up and serving others, and it gave up on this one
request, which is what 504 says of an intermediary and what every layer above
a service is. A `code` of its own, because the response does not say whether
the work took effect — the deadline can fall after a commit — so a client has
to be able to tell it from a 503, a request the service could not serve.
**Consequences.** The middleware is cooperative: it cancels `RequestAborted`,
and a handler that does not pass its token on runs to the end and answers
late. Placed above `UseExceptionHandler` it would never fire, since that
handler, then inside it, answers a cancelled request 499 first; the order is
measured by a test. The gateway's forwarder answers a cancelled request itself,
as a 400, so its pipeline hands the deadline back as the cancellation it was.
Each deadline sits inside `HostOptions.ShutdownTimeout`, so a request in flight
at a stop still drains (§15.3). No endpoint opts out today. Clients switching
on `code` gain a value: `blueprint-frontend`'s error mapper is told it by name.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

# ADR-159 — Framework categories log at Warning unless configured (#1532)

**Decision.** `AddObservability` holds the framework categories in
`ObservabilityExtensions.FrameworkCategories` at `Warning`: ASP.NET Core's
hosting, routing and result lines, EF Core's command log, `HttpClient` and
YARP's forwarder. They are named by subcategory, so ASP.NET Core's
authentication and authorization lines, which carry the reason for a 401 or a
403 that no span records, keep the host's default. A level the configuration
names under `Logging:LogLevel` for one of the listed categories wins, so an
operator can lower one while debugging. The gateway names two: its
`appsettings.json` keeps `Microsoft.AspNetCore.Hosting` and
`Yarp.ReverseProxy.Forwarder` at `Information`, because those request and
response lines are the edge's log that [§10.1](../10-api-gateway.md)
promises, and the forwarder's are the ones written inside the correlation
scope ([§10.4](../10-api-gateway.md)). The stress override holds both at
`Warning` for the run.
**Why.** The stress run of 2026-10-28 sent 17 000 to 25 000 log records a
second against 18 idle. The LGTM container's collector held its log export
queue full and refused 7.2 million records in 30 minutes, while Loki stored
about 330 000. So in the window an operator reads logs to find what broke,
more than 95% were dropped, the platform's own lines among them. Of the lines
that survived, about 90% came from these categories. Each logs a line per
request, result, proxied call or SQL command, and each repeats what the
request's span already carries, so holding them at `Warning` loses no fact a
trace lacks. A filter added in code outranks a configured rule for the same
category, so the code adds one only where the configuration names none.
**Consequences.** A service no longer logs a request's arrival, its route, its
result or its SQL at `Information`; a reader who wants them reads the trace,
or lowers the category through configuration for the session. A configured
level for a broader category, `Microsoft.AspNetCore` for example, does not
reach a listed one, since a longer category wins; the operator names the
listed category itself. During a stress run the gateway writes no request
line either, so the edge's record of a request under load is its span where
[ADR-160](ADR-160-the-stress-override-samples-traces-parent-based-1533.md)'s
sampler keeps one, and otherwise there is none. A framework category not in
the list still logs at `Information`, and one found to log per request is
added to the list rather than filtered per host.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

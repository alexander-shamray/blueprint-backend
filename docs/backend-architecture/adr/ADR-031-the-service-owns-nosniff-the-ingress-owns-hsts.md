# ADR-031 — The service owns `nosniff`; the Ingress owns HSTS

**Decision.** `Common.Web` ships `UseSecurityHeaders`
([§10.6](../10-api-gateway.md)), called outermost by all four hosts, and it sets
one header: `X-Content-Type-Options: nosniff`. `Strict-Transport-Security`,
`X-Frame-Options` and `Content-Security-Policy` are deliberately not set by any
host.

**Why.** [#39](https://github.com/alexander-shamray/blueprint-backend/issues/39)
asked, among other things, for an explicit owner for the response security
headers. There was none — the blueprint did not discuss them as done, as
deferred, or as out of scope, which is the state a decision is meant to end.

**The edge is not in front of the other three hosts.** Putting the header on
the gateway is the obvious placement and it has a hole: §11.2 assumes the
network inside the cluster is hostile, and three of the four hosts answer
requests that never traverse the edge. A building block every host composes is
the only placement with no such gap, and it is also what §4.5's scaffold copies
into every service that follows.

**The omissions are the argued half.** HSTS belongs to the Ingress, which is
the only component in this platform that terminates TLS
([§10.1](../10-api-gateway.md), [§15.3](../15-cicd-deployment.md)); a host behind it
sees plain HTTP and would be asserting something it cannot observe. Framing and
script policies govern how a browser renders a *document*, and no host here
serves one: the API responses are `application/json` or
`application/problem+json`, and §13.5's probes are `text/plain`. A policy that
protects nothing is a policy somebody later has to reason about. `nosniff` is
the one that is not about rendering: it stops a browser reclassifying a JSON
response, including one whose body carries a value a caller supplied, as
something it will execute.

> **Where the header is written from is the whole of its correctness.**
> `UseExceptionHandler` clears the response before it writes
> [§10.5](../10-api-gateway.md)'s problem body, so a header assigned on the way in
> is gone from exactly the 500 on which a caller-supplied value is most likely
> to be reflected. The middleware registers an `OnStarting` callback instead,
> which fires after that clear. A test drives the 500 path, because the 200
> path passes either way.

**Consequences.**

- **The list is one entry, and a second needs this ADR amended.** A storefront
  serving HTML from this platform would change the framing and script argument
  above; nothing here anticipates that, deliberately.
- **The Ingress is now load-bearing for a header no chart sets.** HSTS is owed
  by `deploy/helm/`'s ingress annotations and is not there today. Named rather
  than closed: the decision is who owns it, and the owner is not the code in
  this repository.
- **Every host pays one delegate per request.** The callback is static and
  takes the response as state, so the middleware allocates nothing per request
  beyond what the pipeline already does.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

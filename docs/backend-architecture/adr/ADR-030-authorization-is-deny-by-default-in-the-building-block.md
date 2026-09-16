# ADR-030 — Authorization is deny by default, in the building block

**Decision.** `AddCommonWebDefaults` (§13.2) sets a fallback authorization
policy requiring an authenticated user, so an endpoint carrying no
authorization metadata is refused rather than admitted. A public path says
`AllowAnonymous()` — or, at the edge, names YARP's reserved `anonymous`
([§10.2](../10-api-gateway.md)). Everything already public says so: the three
probes `MapCommonHealthEndpoints` maps in every host, and Catalog's product
listing.
Every host composing that call inherits the policy, including the gateway.

**Why.** [#41](https://github.com/alexander-shamray/blueprint-backend/issues/41)
observed that `UseAuthorization` with no fallback evaluates **nothing** on an
endpoint with no policy metadata, so authorization was opt-in: it applied
exactly where somebody had written `RequireAuthorization()`. Every endpoint
group in the solution had in fact written it, which is what made the gap easy
to miss — the defect this closes is not a live exposure but the *absence of a
mechanism*. A new `*Endpoints` class that omits the line produces no compiler
error, no `ValidateOnBuild` failure, no startup warning and no failing test,
and §4.5's scaffold copies whatever the composition root does into every
service that follows.

**A fallback is the only form of this rule that cannot be forgotten.** The
alternative on offer was §11.4's proposed `EndpointDataSource` test, asserting
that every non-health endpoint carries a policy. That is a real check and it is
weaker in the way that matters: it lives in one service's test project, so a
service scaffolded from it inherits the test and a host written by hand does
not, and it fails at test time rather than refusing the request. The two are
not alternatives so much as different distances from the request — and the
fallback is the one the request itself passes through.

> **The rule reaches endpoints nobody wrote, and that is where its cost is.**
> Routing's 405 short-circuit endpoint carries no authorization metadata, so an
> anonymous request using the wrong method on a real path is now challenged
> before the method is considered. An authenticated one still receives 405.
> That is a deliberate consequence rather than a side effect: 405 tells an
> unauthenticated caller which methods a path accepts, and §11.2's posture is
> that such a caller learns nothing. `ProxiedRouteTests` pins both halves,
> because the 401 alone would go on passing if the route stopped existing.

**The third is no endpoint at all.** A fallback policy is evaluated when
routing matched *nothing*, so an anonymous request for a path this service does
not have is a 401 rather than a 404 — measured, not deduced. Taken on the same
terms: a caller with no credentials learns nothing about which paths exist, and
a 404 is exactly the disclosure §11.4's ownership rule already refuses one
resource at a time. An authenticated caller still gets the 404, and both halves
are asserted, because the 401 alone would pass against a host that had stopped
routing.

**The second endpoint nobody wrote is the OpenAPI document.** `MapOpenApi()`
carries no metadata either, so `/openapi/v1.json` now requires a caller. Taken
on the same terms: the document enumerates every route and every schema the
service has, it is not routed by the gateway, and §11.2 assumes the network
inside the cluster is hostile. `HostSmokeTests` asserts the challenge and a
second test asserts the document still generates for a caller who gets through
— a 401 on its own is satisfied by a host that has stopped serving the
document at all.

**Consequences.**

- **A route file that names no policy is no longer a public route**, so
  [§10.2](../10-api-gateway.md)'s `catalog-public` names `anonymous` explicitly.
  The chapter's old rule — that naming no policy was the only correct way to
  declare a route public — is reversed, and both the file and the chapter say
  so.
- **The named `authenticated` policy stays**, and is now belt-and-braces on
  every group that names it. It is kept because the gateway's route file names
  it, and a route file that says what it means is worth a line.
- **The failure direction is a 401 nobody expected rather than an endpoint
  nobody protected**, which is the trade this takes. A public path that loses
  its `AllowAnonymous` fails loudly at the first anonymous request instead of
  silently at the first attacker.
- **`Common.Web` gains no way to be selectively opted out of.** A host that
  wants a different fallback sets one after the call; nothing here takes a
  parameter, because a parameter is a way to turn the rule off.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

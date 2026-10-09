# ADR-089 — The browser-origin obligation binds the clients that exchange a code from a page

**Decision.** [ADR-046](ADR-046-each-client-declares-a-browser-origin-and-the-gate-asserts-the-shape.md)'s
obligation to declare a non-empty `webOrigins` binds the clients that run a
browser token exchange, `web-app` and `mobile-app`, and no other.
`realm_check.py` holds those two to `check_web_origins`; the service-account
clients it also names, `shipping-worker`, `notifications-worker` and
`web-bff`, declare `[]` and are held to none. It amends ADR-046, whose decision
said every client the gate names declares one.
**Why.** The obligation exists for a second hop: a page exchanging an
authorization code at Keycloak's token endpoint, whose answer the browser
discards unread unless an origin is granted. A client-credentials client runs
no such exchange, so it has no page whose script needs the grant, and an
origin declared for it would be a CORS grant nothing uses. ADR-046's sentence
was true while the gate named only the two browser clients; ADR-052's workers
and the BFF's client made it untrue, and the gate was right not to follow it.
**Consequences.** A client added to the gate's named set is not held to an
origin by being named; it is held only if it is added beside `web-app` and
`mobile-app` as a browser client. A public client that runs a page exchange and
is named without that entry would ship with no origin and fail as ADR-046
describes, silently, so the gate's browser-client list is the place a new
browser client has to be written.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

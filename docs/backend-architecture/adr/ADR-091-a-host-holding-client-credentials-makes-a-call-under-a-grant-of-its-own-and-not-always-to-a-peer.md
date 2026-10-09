# ADR-091 — A host holding client credentials makes a call under a grant of its own, and not always to a peer

**Decision.** A host that holds client credentials (§11.5) is a host that
makes a synchronous call under a grant of its own. The callee is a peer
service (§9.7) or the deployment's own identity provider; it is never read as
"a peer" alone. `Web.Bff` and Shipping's worker call peers, and Notifications'
worker calls Keycloak for the contact read
([ADR-052](ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)),
which is the deployment's own and no peer and no third party. This amends the
sentence in [ADR-055](ADR-055-an-outbound-hop-registers-beside-its-layer-and-the-host-calls-it.md)
that said a host holding client credentials is a host that calls a peer. The
rest of ADR-055, where a hop registers and who calls it, stands.
**Why.** ADR-055's sentence was true while the only hosts holding credentials
called peers. Notifications' worker holds them for a read that reaches
Keycloak, so "calls a peer" is false of it, and §9.7 already words the rule
as a call made under a grant, which is true of all three hosts.
**Consequences.** Nothing in the code moves. A reader who reasons from ADR-055
that every host holding credentials depends on a peer service will miss that
Notifications' worker depends on Keycloak instead; the count of hosts holding
a client secret is the count of synchronous couplings, and this record is
where the callee's kind is stated. ADR-055 itself still carries the old
sentence, as a superseded record does.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

# ADR-062 — The domain is a reference implementation

**Decision.** The e-commerce domain — the six services
[§4.1](../04-solution-structure.md) names, their events and the saga
[§9.6](../09-messaging.md) runs across them — is a reference implementation:
neither the specification nor a throwaway illustration. It is the worked
example an adopter reads to see each rule held by running code, and replaces
with their own. What an adopter **keeps** is what no domain choice shaped: the
building blocks under `src/BuildingBlocks/`, the gates under `.github/`, the
scaffold under `tools/new-service/`, and the operating contract in
`docs/change-locality.md`. What they **replace** is the six services, their
contracts and their saga; the gateway's routes and the BFF's projection go with
them, because both are written against those contracts. What they must
**bring** is what a reference implementation shows the seam for and cannot
supply, because it is nobody's legal advice: a tax model, a consent record, a
residency decision and, where a country demands one, a fiscal integration —
the four
[ADR-053](ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md) names
rather than builds. The seams they enter by already exist: ADR-053's rule that
a jurisdiction is configuration, each service's jurisdiction options
(`ShippingJurisdictionOptions`, `NotificationsJurisdictionOptions`), and its
rule that a deployment per jurisdiction is how residency is met. Both READMEs'
*Sample domain* rows say this; ADR-053's aside that the READMEs call the domain
illustrative is answered here rather than rewritten.
**Why.** The READMEs called the domain "illustrative only" while §4.1 named six
services concretely and all six were built, so two statements disagreed and no
record said which held. Two alternatives were declined. *The domain is the
specification* would make every domain choice a commitment — tax, consent and
residency among them — for a repository that runs no shop. *Extracting the
domain-free part as a template repository* would duplicate what
`tools/new-service/` already is, and a second repository would have to be kept
in step with this one by hand.
**Consequences.** A reader may not take a domain rule as the blueprint's
advice: that a shipment stops waiting on its carrier at an age
([ADR-054](ADR-054-a-shipment-stops-waiting-on-its-carrier-at-an-age.md)) is
the example's answer, and the rule it illustrates — a worker's wait ends at an
age the deployment states — is what carries over. A change to the domain is
still held to the chapters, because the example is how the chapters are
proved; a domain choice that would contradict a chapter is argued as one, not
waved through as only the sample. And the line between kept and replaced is
not a directory boundary everywhere: `deploy/` holds both — the Compose
baseline, the library chart and the gate scripts are kept, while each
deployable's unit, chart and canary file, and every alert over one service's
own meter, go with the service they deploy or watch. An adopter sorts those
file by file, and a kept file that starts to name a service has moved to the
replaced side.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

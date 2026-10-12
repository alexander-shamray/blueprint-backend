# ADR-105 — Appendix E has no per-service unit, so remove-service still edits no document

**Supersedes.** Nothing. It answers the question the last sentence of
[ADR-102](ADR-102-remove-service-lists-what-still-names-a-removed-service-and-edits-no-document.md)'s
Consequences left open, and the answer is that ADR-102 stands.
**Decision.** Appendix E has no per-service unit, and none is added.
`tools/remove-service` keeps ADR-102's behaviour: it opens no chapter, ADR or
appendix to edit it, Appendix E included, and lists what still names the
removed service. A fork edits Appendix E by hand, as ADR-102 says, or replaces
it wholesale, as
[ADR-096](ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md)'s
fourth clause says. The question comes back only as an ADR that gives
Appendix E a unit a tool can cut without leaving a link or a diagram false.
**Why.** ADR-102 waited for the chapter passes under #829 to fix Appendix E's
layout. They have, and the layout is one section per chapter pass, with a
subsection per topic. Of its 47 units (E.1, E.2.1 to E.2.8, E.3.1 to E.3.3,
E.4.1 to E.4.6, E.5.1 to E.5.6, E.6.1 to E.6.9, E.7.1 to E.7.8, E.8, and
E.9.1 to E.9.5), 34 name two or more services, so cutting "a service's
unit" would cut another service's facts with it. The other 13 name one
service as the case a rule is worked on (the gates of E.2.2 to E.2.5, the
fixture of E.7.2): the case, not the service, is the unit, and a fork that
removes the service replaces the case rather than deleting it. The chapters
link into the appendix 216 times, to 43 distinct anchors, so a deleted unit
leaves dead links in the very chapters that cite it. The shared diagrams of
E.1 draw every service in one block, and removing a node means removing
its edges, which is a rewrite of the picture and not a text cut. Each choice
the issue named is no better: a heading removes too much, a table row is only
E.1's, and a block under a marker needs a marker on every sentence that names
a service, which is the cost ADR-096's own marker already carries, doubled,
and a second gate to keep it covering the newest section.
**Consequences.** Removing a service still leaves Appendix E describing a
service that is gone, and the list is the only thing that says where. That is
the cost ADR-102 named, and it is now permanent rather than waiting on a
layout: an adopter either follows ADR-096's clause and replaces the appendix
wholesale, which is the intended route, or edits it by hand against the list.
`tools/remove-service` and its suite are unchanged, so the round-trip test
still compares a tree that never held an Appendix E edit. A future ADR may
supersede this one if Appendix E is ever restructured by service, and would
then owe the tool, the suite and the gate that keeps the structure covered.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

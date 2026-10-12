# ADR-096 — A chapter names a reference service only as evidence

**Decision.** Of the three options issue #829
set out, the blueprint takes the third: the domain stays in a chapter where it
is the evidence for a rule, and everything else moves to one example appendix.
Five parts, each testable.
(1) *The split.* A chapter names `Catalog`, `Ordering`, `Inventory`,
`Payments`, `Shipping`, `Notifications` or `Privacy` only in a block that is
evidence: a block whose rule cannot be stated or justified without the
concrete case, so that deleting the service would delete the argument (the
saga of [§9.6](../09-messaging.md), a money-movement command, Catalog as the
scaffold template `tools/new-service` reads, Privacy's fixed responder set).
An illustration, where the rule stands without the service, and a roster,
which enumerates the services, move to
Appendix E, `appendix-e-reference-domain.md`
beside the other appendices. It sits inside the blueprint tree and not under
`docs/example/` because `/check-links` and `/validate-blueprint` walk that tree
and would otherwise never see it. The chapter states the rule abstractly and
cites the Appendix E section by link. Where a code tree, a Compose file or
`classes.yml` already holds a roster, the chapter cites that owner and
Appendix E does not copy it. A word that is part of a mechanism that survives
a fork (a building block, a tool, a gate, a namespace pattern) is unchanged.
(2) *The marker.* An evidence block carries one form: it contains a link whose
target begins `appendix-e-reference-domain.md#`, which names the Appendix E
section holding the case's facts, and its prose says "in the reference
domain". The block is a paragraph or a table row; a code fence belongs to the
paragraph before it. This is the only statement of the form; the style guide
does not repeat it.
(3) *The owner.* Appendix E owns every domain fact the chapters no longer
state: the services and what each is, their contracts, queues, ports, realms,
Compose units and CI filters where no other file holds them, and the worked
examples. A chapter cites a section, never a value.
(4) *What a fork does.* It replaces Appendix E wholesale. An evidence block it
leaves is read as the reference implementation's case, which stays true of
the upstream platform; where a fork's domain contradicts one, this ADR makes it
history of the upstream case, the fork records its departure in an ADR of its
own, and the chapter is not edited. ADRs are inherited as history and never
edited ([ADR-062](ADR-062-the-domain-is-a-reference-implementation.md) already
says what is kept and replaced; this says what becomes of the
specification that describes the replaced part).
(5) *The gate.* A gate fails a block in a scanned file that names a reference
service without the marker. The service names are read from the directories
under `src/Services/`, not typed into the gate. The scanned set is every
`*.md` under `docs/backend-architecture/` except a closed list, named in the
gate: the ADRs and Appendices A to E. A file not on the list is scanned, so a
new chapter is covered by default, and the gate's test takes the scanned set
as its subject, not only the hits it finds. The gate is its own Class D issue.
**Why.** On `c7ef8e84` the chapters hold 744 whole-word mentions across 634
lines, and the ADRs 333 more. Of the 634 chapter lines, 213 are evidence, 243
are illustrations, 162 are rosters and 16 are mechanism names, so about two
thirds are not evidence. Under the one rule every fact has one owner, so
removing or renaming a service in a fork would edit those lines in a dozen
chapters, or leave the blueprint contradicting its own code. Option 1,
freezing the blueprint, splits a fork's specification in two. Option 2, moving
every mention, would turn the saga chapter into an abstraction with no case
to argue from, and the reader loses what shows the rule is true. The ADRs are
out of scope because they are superseded, never rewritten. Appendix C is the
closed delivery record, so it is frozen. Appendix B is licence identity,
which the licence gate makes load-bearing, and its role cells are left
alone. Appendix D is the type inventory and moves with the code.
**Consequences.** The pass is large: nine chapter issues and a skeleton, about
405 lines edited across fourteen chapters, in parallel PRs (an inventory
sized it; the chapters with the most illustration and roster lines are §15,
§4, §12, §13 and §14). Chapters lose concreteness where an example becomes a
link, and a reader must follow it to see the case. The 213 evidence lines
stay, so §9 and §6 still name the services at length: a fork has less to
edit, not nothing, and the marker keeps a stale case findable. Keeping the
marker costs every future edit: a new paragraph that names a service is
refused until it is evidence and says so, and "evidence" is a judgement the
gate cannot make, only the marker's presence. A paragraph with one marker
covers every name in it. `tools/remove-service` (issue #826)
takes Appendix E as its one documentation edit point: it removes the
service's section, then lists the marked blocks that still name it, which are
the fork's to supersede by ADR.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

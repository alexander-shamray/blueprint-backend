# ADR-102 — Remove-service lists what still names a removed service and edits no document

**Supersedes.** The last sentence of
[ADR-096](ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md)'s
Consequences, which gave `tools/remove-service` Appendix E as its one
documentation edit point.
**Decision.** `tools/remove-service` removes a service's directories, its
Compose unit and the edits `tools/new-service` made to the shared files its
README names, and then lists every file that still names the service. It opens
no chapter, ADR or appendix to edit it, Appendix E included. What the list
holds is the fork's to decide: a marked evidence block stays as the upstream
platform's case, and a fork that disagrees with one supersedes it by an ADR of
its own, as
[ADR-096](ADR-096-a-chapter-names-a-reference-service-only-as-evidence.md)'s
fourth clause already says.
**Why.** ADR-096 says in one clause that a fork replaces Appendix E wholesale
and in its last sentence that the tool removes the service's section from it.
Appendix E has no section per service: its sections are one per chapter pass,
so "the service's section" names a unit that does not exist, and the passes
that fill them (the chapter issues under #829) have not fixed what they will
hold. A tool that cut text out of a file whose layout is still being written
would either remove nothing or remove a section that also holds another
service's facts.
**Consequences.** A fork edits Appendix E by hand, or replaces it, after
the tool has run, and the list is the only thing that says where to look.
That is the cost: removing a service no longer leaves the appendix
consistent with the tree on its own. The tool can take on the edit once the
chapter passes have fixed Appendix E's layout, and that is a new ADR then,
because it would change what this one decides.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

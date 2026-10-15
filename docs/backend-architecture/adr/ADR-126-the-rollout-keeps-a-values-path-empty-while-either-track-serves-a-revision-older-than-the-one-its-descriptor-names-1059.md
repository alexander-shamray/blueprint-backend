# ADR-126 — The rollout keeps a values path empty while either track serves a revision older than the one its descriptor names (#1059)

**Decision.** **Where a value makes a release write rows its earlier
revisions misread, the rollout refuses that release while the value names
anything and either track serves a revision older than the one that reads
them.** The path and that revision are declared once, in the release's canary
descriptor, as `emptyUntil`. Before a rollout changes the cluster, `deploy.yml`
asks `canary.py empty-until` which paths the dispatched release keeps empty,
and for each one whose revision is not an ancestor of both the stable track's
and the tag's, holds the values the release will be installed with, read as
[ADR-125](ADR-125-a-value-two-releases-are-each-given-is-held-equal-before-either-rolls-1042.md)
reads them, to an empty value through `canary.py values-empty`. Today the one
path is `push.apps` on `notifications`, at the last commit of the pull request
that added push deliveries, which reached `main` with the rest of it: a worker
of an earlier revision claims every pending notice with no channel filter, so
it would claim a push notice and send it as mail.
`canary.py`'s check 15 holds the workflow to the comparison
(`deploy/canary/README.md`).

**Why.** A canary runs the candidate's workers beside the stable track's for
every rung, against one table, so a row the new revision writes is claimed by
whichever worker reaches it first. [§7.4](../07-persistence.md)'s
expand-and-contract keeps the schema readable by the version before, and says
nothing of a row that version reads wrongly. The rule had been a sentence in
[Appendix E](../appendix-e-reference-domain.md)'s `Push__Apps` row, which
nothing held: an operator naming an app in the release's values before
upgrading would have sent each event's mail twice and its push never. The
alternatives were rejected. A worker that writes push notices only once its
peers are known to be new needs a cluster-wide view the worker does not have.
A refusal read from the source tree of each track names a file the next
refactor moves, while a commit on `main` is never rewritten.

**Consequences.** **The first app is named in a second rollout, not the
first**: the release that adds push deliveries rolls with the list empty, and
the list is stated once that release is the stable track. A deployment that
already names an app empties it by hand in both releases before the upgrade,
since a rollout keeps the values it finds and ADR-125 refuses either one while
the two lists differ, and device registration pauses for that window. **The same
refusal holds a rollout back across the revision**: a tag older than it, with
an app named, is refused, so the release one rollback returns to also reads
push notices. It does not see rows already written: a list emptied by hand and
then a rollout to an older tag leaves pending push notices for that older
worker to claim. A revision named that is not a commit in the checkout fails
the step rather than passing it, and a path the values do not hold is refused
as misspelt rather than read as empty.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

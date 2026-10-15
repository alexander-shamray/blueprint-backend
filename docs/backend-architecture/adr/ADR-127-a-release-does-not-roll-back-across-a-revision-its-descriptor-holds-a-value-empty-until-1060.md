# ADR-127 — A release does not roll back across a revision its descriptor holds a value empty until (#1060)

**Decision.** This record amends
[ADR-126](ADR-126-the-rollout-keeps-a-values-path-empty-while-either-track-serves-a-revision-older-than-the-one-its-descriptor-names-1059.md)'s
consequence that its refusal does not see rows already written. **The
rollout refuses a tag older than a revision the release's `emptyUntil` names
while the stable track serves that revision or later, whatever the values
name.** The step that asks `canary.py empty-until` refuses that case before
it reads the values, and `canary.py`'s check 15 holds the workflow to the
refusal (`deploy/canary/README.md`). Today it means `notifications` does not
roll back across push deliveries.

**Why.** ADR-126 reads the values a release will be installed with, and the
rows a value made the release write outlive it. A list that named an app and
was emptied by hand leaves pending push notices in `NotificationLog`, and a
worker of an older revision claims each one as mail. The rollout reads no
table, so it cannot tell a release that never wrote such a row from one that
did. A refusal by revision holds for both. The alternative, counting pending
push notices before an older candidate is admitted, would have the rollout
read a service's database, which it does nowhere else.

**Consequences.** **Returning `notifications` to a revision older than push
deliveries is not a rollout.** It is an operator's change outside
`deploy.yml`, made once no push notice is pending.
[§15.5](../15-cicd-deployment.md)'s automatic rollback is unaffected: it
removes the canary and leaves the stable track where it was, so no older
worker starts. This record refuses nothing while the stable track is older
than the revision too, since neither track's workers write the rows in
question; ADR-126 still holds the value empty there.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

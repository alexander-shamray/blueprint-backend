# ADR-111 — A red object-store.yml run files a tracker issue, or comments on the open one (#988); narrows ADR-110's consequence that a red run files no issue

**Decision.** The `deployed` job of `.github/workflows/object-store.yml` has a
last step that runs when, and only when, the step asking the store failed: it
files an issue labelled `security` and `high`, or comments on the open one with
the run's URL, so the tracker holds one issue per drift and not one per day.
The job holds `issues: write` and the `check` job does not, the asking step
carries its own timeout shorter than the job's so a hung store is a failed step
and not a cancelled job, and `deploy/objectstore/test_store_check.py` asserts
each of those properties of the workflow's commands.
**Why.** [ADR-110](ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md)
left a red run to the repository's notification, which is read once and closes
with nothing open; the realm job it was modelled on files an issue for that
reason ([ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md)).
A store that lets an image credential delete is a protection that does not
protect, and it should stay open somewhere until somebody closes it.
**Consequences.** The step cannot tell a policy that drifted from a store it
could not reach, so it files both as `high`, and the issue's procedure asks
which it was. The title is the key the next run matches on, so editing an open
issue's title files a second one. What stays recorded in ADR-110 and open is
the silent schedule: GitHub sheds one under load, or suspends it after a stretch
without a commit, without a red run and so without an issue, and a monitor
outside GitHub is the deployment's to add.

**Supersedes in part ADR-110**: the Consequences' clause that a red run files no
issue.

**Also superseded in part by [ADR-114](ADR-114-the-object-store-check-sends-a-heartbeat-and-is-provisioned-by-a-script-995-996.md)**: the clauses on a store addressed only by a virtual-hosted name and on a monitor outside GitHub.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

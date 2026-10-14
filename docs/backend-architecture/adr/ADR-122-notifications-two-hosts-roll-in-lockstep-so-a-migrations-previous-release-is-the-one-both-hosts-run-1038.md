# ADR-122 — Notifications' two hosts roll in lockstep, so a migration's previous release is the one both hosts run (#1038)

**Decision.** This record amends
[ADR-118](ADR-118-notifications-gains-an-api-host-for-device-registration-and-preferences-and-the-push-adapters-stay-in-the-worker-474.md)'s
rollout order, and through it the paragraph of
[§15.3](../15-cicd-deployment.md) that states the order. **A host whose schema
another release's hook migrates rolls only at the revision that release's
stable track serves**: not ahead of it, which ADR-118 already refused, and
now not behind it either. **The migrating release rolls only while every such
host serves its stable revision**, neither older nor newer. A host with no
release yet runs nothing a migration could break, so it holds nothing back.
Its first install is by hand and outside the rollout, and is made at the tag
the migrating release's stable track serves; one made at any other revision
is refused at that release's next rollout. Today the one such pair is
`notifications-api` and `notifications`. `deploy.yml` refuses either tag
before the rollout changes the cluster, and `canary.py`'s check 13 holds the
workflow to both comparisons (`deploy/canary/README.md`).
**Why.** Two rules say what a migration must stay compatible with: §15.5's
"the previous release" and §7.4's "the currently running version". With one
host per schema they name one revision. Under ADR-118 they did not, because
the rollout bounded the API host from above only. `notifications-api` could
keep serving a revision several worker releases old. A contract step that
drops a column the worker's previous release no longer reads would then
satisfy §15.5 and still break that host. Lockstep makes the two rules name one
revision again: when the worker's hook runs, the worker's previous release is
the revision every host on the schema serves. So neither chapter rule has to
change, and neither does the multi-release discipline §7.4 prices. The
alternative was to rewrite both rules as "compatible with every running host
of the service" and record that as ADR-118's sixth departure. It was rejected
because that rule cannot be checked. With no lower bound, nothing says which
revisions are running, so a migration's author cannot know what the
migration must support.
**Consequences.** **Every worker release owes an API release before the next
worker release**, even when the API host's image did not change. A change
that touches only the worker still makes the API host roll, or the worker's
next tag is refused. So Notifications always deploys as two dispatches in a
fixed order, and the second can never be skipped. **The API host cannot roll
back on its own.** Its canary still keeps the stable track when a step fails,
but an API release that passes its canary and turns out bad is fixed forward,
or rolled back together with the worker, never alone; a worker rolled back
alone holds its own next release until the API host follows it. And the check
compares releases as they stand when a rollout starts. Two dispatches in
flight at once are serialised per workload and not across the pair, so each
reads the other's stable track. A ladder does not move that track until it
promotes, and no interleaving puts a host behind a migration that ran. A
third host on one schema would need the same descriptor field and nothing
more, because both comparisons read `migratedBy` rather than a name.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

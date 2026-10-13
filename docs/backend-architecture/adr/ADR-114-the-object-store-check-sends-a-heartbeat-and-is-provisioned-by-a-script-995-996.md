# ADR-114 — The object store check sends a heartbeat on a green run and is provisioned by a script, and can name a virtual-hosted bucket (#995, #996)

**Decision.** The `deployed` job of `.github/workflows/object-store.yml` ends,
after a green check, with a step that requests the URL in the Environment secret
`OBJECT_STORE_HEARTBEAT_URL` (`store_check.py heartbeat`). It carries no `if:`,
so a red or cancelled run sends none. A monitor outside GitHub, which the
deployment runs, alerts when the URL has not been requested for longer than its
grace period. `deploy/objectstore/provision.py` names what the `production`
Environment still lacks (`check`) and sets it from the caller's environment
(`apply`): the Environment must already exist and admit `main` alone, nothing is
set unless every value is present, secrets travel on stdin, and the opt-in
variable `OBJECT_STORE_CHECK_SCHEDULED` is set last. An optional Environment
variable `OBJECT_STORE_ADDRESSING`, `path` unless set to `virtual`, makes the
check name the bucket as a label of the host for a store reachable no other way.
**Why.** [ADR-110](ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md) and [ADR-111](ADR-111-a-red-object-storeyml-run-files-a-tracker-issue-or-comments-on-the-open-one-988-narrows-adr-110s-consequence-that-a-red-run-files-no-issue.md) left two things open.
GitHub sheds a schedule under load and suspends it after a stretch without a
commit, in both cases without a red run, so nothing in the repository could
notice the daily check had stopped
([#995](https://github.com/alexander-shamray/blueprint-backend/issues/995)): a
workflow cannot report its own absence, and a second workflow on the same
schedule is suspended with it. A beat on success inverts the question, so the
absence is what the monitor reads. And until an operator created the
Environment's values the job was skipped on every run, so the issue-filing step
could never fire
([#996](https://github.com/alexander-shamray/blueprint-backend/issues/996)). The
script refuses an Environment open to other branches because the job sends the
credential pairs to the endpoint (docs/secrets.md), and sets the opt-in last so
a half-provisioned job never runs.
**Consequences.** The repository now holds the beat and the provisioning, and
the deployment still owes the monitor: nobody can create it from here, and until
the secret is set the job is red at its last step, with no issue filed, because
only the judgement filing one is deliberate. The URL's path is a bearer token,
so it is a secret, is never in a message and is requested over `https` or on a
loopback host alone. A red check is a missed beat as well as an issue, which is
accepted: the monitor is the second signal and not the first. `realm.yml` has
the same residual and is left as it is. The check still cannot tell a drifted
policy from an unreachable store, and it does not see a CDN or an ACL that
grants a delete around the policy.

**Supersedes in part [ADR-110](ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md) and [ADR-111](ADR-111-a-red-object-storeyml-run-files-a-tracker-issue-or-comments-on-the-open-one-988-narrows-adr-110s-consequence-that-a-red-run-files-no-issue.md)**: their
Consequences' clauses that a store reachable only by a virtual-hosted name is
not covered, and that a monitor outside GitHub is the deployment's to add with
nothing in the repository to feed it.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

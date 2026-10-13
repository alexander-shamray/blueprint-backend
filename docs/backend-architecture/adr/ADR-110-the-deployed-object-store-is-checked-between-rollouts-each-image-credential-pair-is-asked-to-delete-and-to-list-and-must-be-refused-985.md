# ADR-110 — The deployed object store is checked between rollouts: each image credential pair is asked to delete and to list, and must be refused (#985)

**Decision.** `.github/workflows/object-store.yml` runs
`deploy/objectstore/store_check.py` daily and on `workflow_dispatch`, under the
`production` GitHub Environment, and its suite on every change to that tree.
With each of the two credential pairs of [§10.8](../10-api-gateway.md) the check
signs three requests against the deployed store: a delete of a key nothing holds
under `uploads/`, the same under `products/`, and a list of the bucket. A
request passes only when the store answers `403` with the code `AccessDenied`;
any other status, a `403` carrying another code, an unreachable store and an
unset variable all fail the run. The job is opted in by the repository variable
`OBJECT_STORE_CHECK_SCHEDULED`, set to `enabled` by whoever provisions the
Environment, and refuses a dispatch from any ref but `main`.
**Why.** [§10.8](../10-api-gateway.md) states that neither pair can list a
bucket or delete, and the local store proves it
([#979](https://github.com/alexander-shamray/blueprint-backend/issues/979)). A
deployed store is run outside this repository
([ADR-064](ADR-064-every-store-is-run-outside-this-repository-to-a-stated-recovery-point.md)),
so a policy edited there, or a service that began to delete, passed every check
here and failed only against the real store
([#985](https://github.com/alexander-shamray/blueprint-backend/issues/985)). The
delete names an absent key because a store decides authorisation before
existence: a refused delete answers `403` whether or not the key is there, and
an allowed one removes nothing. The check therefore never writes to a production
bucket, where an object under `products/` would be served publicly and never
expire. The code is required because an unknown access key or a wrong secret
answers `403` as well, with `InvalidAccessKeyId` or `SignatureDoesNotMatch`, and
counting that as a refusal would pass a rotated secret as a policy that holds.
The suite holds a store that allows the delete red, which is the local store as
it stood before #979.
**Consequences.** The `production` Environment holds a second copy of both
service credential pairs, which docs/secrets.md states and rotates with them.
They are the narrowest the platform issues, and the check is the only consumer
outside the two hosts, but a leaked Environment secret now carries `uploads/`
writes. The endpoint is pinned from an Environment variable and never read from
a release, because the pairs are sent to it. The check addresses the bucket by
path, so a store reachable only by virtual-hosted name is not covered, and it
speaks to S3 policy alone: a CDN or a bucket ACL that grants a delete around the
policy is the deployment's to examine. A schedule is best effort, so a dropped
run is silent as
[ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md) records for
the realm, and unlike that job a red run files no issue: the run is red and the
repository's notification is the signal. The check does not prove the policy
allows what the services need; the deployed upload path does.

**Superseded in part by
[ADR-111](ADR-111-a-red-object-storeyml-run-files-a-tracker-issue-or-comments-on-the-open-one-988-narrows-adr-110s-consequence-that-a-red-run-files-no-issue.md)**: the Consequences'
clause that a red run files no issue.

**Also superseded in part by [ADR-114](ADR-114-the-object-store-check-sends-a-heartbeat-and-is-provisioned-by-a-script-995-996.md)**: the clauses on a store addressed only by a virtual-hosted name and on a monitor outside GitHub.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

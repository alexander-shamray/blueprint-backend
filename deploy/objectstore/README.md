# The object store check

**What §10.8's permissions are asked of a deployed store, and what this tree
deliberately does not check.**

[ADR-110](../../docs/backend-architecture/adr/ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md)
is the decision. [§10.8](../../docs/backend-architecture/10-api-gateway.md)
states that neither of the two image credential pairs can list a bucket or
delete; Compose's store is held to it by `ProductImageStoreTests`, and this is
the same question put to a store this repository does not run.

```bash
py -3.12 -m unittest discover -s deploy/objectstore
py -3.12 deploy/objectstore/store_check.py check     # needs the variables below
```

## What it asserts

With each pair, the store answers `403` with the code `AccessDenied` to:

- a delete of a key nothing holds under `uploads/`;
- the same under `products/`;
- a list of the bucket.

A delete names an absent key so that a store which wrongly allows it removes
nothing: authorisation is decided before existence. The check writes nothing.

**Anything else fails the run**, and the reasons are the suite's cases: a `2xx`
is a permission that exists; a `403` with another code (`InvalidAccessKeyId`,
`SignatureDoesNotMatch`) is a pair the store does not know, which would read as
a refusal and pass a rotated secret; a `404` is not a refusal, and
`NoSuchBucket` names no bucket the store holds; an unreachable store and an
unset variable are not answers.

## What it needs

| | |
|---|---|
| Variables | `OBJECT_STORE_ENDPOINT`, `OBJECT_STORE_BUCKET`, `OBJECT_STORE_REGION`; `OBJECT_STORE_ADDRESSING` (`virtual` for a store reachable only by a virtual-hosted name, `path` when unset) |
| Secrets | `OBJECT_STORE_BFF_ACCESS_KEY`, `OBJECT_STORE_BFF_SECRET_KEY`, `OBJECT_STORE_CATALOG_ACCESS_KEY`, `OBJECT_STORE_CATALOG_SECRET_KEY`, `OBJECT_STORE_HEARTBEAT_URL` |
| Repository variable | `OBJECT_STORE_CHECK_SCHEDULED`, `enabled` |

Where they live, why the endpoint is pinned and how to rotate them are
[`docs/secrets.md`](../../docs/secrets.md)'s.

## Provisioning it

The `production` Environment must already exist and admit `main` alone. Put
every variable and secret above, bar the optional addressing one, in your
shell's environment; `apply` sets the repository variable itself, last. Then:

```bash
py -3.12 deploy/objectstore/provision.py check    # what is still missing
py -3.12 deploy/objectstore/provision.py apply    # sets it, the opt-in last
```

Create the monitor first and use its ping URL as `OBJECT_STORE_HEARTBEAT_URL`:
any service that alerts when a URL has not been requested for longer than a
grace period will do, with a period a little over a day.

## What it does not check

- **That the policy allows what the services need.** A deployed upload does.
- **That the bucket is the one the pairs are scoped to.** A bucket that exists
  and grants the pairs nothing refuses all six probes with `403 AccessDenied`,
  as the right bucket's policy does, so a wrong `OBJECT_STORE_BUCKET` reads as
  a pass. Only a request a pair must be allowed would tell the two apart. The
  BFF pair's only allowed request is a write under `uploads/`, and the Catalog
  pair's read of an absent key is refused on both buckets, since a store
  answers a pair without list permission `403 AccessDenied`. Asking for the
  write would make the check write, which
  [ADR-110](../../docs/backend-architecture/adr/ADR-110-the-deployed-object-store-is-checked-between-rollouts-each-image-credential-pair-is-asked-to-delete-and-to-list-and-must-be-refused-985.md)
  decided it does not.
- **Anything outside the store's own policy**, such as a CDN or a bucket ACL
  that grants a delete around it.
- **That the monitor exists.** A green run requests the heartbeat URL, so a
  schedule GitHub shed or suspended is heard as silence, but only by a
  monitor the deployment created ([ADR-114](../../docs/backend-architecture/adr/ADR-114-the-object-store-check-sends-a-heartbeat-and-is-provisioned-by-a-script-995-996.md)).

## What a red run means

A red run of the deployed check files an issue labelled `security` and
`high`, or comments on the open one ([ADR-111](../../docs/backend-architecture/adr/ADR-111-a-red-object-storeyml-run-files-a-tracker-issue-or-comments-on-the-open-one-988-narrows-adr-110s-consequence-that-a-red-run-files-no-issue.md)).
The run log says which of two things it was, per credential pair:

- **The store allowed a delete or a list**, or answered with a code other than
  `AccessDenied`. The policy has drifted from §10.8: correct it, then dispatch
  the workflow and close the issue on the green run.
- **The store could not be asked**: an unreachable endpoint, an unset variable
  or a pair the store does not know. Nothing is established either way, so
  treat it as the first until the run is green.

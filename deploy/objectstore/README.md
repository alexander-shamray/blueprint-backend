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
a refusal and pass a rotated secret; a `404` is a bucket that is not the one
named; an unreachable store and an unset variable are not answers.

## What it needs

| | |
|---|---|
| Variables | `OBJECT_STORE_ENDPOINT`, `OBJECT_STORE_BUCKET`, `OBJECT_STORE_REGION` |
| Secrets | `OBJECT_STORE_BFF_ACCESS_KEY`, `OBJECT_STORE_BFF_SECRET_KEY`, `OBJECT_STORE_CATALOG_ACCESS_KEY`, `OBJECT_STORE_CATALOG_SECRET_KEY` |

Where they live, why the endpoint is pinned and how to rotate them are
[`docs/secrets.md`](../../docs/secrets.md)'s.

## What it does not check

- **That the policy allows what the services need.** A deployed upload does.
- **A store addressed only by virtual-hosted name.** The request is signed for
  a path-style URL.
- **Anything outside the store's own policy**, such as a CDN or a bucket ACL
  that grants a delete around it.
- **That the schedule ran.** GitHub sheds a schedule under load, or suspends
  it after a stretch without a commit, without a red run and so without an
  issue; a monitor outside GitHub asking whether `object-store.yml` ran
  recently is the deployment's to add.

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

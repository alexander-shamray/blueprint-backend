# ADR-106 — An S3-compatible object store for product images; the BFF issues a pre-signed upload URL (#472)

**Supersedes.** The thumbnail half of
[ADR-085](ADR-085-a-published-products-name-is-visible-text-and-its-thumbnail-is-https-from-a-host-the-seller-chooses.md):
that `PublishProductValidator` refuses a thumbnail URL that is not absolute
`https` and leaves the host to the seller. The request no longer carries a URL,
so there is none to refuse; the `https` rule is about the configured base
([§10.8](../10-api-gateway.md)). Its name half stands.
**Decision.** Product images live in an object store spoken to through the S3
API, and no service handles the bytes of an upload. `Web.Bff` signs a
short-lived `PUT` URL for one key it chose, to an authenticated seller; the
client uploads straight to the store; `Catalog` attaches the image when the
product is published by copying that object inside the store and rebuilding the
public URL from the key, never from a string the client sent. The endpoint's
rules are [§10.8](../10-api-gateway.md); the store is infrastructure the
deployer supplies, and the PR that implements this adds a pinned local one to
Compose ([§14.1](../14-local-development.md)). It joins
[ADR-064](ADR-064-every-store-is-run-outside-this-repository-to-a-stated-recovery-point.md)'s
list as a store of record for `products/`, where a restore must bring back
every published image and the deployment states a recovery point and a recovery
time; `uploads/` is never restored, because it expires.
**Why.** `ThumbnailUrl` is a URL the seller must already have, so a phone's
camera cannot be a source and the seller must host the image elsewhere first.
The upload belongs at the edge a client already talks to, and it must not
route the bytes through a service: a service holding a 5 MiB body per request
is a memory and thread cost on the host that publishes products, and the
platform has no other reason to hold a file. The S3 API is the portable
choice, because every managed store and every self-hosted one speaks it, so
the application depends on a protocol and a chart value rather than on a
vendor. Signing is a local computation, so the BFF takes no runtime dependency
on the store to hand out a URL.
**Consequences.** This is the platform's first object store, and with it come
an operational surface (a credential pair per role, a bucket, a lifecycle
rule, a public host) that nothing else here has. An upload that is never
attached sits in `uploads/` until the lifecycle rule expires it, and a copy
that succeeds before the publish transaction fails leaves an object in
`products/` that no row names; the rule bounds the first and nothing bounds
the second but a retry reusing the same destination key, which is why the
destination key is derived from the source key. A pre-signed `PUT` can pin the
size and content type the client declared but cannot make the store inspect
the bytes, so `Catalog` reads the first bytes of the object and refuses one
whose signature disagrees with its declared type; that is a small read by a
service, and the one place this decision's "no service handles the bytes"
yields to a header check. The public image is served by the store or a CDN in
front of it and not by the gateway, so the gateway's rate limit and
correlation do not see image reads, and the `nosniff` header of
[§10.6](../10-api-gateway.md) is the store's or the CDN's to set, which this
repository can state and cannot enforce.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

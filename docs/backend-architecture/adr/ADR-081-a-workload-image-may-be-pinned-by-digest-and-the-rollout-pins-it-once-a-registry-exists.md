# ADR-081 — A workload image may be pinned by digest, and the rollout pins it once a registry exists

**Decision.** Every chart takes `image.digest` and `image.migratorDigest`, each
optional. When one is set, the Deployment's or the migration Job's image
reference becomes `<registry>/<repository>:<tag>@<digest>`, and a value that is
not `sha256:` and 64 lowercase hex characters fails the render
(`commerce.digest`). The tag stays required, because it names the version label
and the migration Job. The rollout passes no digest until a registry exists.
From then on it passes the digest
[ADR-071](ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md)'s
`cosign verify` checked, and that step belongs to #438.
**Why.** A tag is mutable. Anyone who can push to the registry can overwrite
`<repository>:<sha>`, and every node that pulls afterwards runs the new bytes:
a rescheduled pod, a new node, the next migration Job holding the DDL
credential. `app.kubernetes.io/version` still names the tag. ADR-071's
signature check reads the tag's digest once, at rollout, so it does not cover a
pull that happens later. Pinning the digest the check verified binds every
later pull to those bytes. The chart can carry the reference form now, and the
value that fills it waits on the registry ADR-071 waits on.
**Consequences.** Until a registry exists, every image is still named by its
tag alone and an overwritten tag still runs; the step that closes that in
practice is #438's. `deploy/helm/smoke.sh` asserts
both forms: a tag alone when no digest is given, and the pinned form, for both
images, when one is. A digest set without the rollout's help pins whatever an
operator typed, and keeping it in step with the tag is theirs to do.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

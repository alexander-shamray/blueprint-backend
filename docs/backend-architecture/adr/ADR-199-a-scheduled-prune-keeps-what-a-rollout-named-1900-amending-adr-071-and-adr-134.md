# ADR-199 — A scheduled prune keeps what a rollout named (#1900), amending ADR-071 and ADR-134

**Decision.** `.github/workflows/ghcr-prune.yml` runs
`tools/ci-cost/ghcr_prune.py`, which deletes a container version of this
repository's packages unless it is kept for itself or a kept version needs it.
A version is kept for itself when it was touched within `KEEP_DAYS`, is among
the `KEEP_NEWEST` newest images of its package, carries a commit a dispatch of
`deploy.yml` named, or carries a tag that is neither a commit nor a referrers
tag, `main` among them. A kept version that carries a tag needs its index's
children and each one's signature and attestations, read from the registry. A
run deletes at most `MAX_DELETES`, images before what they name and oldest
first, and a package with a manifest it cannot read loses nothing and fails the
run. A dispatch whose title it cannot read stops every package, so a dispatch of
a `deploy.yml` without that title is cleared by hand: its image is given a tag
of its own and the run is deleted.
**Nothing deletes until it is opted in**: a schedule runs only when the
repository variable `GHCR_PRUNE_SCHEDULED` is `enabled`, and a dispatch reports
unless its `delete` input is set. `ci.yml`'s artefacts and SBOMs are kept for
`ARTIFACT_RETENTION_DAYS` rather than the repository's default.
**Why.** [`docs/ci-cost-comparison.md`](../../ci-cost-comparison.md) read 14,170
versions in 18 packages after under a month, a listing of one package on
2026-11-09 showed nine for each build, and #1900 found nothing that removes one.
An old-looking untagged version is not safely garbage: the platform manifest
under an index, the attestation manifest beside it and the signature bundles the
rollout verifies
([ADR-109](ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md))
are all untagged versions, and deleting one breaks a release that pins that
digest ([ADR-081](ADR-081-a-workload-image-may-be-pinned-by-digest-and-the-rollout-pins-it-once-a-registry-exists.md)),
so only a walk of the manifests can tell what is spare. What a rollout rolled
is read from `deploy.yml`'s run title, since the API keeps no dispatch's inputs
and the prune holds no cluster credential. The rollout takes only a full commit
tag, so that title names the commit whose image it verified. The schedule's
guard is [ADR-043](ADR-043-the-deployed-realm-is-checked-between-rollouts.md)'s
form, and a dispatch needs its own input instead, because a deletion is the one
step here no later run can undo and the first report is what a person reads
before allowing it. A tagged version goes before what it names, and what a tag
that would not go still names stays, so that neither a capped run nor a refused
delete leaves a tag that no longer pulls.
**Consequences.** The prune knows tags and a release pins a digest. A rolled tag
rebuilt with other bytes protects only its new digest, so the digest a release
pinned goes after the window, and a pod rescheduled on it fails to pull. So does
a release deployed by anything but `deploy.yml`, and a rollout older than the
runs GitHub retains, about 400 days. A commit an operator has not yet rolled can
still be named after the window, and the rollout then fails at its signature
check: the window is a bet that nobody rolls a commit that old. An adopter's
commit tag or digest pinned by hand goes the same way, where `:main` does not. A
successful SBOM older than the retention is readable only from its attestation,
and a pull request's, which has none, is simply gone. Whether a job's
`GITHUB_TOKEN` may delete a version rests on the package's access settings and
was not exercised when this was written: the first run with `delete` set is that
check, and a refusal fails it and deletes nothing. A deleted version can be
restored only within GitHub's own limit for that.

**Amends**
[ADR-071](ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md),
whose artefact now lives for `ARTIFACT_RETENTION_DAYS`, and
[ADR-134](ADR-134-each-image-has-a-main-tag-that-names-its-newest-signed-build-and-compose-names-the-image-beside-its-build-1111-adding-to-adr-109.md),
whose `<sha>` set and pinned digest hold for a consumer only while this rule
keeps them, once the prune is opted in.

**Amended by
[ADR-200](ADR-200-the-prune-takes-its-packages-from-the-image-matrix-1915-amending-adr-199.md)**,
which takes the packages from the image matrix.

**Amended by
[ADR-201](ADR-201-the-rollout-tags-the-digest-it-verified-so-the-prune-keeps-what-a-release-pins-1915-amending-adr-109-and-adr-199.md)**,
whose rollout tags the digest it verified, so a rebuilt commit tag no longer
leaves the digest of a release `deploy.yml` makes from then on to the prune.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

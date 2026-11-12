# ADR-201 — The rollout tags the digest it verified, so the prune keeps what a release pins (#1915), amending ADR-109 and ADR-199

**Decision.** In the step of `.github/workflows/deploy.yml`'s rollout that
verifies its images, before any Helm command, each digest `cosign verify`
reported is tagged `rollout-<run id>-<run attempt>` in its own package by
`docker buildx imagetools create`, and the new tag is verified again: a tag that
names any other digest stops the rollout. The job's `permissions:` block in
`deploy.yml` grants `packages: write` for it, where it held `packages: read` for
the pull
[ADR-109](ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md)
verifies. The tag is neither a commit nor a referrers tag, so
`tools/ci-cost/ghcr_prune.py` keeps that version and everything it needs by the
rule
[ADR-199](ADR-199-a-scheduled-prune-keeps-what-a-rollout-named-1900-amending-adr-071-and-adr-134.md)
already has for such a tag, and `pipeline-gate images` fails a verify step that
records no tag.
**Why.** The prune knows tags and a release pins a digest, so a commit tag
rebuilt with other bytes by a dispatch of `ci.yml` protected only its new
digest, and the one a release pinned went after `KEEP_DAYS`. The record has to
be where the prune reads without a cluster credential, and written after the
verify: the run title is written before it, and an artefact, a job summary or
an annotation expires. A tag lives in the registry the prune already walks, and
a tagged version already keeps its children, signature and attestations. It is
written before Helm, so no release pins a digest the record missed; a separate
job holding the grant would run after the release and lose the record with a
run that died. It is per run rather than per commit, because a second rollout
of a rebuilt commit would move a per-commit tag off the first release's digest.
**Consequences.** The rollout holds a write grant for its whole run, beside
`helm` and `kubectl`, where it held none. It runs only on a dispatch from `main`
under the `production` Environment, so
[ADR-197](ADR-197-write-grants-leave-the-jobs-that-run-a-pull-requests-code-1789-amending-adr-109-and-adr-149.md)'s
gate holds, but whoever may dispatch it may push a tag; that tag cannot make
other bytes roll, since the rollout pins the digest signed by `ci.yml` on
`main`. Every rollout keeps its digests for good, one image's family per
rollout and package, and retiring one is deleting its tag by hand. Rollouts made
before this, and a release deployed by anything but `deploy.yml`, keep only
ADR-199's protection, so its title read stays. The copy and the grant are
unexercised when this is written: the step comes before the first `kubectl`, so
the first dispatch, which still fails for want of a cluster, is that check.

**Amends**
[ADR-109](ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md),
whose rollout now tags each image it verified, and
[ADR-199](ADR-199-a-scheduled-prune-keeps-what-a-rollout-named-1900-amending-adr-071-and-adr-134.md),
whose rebuilt-tag consequence holds now only for the releases named above.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

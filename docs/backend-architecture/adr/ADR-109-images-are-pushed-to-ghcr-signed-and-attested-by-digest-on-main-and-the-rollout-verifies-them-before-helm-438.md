# ADR-109 — Images are pushed to GHCR, signed and attested by digest on main, and the rollout verifies them before Helm (#438)

**Decision.** The registry is GHCR, namespace
`ghcr.io/alexander-shamray/blueprint-backend`, and its address has one owner:
the workflow-level `REGISTRY` in `ci.yml`. `deploy.yml` declares the same value
because a workflow cannot read another's `env`, and `pipeline-gate images` fails
the build where the two differ or where either workflow spells the address
anywhere else, so moving registries is one value, the host in each workflow's
login step and one ADR.
The value must be lower case, because a registry refuses an upper-case name,
and it is only as lower case as `github.repository`, which nothing here
lower-cases. On a push to
`main`, or a `workflow_dispatch` run on `main`, each leg of the `images` job
that built an image pushes it as `$REGISTRY/<image>:<sha>`, reads the pushed
digest back, and by that digest signs it keyless with cosign, attests SLSA
provenance (`actions/attest-build-provenance`) and attests the Syft SBOM
(`actions/attest-sbom`), both pushed to the registry. The SBOM attested is the
file the artefact keeps. A pull request builds and takes the SBOM and nothing
else. `deploy.yml`'s rollout, after the tag is validated and before any Helm
command, verifies the workload's API image and, where its chart has one, its
migrator: `cosign verify` of the tag against the identity `ci.yml` on
`refs/heads/main` and GitHub's OIDC issuer, then `gh attestation verify` of the
provenance for that digest, with `--signer-workflow` `ci.yml` and `--source-ref`
`refs/heads/main`. The digest handed
on is the one `cosign verify` reports, read by `canary.py verified-digest`,
which refuses anything but `sha256:` and 64 lowercase hex characters, and every
`helm upgrade` passes it as `--set-string image.digest`, with
`image.migratorDigest` and `image.registry`. The image names come from each
chart's `image.api` and `image.migrator`, read by `canary.py images`. The charts'
default `image.registry` stays a placeholder that the rollout overrides.
**Why.** GHCR needs no account beyond the repository and takes `GITHUB_TOKEN`.
Keyless signing leaves no key to hold, and its identity, this workflow on
`main`, is the question the verifier asks
([ADR-071](ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md)).
GHCR has no tag-immutability setting, so a tag can be overwritten, which
[ADR-081](ADR-081-a-workload-image-may-be-pinned-by-digest-and-the-rollout-pins-it-once-a-registry-exists.md)
already answered: the rollout verifies a digest and Helm pulls that digest, so
an overwritten tag cannot run other bytes, and a later pull of the same release
cannot either. Signing and attesting by digest, never by tag, is what makes the
signature bind the bytes. The job holds `packages: write`, `id-token: write` and
`attestations: write` on a pull request run from the same repository, and that
is harmless: every step that spends one carries the main-push condition, and an
OIDC token minted for a `refs/pull/...` ref cannot satisfy the verifier, which
pins `refs/heads/main`. `gh attestation verify` takes the provenance because it
checks the subject digest and the signing workflow in one call. The `images` job
is path-filtered, so a service no push has touched since this landed has no
signed image to verify, and a change to `ci.yml` does not select it: a dispatch
of `ci.yml` makes every filter true and signs every image once. Each commit on
`main` has a concurrency group of its own, so no run is cancelled or replaced
while pending, because one lost between the push and the signature leaves an
image the verifier then refuses.
**Consequences.** Nothing here can be exercised before the first push to `main`,
so the first dispatch is where a wrong action input shows, or a `cosign verify`
output the digest reader cannot parse, which refuses the rollout rather than
passing it. Keyless signing needs
Sigstore's public service up at build time and writes each signature to a public
log that names the repository and the workflow. The check guards the deploys
made through the rollout, and a `helm upgrade` run by hand is not refused; a
cluster policy is the stronger seat once a cluster exists. A package pulled by a
future cluster needs an `imagePullSecret` if it is private. A burst of merges
builds and signs in parallel, one run each. **Revisit trigger:** if the
repository goes private, signing is re-decided, because the Rekor entry would name a private
repository in a public log and GitHub's attestations on private repositories
need Enterprise Cloud; the registry need not change.

**Amends**
[ADR-071](ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md)
and
[ADR-081](ADR-081-a-workload-image-may-be-pinned-by-digest-and-the-rollout-pins-it-once-a-registry-exists.md):
the registry they waited on exists, and the push, the signature, the two
attestations and the admission check are the steps they described.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

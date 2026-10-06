# ADR-071 — Every image carries an SBOM, and signing waits on a registry

**Decision.** Every image the `images` job in `ci.yml` builds gets an SBOM,
made by Syft (`anchore/sbom-action`, pinned to a commit) from the built image
as SPDX JSON and kept as a workflow artefact, and `pipeline-gate images`
refuses a build step with none after it. Signing and provenance arrive with the first push to a
registry, in the job that pushes: keyless with cosign, the trust root
Sigstore's public-good Fulcio and Rekor, the identity `ci.yml` on
`refs/heads/main` through GitHub's OIDC token, with the SBOM and a SLSA
provenance attached to the pushed digest as attestations. The admission check
is a `cosign verify` of that identity in `deploy.yml`'s rollout, before its
first Helm call.
**Why.** An SBOM needs neither a registry nor a key, so it runs now. A
signature over an image nothing pushes binds to a digest no deployment pulls,
so signing before a registry exists would be a step faked for the diagram.
Keyless signing leaves this repository no private key to hold or rotate, and
its identity is the workflow and ref that built the image, which is the
question a verifier asks. A chart cannot refuse an image and no cluster exists
to enforce a policy, so the check sits where the rollout already reads the
tag; a cluster policy is the stronger seat once a cluster exists, and this
does not exclude one.
**Consequences.** Until a registry exists no image is signed and nothing is
verified, so [§15.1](../15-cicd-deployment.md)'s "Build + sign images" node is
half true and #438 stays open on that precondition. The SBOM describes the
image as built on the runner rather than as pushed, and lives only as long as
the artefact's retention. Keyless signing needs Sigstore's public service up at
build time and writes each signature to a public transparency log that names
the repository and the workflow. A check in the rollout job guards the deploys
made through it, and a `helm upgrade` run by hand is not refused.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

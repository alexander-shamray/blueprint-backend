# ADR-134 — Each image has a `main` tag that names its newest signed build, and Compose names the image beside its build (#1111), adding to ADR-109

**Decision.** Each leg of the `images` job that builds and signs an image on
`main` moves the tag `main` of that image to the digest it just signed, in a
step of its own after both attestations, and reads the tag back to confirm it
names that digest. A leg that built nothing does not touch the tag, so it keeps
naming the newest build of that image, whichever commit made it. The image
set at `main` is then `$REGISTRY/<image>:main` for every entry of the matrix,
and a dispatch of `ci.yml` is what gives an image no push has touched its first
`main` tag. Each application service in `deploy/compose/services/` names its
image beside its `build:` as
`${BLUEPRINT_REGISTRY:-blueprint.local}/<image>:${BLUEPRINT_TAG:-local}`,
`<image>` being the matrix's name for that Dockerfile. The default registry
is `blueprint.local`, which resolves nowhere, so a `pull` with no registry
set fails rather than pulling a same-named image from a public namespace. A
local `up` still builds, and tags what it builds with that name. A consumer
sets
`BLUEPRINT_REGISTRY=ghcr.io/alexander-shamray/blueprint-backend` and
`BLUEPRINT_TAG=main`, then runs `docker compose pull`, `docker compose build
rabbitmq` and `up --no-build`.
`pipeline-gate images` fails a leg that has no push of the `main` tag, one
that does not run under the main-push condition, and one that runs before the
provenance or the SBOM attestation. The `compose.yml` workflow keeps building
from source. The packages are public: the 18 images were read with no token on
2026-10-17, and a consumer needs none.
**Why.** A push to `main` builds only the images its paths select, so `<sha>`
tags are a scatter: an arbitrary commit has tags for the images it touched and
none for the rest, and nothing names a full set. A client's CI therefore built
all 19 `build:` stanzas on every run, seven minutes or more against the half
minute of the suite it served. A moving tag fixes that without a rebuild of
the unchanged images, and it keeps ADR-109's rule whole: the tag is a second
name for a digest that was signed and attested by digest, and the rollout still
verifies and pins by digest, so an overwritten or out-of-date tag cannot run
other bytes. The alternative, building every leg on every push to `main`, makes
every commit pay for every image and gives each a new digest for unchanged
source, which is the cost the filters exist to avoid. The `compose.yml`
workflow stays cold because it tests the pull request's sources: pulling
`main` there would run the images the change is trying to replace.
**Consequences.** The tag is moved by the run that finishes last, and a burst
of merges runs in parallel (ADR-109), so two builds of one image can finish out
of order and leave `main` on the earlier one until the next build of that image;
the `<sha>` tag stays exact, and a consumer that needs one commit's set reads
those. The tag moves after the attestations, so a run that fails between them
leaves it on the previous build, which is the safe side. A consumer's
`BLUEPRINT_TAG=main` is a moving target by design, so a pinned set is a
digest. `rabbitmq` is built from `deploy/compose/rabbitmq/Dockerfile` and is
not in the matrix, so a consumer still builds that one image (a base image and
one plugin). Until the first dispatch after this lands, no image has a `main`
tag, and the consumer's pull fails closed rather than finding a stale set.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

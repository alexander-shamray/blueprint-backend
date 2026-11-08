# ADR-197 — Write grants leave the jobs that run a pull request's code (#1789), amending ADR-109 and ADR-149

**Decision.** No job a pull request reaches holds a write grant beside a
checkout of its branch, and `pipeline-gate grants` refuses one in any workflow.
In `ci.yml`, `images` builds and inventories on every run but `main`'s and holds
no grant; `publish-images` runs on a push or a dispatch on `main`, takes the
same matrix by YAML alias, runs the same `docker build` line, which
`pipeline-gate images` holds to the first, and of the two alone holds
`packages`, `id-token` and `attestations`. Where an older record, among them
[ADR-109](ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md)
and
[ADR-134](ADR-134-each-image-has-a-main-tag-that-names-its-newest-signed-build-and-compose-names-the-image-beside-its-build-1111-adding-to-adr-109.md),
gives the `images` job a build on `main`, a push, a signature or a tag, read
`publish-images`. `packages` packs on every run and holds no grant;
`publish-packages` runs on a push to `main` that changed a package, takes what
`packages` packed as an artefact and pushes it, so the grant
[ADR-149](ADR-149-the-ai-assistants-core-and-its-adapter-are-two-building-blocks-this-repository-publishes-as-nuget-packages-1346.md)
gave `packages` runs no MSBuild file. The integration shards hold no grant and
leave what they saw as artefacts; `integration-report`, with no checkout, holds
`issues: write` and posts them. Every checkout in these jobs sets
`persist-credentials: false`, and `packages` reads the base's project files from
a second checkout rather than a fetch, so it holds no git credential whatever
the repository's visibility.
**Why.** A job holds its grants from its first step, and `permissions:` takes no
expression, so a step's `if:` stops that step and nothing else. A target in a
branch's `Directory.Build.targets` runs during `dotnet pack`, reads the token
checkout persisted for git and publishes to the feed, with no review and no
merge; a Dockerfile or a test reaches the same way what its job holds. ADR-109
called its grants harmless for two reasons: every step that spends one carries
the main-push condition, which stops that step and not a branch's code, and a
token minted for a `refs/pull/...` ref cannot satisfy the verifier, which is
still so of the signature and says nothing of the `packages` grant, which needs
no signature to put an image under a tag. Pushing an artefact rather than
packing again in the publishing job is what leaves no build beside its token.
**Consequences.** This narrows the hole and does not close it. A pull request
runs the head's copy of `ci.yml`, so whoever may write a branch can add a grant
to a job in that branch, and neither this rule nor its gate, which the same head
carries, stops that run. What the change buys is that a write to a project file
or a Dockerfile alone no longer reaches a token that can write: taking one now
takes a workflow edit, in the diff a reviewer reads first. The real control is
who may write a branch, on 2026-11-08 the owner alone. **Revisit trigger:** a
second person who may write one is when a ruleset limiting branches, and a
protected environment on the publishing jobs, are owed; neither is taken now,
because with one writer each adds a setting that guards nothing. The build line
and the filter guard are now written twice, and the gate holds the build line to
its copy and not the guard. `integration-report` bills a short job on every run
the shards ran. On a push to `main`, `images` is one skipped check whose name
shows its expression, and `publish-images`' legs are named with every matrix
value. `publish-packages`, like the rest of ADR-109's signing, cannot run on the
pull request that changes it, so the first push to `main` that changes a package
is where a wrong path shows. The comment steps run on one but post only what a
shard left, so the first sighting is where theirs shows, and a comment that
fails leaves its artefact and fails no run. A cancelled run posts nothing.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)

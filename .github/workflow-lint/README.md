# The workflow lint

**The claim: every workflow under `.github/workflows/` is well formed to
GitHub, not only to a parser.** The gates read the workflows as text for
coverage and order; none checks GitHub expression syntax, a reference to a
step output that does not exist, an action's input names or the shell in a
`run:` block. The signing steps of [ADR-109][adr-109]
cannot run before the first push to `main`, so a typo there would otherwise
show only on the first real run.

## What it reads

- Every workflow under `.github/workflows/`, by `actionlint`, in the
  `workflow-lint` job of [`ci.yml`](../workflows/ci.yml). The runner carries
  `shellcheck`, which `actionlint` runs over each `run:` block it finds.
- The binary is pinned to a version and checked against the digest its
  release publishes, so a re-uploaded asset fails the download rather than
  running.

## The case

`broken-reference.yml` reads `steps.absent.outputs.digest`, a step no job
has. The job lints it first and requires the lint to fail with
`property "absent" is not defined`, so a lint that stopped reading, or one
that failed for another reason, goes red. It sits outside `.github/workflows/`
so GitHub never runs it and the lint of the real workflows never meets it.

## What it does not claim

- It cannot know a repository variable, secret or environment exists, or that
  a third-party action's version does what its name says.
- It runs on every pull request with no path filter, because a workflow can
  change in any commit.

## Moving the pin

Take the version's `actionlint_<v>_checksums.txt` from its release, copy the
`linux_amd64` line's digest into `ACTIONLINT_SHA256`, and move
`ACTIONLINT_VERSION` with it.

[adr-109]: ../../docs/backend-architecture/adr/ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md

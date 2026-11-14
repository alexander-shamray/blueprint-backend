# The pipeline gate

**The claim: [§15.1](../../docs/backend-architecture/15-cicd-deployment.md)'s
staged pipeline is not quietly doing other than it appears to.** Each of its
eight subcommands is an inventory, and each fails on the ways the pipeline
can go green while running something other than what it shows.

| Subcommand | Refuses | Reads |
|---|---|---|
| `filters` | an immediate child of `src/` or `src/Services/` that no path filter matches — one CI would never rebuild — and a paths-filter step in the `changes` job whose own `predicate-quantifier` key is not `some-with-excludes`, the quantifier without which the `deploy` filter's exclusions are never evaluated and a compose-only change deploys; the key is read, not the text, so the word in a comment does not pass it, and no such step fails | those two directories' children, and `ci.yml`'s path filters and its `changes` job's steps |
| `images` | a Dockerfile under `src/` no matrix entry builds, and an entry whose Dockerfile is missing or whose filter is undefined, not exported by the `changes` job, or does not match that Dockerfile's path; and an `images` job whose job-level `if:` is anything but the split from `publish-images`, which would leave a skipped job's matrix name unexpanded on a pull request or build every image twice on `main`; a `publish-images` job its own condition does not keep to a push or dispatch on `main`, that keeps a matrix of its own, or whose `docker build` is not the line `images` runs ([ADR-197](../../docs/backend-architecture/adr/ADR-197-write-grants-leave-the-jobs-that-run-a-pull-requests-code-1789-amending-adr-109-and-adr-149.md)); and a build step in either with no SBOM step after it reading the reference it tags under the same `if:` ([ADR-071](../../docs/backend-architecture/adr/ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md)); a step in either that may fail, other than the upload of a retry's record, with no `id`, no `image_retry.sh note` under its failed outcome after it, or no second attempt after that which runs under the same outcome, may not fail in turn and does what the first does, or a step line in either it cannot read, which would hide a key such as a flow-style step's `continue-on-error`; and, per build step in `publish-images`, a missing registry sign-in, `docker push`, cosign installer, `cosign sign`, provenance attestation or SBOM attestation after it, one not under the main-push condition, whose filter, ref and push event are each a top-level `&&` term, a signature or attestation by tag rather than the pushed digest, an attestation not pushed to the registry or reading another file than the SBOM step wrote; a `REGISTRY` that differs between `ci.yml` and `deploy.yml`, or a `ghcr.io/` address spelled outside it; a rollout with no `cosign verify` pinned to the `ci.yml`-on-`main` identity, or one that runs after its first `helm` command; a `cosign verify` whose identity or issuer flag, read as a shell word, takes its `-regexp` form, or reads neither this repository's identity nor GitHub's issuer, written in the run or through a variable every `env:` line sets so, and a `gh attestation verify` whose `--repo`, `--signer-workflow` or `--source-ref` does not name this repository, its `ci.yml` and `main`; a verify that records no tag on the digest it verified ([ADR-201](../../docs/backend-architecture/adr/ADR-201-the-rollout-tags-the-digest-it-verified-so-the-prune-keeps-what-a-release-pins-1915-amending-adr-109-and-adr-199.md)); and a `helm upgrade` that passes no `image.digest` or `image.migratorDigest` ([ADR-109](../../docs/backend-architecture/adr/ADR-109-images-are-pushed-to-ghcr-signed-and-attested-by-digest-on-main-and-the-rollout-verifies-them-before-helm-438.md)); per build step, no push of the image's `main` tag, one not under the main-push condition, or one before the provenance or the SBOM attestation, a build with no `org.opencontainers.image.revision` label, and a move of the tag with no `git merge-base --is-ancestor`, or one whose answer is not kept and acted on by a `case` whose `1)` arm exits and whose `*)` arm exits 1, or none that reads the tag back from the registry after its push and compares it with the signed digest in an `if [ ... != ... ]` whose own branch exits 1 — each the first exit that arm or branch reaches on every run, not one inside a nested `if`, `case` or loop, nor one right after `&&`, `||` or a pipe, directly or through a line continuation, nor one inside a `{` group such an operator opens or a `(` still open ([ADR-134](../../docs/backend-architecture/adr/ADR-134-each-image-has-a-main-tag-that-names-its-newest-signed-build-and-compose-names-the-image-beside-its-build-1111-adding-to-adr-109.md)); a `docker buildx build`, `bake` or `imagetools create`, `docker image build` or `push`, `docker builder build` or `docker compose build` or `push` in either image job, or a `docker/build-push-action` or `docker/bake-action` step, forms no check here reads; a build step or a `helm upgrade` the parser cannot find fails rather than passing | the Dockerfiles, and in `ci.yml` the image matrix, the path filters, the `changes` job's outputs, the `images` and `publish-images` jobs' keys, conditions and steps and its `REGISTRY`; in `deploy.yml` its `REGISTRY`, the `rollout` job's steps and every `helm upgrade` |
| `shards` | an integration shard that does not exclude every shard above it, which runs those tests twice; one that excludes a namespace no shard above it selects, which runs those tests nowhere; a last shard that selects anything rather than being the remainder; any shard but the last selecting other than one namespace; and a term that is not a `FullyQualifiedName` one over a dotted name, which an OR-ed or parenthesised value is not. `stages` reads the shards as the one directory they land in, so this is the check that can see them | in `ci.yml`, the `integration` job's matrix |
| `actions` | a `uses:` in any workflow, a step's or a reusable workflow's, that names an action outside `actions/` by anything but a full commit SHA, and a `docker://` image with no digest. A tag can be moved to other code, and `paths-filter` decides which gates run. GitHub's own `actions/*` ride major tags by a separate decision | every `.yml` and `.yaml` under `.github/workflows/` |
| `grants` | an `on:` it cannot read, whose jobs it then reads as a pull request's; a job in a workflow a pull request starts, by `pull_request`, `pull_request_target` or either review event, that holds a write grant, its own or the workflow's, and checks out the branch or calls a reusable workflow, unless its own `if:` is false on every pull request's run, which a condition it cannot read as a test of the ref or the event is not; no workflow, or no job holding a grant anywhere, fails rather than passing. It reads what lands: a pull request runs the head's copy of the workflow, and of this gate, so it stops nobody editing either on a branch ([ADR-197](../../docs/backend-architecture/adr/ADR-197-write-grants-leave-the-jobs-that-run-a-pull-requests-code-1789-amending-adr-109-and-adr-149.md)) | every `.yml` and `.yaml` under `.github/workflows/`: its `on:` block's events, under a bare or quoted key, and each job's `permissions:`, `if:`, `actions/checkout` and job-level `uses:` |
| `alerts` | a workflow a push to `main` runs that `main-red.yml`'s `workflow_run` list does not name, one with no `name:` for it to name, and a listed name that no workflow carries or whose workflow never runs on a push to `main`, and an `on:` written as a flow mapping, at the key or at an event, or in another form it cannot read; a branch filter's entries are matched as GitHub's globs. `workflow_run` matches by name and a name nothing carries matches nothing without an error, so a workflow added or renamed would go red on `main` unheard ([ADR-155](../../docs/backend-architecture/adr/ADR-155-a-red-push-run-on-main-files-a-tracker-issue-and-is-reverted-before-it-is-fixed-1450.md)); an alert file missing or listing nothing, or no workflow pushed on `main` at all, fails rather than passing; and a `file` job that drops a conclusion a red run ends on, or a `cancelled` run no `read-cancelled` job reads into it ([ADR-196](../../docs/backend-architecture/adr/ADR-196-a-cancelled-push-run-on-main-is-red-unless-a-newer-run-replaced-it-1871-amending-adr-155.md)), and a `rerun` job not kept to a first attempt or a `file` job that does not file what its rerun did not see pass, each term read as an `&&` term or an `||` operand of one, so an operator flipped, a term negated or a number widened is a term missing ([ADR-210](../../docs/backend-architecture/adr/ADR-210-a-red-first-attempt-of-a-push-run-on-main-is-rerun-once-and-files-only-when-the-second-attempt-is-red-too-2018-amending-adr-155.md)) | every `.yml` and `.yaml` under `.github/workflows/`: its `name:` and its `on:` block's `push` filter, and `main-red.yml`'s `workflow_run` list and its three jobs' `if:` |
| `code` | a `code` filter that is not `**` and exclusions, which would list what is code so that a path nobody listed skips every stage; a `stage-inputs` filter holding an exclusion; a pattern holding glob syntax the gate's own reader does not read; a `code` output that can say false for a push or a dispatch or ignores `stage-inputs`, each of its three terms read as a top-level `||` operand, so a term joined by `&&` or negated is missing; a job-level `if:` on `build`, `integration` or `stage-gate`, which would lose a shard's name or skip what needs the job; a step of those three with no `if:` on the output as an `&&` term, so `|| always()` beside it or a `!` before it does not pass, unless it runs on `failure()`, or on `always()` beside a non-empty `hashFiles` or a step output equal to a non-empty value as `&&` terms, or a step line of those three it cannot read; a line of the paths-filter block that is no filter key, quoted or bare item, alias or comment, whose pattern would go unjudged; an exclusion that is neither a whole tree nor a name at the root; and a path under an exclusion that a C# or MSBuild file names and neither filter counts, which is a stage a pull request changing that file would skip, the search coming from the filter so a new exclusion is scanned; and a tracked path holding a line break, which the pinned action's `**` does not match, so a pull request adding only that file would skip every stage. No such file named, no steps or no filter fails rather than passing | in `ci.yml`, those two filters, the `changes` job's output and those three jobs; the `.cs`, `.csproj`, `.props` and `.targets` files under `src/`, `tests/` and `tools/` and at the root; `git ls-files` |
| `stages` | a test project in `Platform.slnx` that ran in no stage, a test that ran in two, an empty stage (a skipped test counts toward neither a stage's total nor its project), a stage under its floor, a stage whose results directory was never passed, and a directory naming no stage it knows | `Platform.slnx` and the TRX files in the three stage result directories |

`stages` counts tests rather than trusting an exit code, because `dotnet
test` exits zero on a filter that matched nothing
([§12.1](../../docs/backend-architecture/12-test-strategy.md)'s trap). The
structural half has two checks. Exhaustive is judged per project: every
project in the solution contributed to some stage. Disjoint is judged per
test, by an identity that carries its assembly: one project may put
different tests in different stages, but no test runs twice, and on the
integration stage an overlap is a container set paid for twice. The floor is
the other half, set well under any plausible total on purpose: it gropes for
an order-of-magnitude miss, not a count.

## How it runs

Its suite and `filters`, `images`, `shards`, `actions`, `grants`, `alerts` and
`code` run in the fast job of [`ci.yml`](../workflows/ci.yml); `stages` runs after the three
test stages.
The suite is negative cases with their positive controls, because a gate
only ever observed green is one nobody has established is looking at
anything.
`docs/testing.md` has the stage invocations `stages` needs in front of it.

`read_cancelled_run.py` and `rerun_red_run.py` live beside the gate and are
not among its subcommands: `main-red.yml` runs them, and the suite here covers
them and pins the path the workflow runs each by. The second reruns a red first
attempt once and waits for the second
([ADR-210](../../docs/backend-architecture/adr/ADR-210-a-red-first-attempt-of-a-push-run-on-main-is-rerun-once-and-files-only-when-the-second-attempt-is-red-too-2018-amending-adr-155.md)),
reading a cancelled one with the first.

`image_retry.sh` lives beside it for the same reason: the two image jobs run
Build, SBOM, Sign, Provenance and SBOM attestation through it, once more after
a failure and a wait, and `image-retry-report` posts what a leg recovered from
on the flaky tests tracker. A shell command runs under it; an action cannot, so
it takes `continue-on-error`, a `note` step and a second attempt, which `images`
holds in that shape and the suite holds the real workflow to. Signing twice is
safe, as checked against cosign's pinned release: a second attempt signs with a
new key, so Rekor's 409 for an entry it already holds does not arise, and
`cosign verify` passes on any one signature that verifies.

## What a pull request may skip

`changes` answers `code` from two filters: everything but the trees and root
Markdown no stage reads, and what a stage reads inside those exclusions. A path
nobody listed is code, so a new input costs a build rather than skipping one.
The answer is false only on a pull request. A push run on `main` tests
everything whatever it changed, so a test that starts reading an unlisted file
in a way `code` cannot see goes red there, on the next push, under ADR-155.

`commit_builds.py` lives beside the gate for the reason the two above do: the
`commits` job runs it, and it reads the same two filters, so there is one list.
A build's outcome is a function of its inputs, so a commit whose diff from its
one parent touches none of those inputs builds what its nearest built ancestor
built, and is skipped with a line naming it. A merge, a root, an empty diff or
a path it does not know is a build, and a failed git read fails the step. It
is a superset of what compiles: a commit
editing `docs/personal-data.md` builds although only a test reads that file,
because a second list is what would drift. `test_commit_builds.py` runs the
two steps' own text over a scratch repository with `dotnet` stubbed: the one
that decides which commits build and which SDKs they pin, and the loop.

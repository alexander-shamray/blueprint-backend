# The pipeline gate

**The claim: [§15.1](../../docs/backend-architecture/15-cicd-deployment.md)'s
staged pipeline is not quietly doing other than it appears to.** Each of its
five subcommands is an inventory, and each fails on the ways the pipeline
can go green while running something other than what it shows.

| Subcommand | Refuses | Reads |
|---|---|---|
| `filters` | an immediate child of `src/` or `src/Services/` that no path filter matches — one CI would never rebuild — and a `ci.yml` in which the text `some-with-excludes` appears nowhere, the quantifier without which the `deploy` filter's exclusions are never evaluated and a compose-only change deploys. That is a text search, not a read of the paths-filter step, so the token surviving elsewhere in the file passes it | those two directories' children, and `ci.yml`'s path filters and its text |
| `images` | a Dockerfile under `src/` no matrix entry builds, and an entry whose Dockerfile is missing or whose filter is undefined, not exported by the `changes` job, or does not match that Dockerfile's path; and an `images` job `if:` that does not test exactly the filters the matrix builds under; and a build step with no SBOM step after it reading the reference it tags under the same `if:` ([ADR-071](../../docs/backend-architecture/adr/ADR-071-every-image-carries-an-sbom-and-signing-waits-on-a-registry.md)) | the Dockerfiles, and in `ci.yml` the image matrix, the path filters, the `changes` job's outputs, the `images` job's `if:` and its steps |
| `shards` | an integration shard that does not exclude every shard above it, which runs those tests twice; one that excludes a namespace no shard above it selects, which runs those tests nowhere; a last shard that selects anything rather than being the remainder; any shard but the last selecting other than one namespace; and a term that is not a `FullyQualifiedName` one. `stages` reads the shards as the one directory they land in, so this is the check that can see them | in `ci.yml`, the `integration` job's matrix |
| `actions` | a `uses:` in any workflow, a step's or a reusable workflow's, that names an action outside `actions/` by anything but a full commit SHA, and a `docker://` image with no digest. A tag can be moved to other code, and `paths-filter` decides which gates run. GitHub's own `actions/*` ride major tags by a separate decision | every `.yml` and `.yaml` under `.github/workflows/` |
| `stages` | a test project in `Platform.slnx` that ran in no stage, a test that ran in two, an empty stage, a stage under its floor, a stage whose results directory was never passed, and a directory naming no stage it knows | `Platform.slnx` and the TRX files in the three stage result directories |

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

Its suite and `filters`, `images`, `shards` and `actions` run in the fast job of
[`ci.yml`](../workflows/ci.yml); `stages` runs after the three test stages.
The suite is negative cases with their positive controls, because a gate
only ever observed green is one nobody has established is looking at
anything.
`docs/testing.md` has the stage invocations `stages` needs in front of it.

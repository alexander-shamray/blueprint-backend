# The pipeline gate

**The claim: [§15.1](../../docs/backend-architecture/15-cicd-deployment.md)'s
staged pipeline is not quietly doing other than it appears to.** Each of its
three subcommands is an inventory, and each fails on the ways the pipeline
can go green while running something other than what it shows.

| Subcommand | Refuses | Reads |
|---|---|---|
| `filters` | an immediate child of `src/` or `src/Services/` that no path filter matches — one CI would never rebuild — and a `ci.yml` in which the text `some-with-excludes` appears nowhere, the quantifier without which the `deploy` filter's exclusions are never evaluated and a compose-only change deploys. That is a text search, not a read of the paths-filter step, so the token surviving elsewhere in the file passes it | those two directories' children, and `ci.yml`'s path filters and its text |
| `images` | a Dockerfile under `src/` no matrix entry builds, and an entry whose Dockerfile is missing or whose filter is undefined, not exported by the `changes` job, or does not match that Dockerfile's path | the Dockerfiles, and in `ci.yml` the image matrix, the path filters and the `changes` job's outputs |
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

Its suite and `filters` and `images` run in the fast job of
[`ci.yml`](../workflows/ci.yml); `stages` runs after the three test stages.
The suite is negative cases with their positive controls, because a gate
only ever observed green is one nobody has established is looking at
anything.
`docs/testing.md` has the stage invocations `stages` needs in front of it.

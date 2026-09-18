# The pipeline gate

**The claim: [§15.1](../../docs/backend-architecture/15-cicd-deployment.md)'s
staged pipeline is not quietly running less than it appears to.** Each of its
three subcommands is an inventory, and each fails on the one way the
pipeline can go green while covering less.

| Subcommand | Refuses | Reads |
|---|---|---|
| `filters` | a deployable under `src/` that no path filter matches — one CI would never rebuild | `src/` and the path filters in `ci.yml` |
| `images` | a Dockerfile under `src/` no matrix entry builds, and an entry whose Dockerfile is missing or whose filter is undefined, not exported by the `changes` job, or does not match that Dockerfile's path | the Dockerfiles and `ci.yml`'s image matrix |
| `stages` | a test project in `Platform.slnx` that ran in no stage or in two, an empty stage, and a stage under its floor | `Platform.slnx` and the TRX files in the three stage result directories |

`stages` counts tests rather than trusting an exit code, because `dotnet
test` exits zero on a filter that matched nothing
([§12.1](../../docs/backend-architecture/12-test-strategy.md)'s trap). The
structural half — every project in exactly one stage — is what turns the
stages being exhaustive and disjoint from a claim into a check, and on the
integration stage an overlap is a container set paid for twice. The floor is
the other half, set well under any plausible total on purpose: it gropes for
an order-of-magnitude miss, not a count.

## How it runs

Its suite and `filters` and `images` run in the fast job of
[`ci.yml`](../workflows/ci.yml); `stages` runs after the three test stages.
Every test in the suite is a negative case, because a gate only ever
observed green is one nobody has established is looking at anything.
`docs/testing.md` has the stage invocations `stages` needs in front of it.

# Testing

**What a checkout needs to know that neither §12, `Platform.slnx` nor the
workflows can tell it.**
[§12](backend-architecture/12-test-strategy.md) is the strategy — the pyramid,
the TDD cycle, what each level is for and what not to test. The solution file
is the list of test projects, and the workflows under `.github/workflows/`
are the list of everything else CI runs. This file is the remainder: which
tests need a daemon and why they are never skipped, the Python floor, how to
run a gate on its own, what the coverage figure is measured over, and how a
mutation run and the review helpers' time budget, which no workflow makes,
are made by hand.

**Where §12 and this file disagree, §12 wins**, and the disagreement is a bug
report against one of them. This file is outside the blueprint tree, so
`/check-links` does not reach it; `/validate-blueprint` does, because it is
named in that command's scope.

## Where the suites are

`dotnet test Platform.slnx` runs every test project the solution names, and
`Platform.slnx` is the only list of them. Everything else is stdlib Python or
shell, kept beside the thing it checks, and **the workflow that runs it is
the enumeration** — `ci.yml` tests each gate and then runs it, and each
workflow beside it is the run of whatever it watches, with a suite in front
where the gate has one. None
of those suites is in `Platform.slnx`, so a green solution says nothing about
any of them; read the workflows, not this file, for what exists.

To run one locally, do what its workflow step does, from the directory the
step runs in, with `py -3.12` in place of `python`. A suite is
`py -3.12 -m unittest` or `py -3.12 -m unittest discover -s <dir>` as the
step spells it; the gate is the command the step after it runs. Each tree
under `deploy/` carries a README stating what its gate asserts and, more
usefully, what it does not — the chart gate reaches no cluster, the
observability gate no Prometheus, the realm gate a file locally and a live
realm only from a workflow holding a credential, and nothing in the canary
tree has established that a replica ratio is a traffic ratio.

The review helpers' suite under `.claude/scripts/` is the one with
requirements a workflow step does not show: `bash`, `grep`, `git` and `jq`
on `PATH`, and no network. The `gh` its ledger cases call is a stub the
suite writes itself, so no `gh` is needed. Each module is named for its
subject — one helper, a family of helpers, or the harness's deny list — and
its test classes name the properties they check.

**Each of its modules has a time budget, on Windows.** CI runs the whole
suite only on Linux and times no module anywhere, so nothing there notices a
module that has become slow on the platform this repository's sessions run
it on. `py -3.12 tools/suite-budget/suite_budget.py` runs each module in a
process tree of its own, as `discover -s .claude/scripts -p <module>`, and
names every one that fails, passes past its ceiling, or is still running at
`KILL_FACTOR` times it — stopped there with every process still in its tree,
so a hang reads as a hang. It is a measurement a session runs, not a gate: its
ceilings are Windows figures, which a shared Linux runner would judge
against nothing, so `ci.yml` runs its suite alone. Run it after a change
that adds a module there or slows one. The ceilings are `CEILINGS` and
`DEFAULT_CEILING` in that script, set from this measurement, each module run
alone on Windows 11 under Python 3.12 at `e037e499`: 704 tests, 482 seconds
in all. Its suite holds the table to the directory, a row per module, and
each ceiling to its row, so a module added there is measured here too.

| Module | Seconds | Module | Seconds |
|---|---:|---|---:|
| `test_git_rebase_onto_main.py` | 196.4 | `test_index_query_hint.py` | 4.7 |
| `test_git_argv_guard.py` | 57.3 | `test_gh_label_ensure.py` | 4.1 |
| `test_git_pr_trailer_helpers.py` | 37.3 | `test_index_argv_guard.py` | 3.3 |
| `test_git_worktree_remove.py` | 33.6 | `test_hint_bound.py` | 3.1 |
| `test_pr_helpers.py` | 25.5 | `test_edit_target_guard.py` | 2.9 |
| `test_sweep_slices.py` | 24.8 | `test_gh_issue_suppresses.py` | 2.1 |
| `test_index_search_hint.py` | 16.9 | `test_pr_for_branch.py` | 1.3 |
| `test_git_worktree_fork.py` | 13.3 | `test_refresh_index.py` | 0.8 |
| `test_mutation_survivors.py` | 13.0 | `test_harness_denies.py` | 0.7 |
| `test_cbx.py` | 10.8 | `test_root_ignore_covers_nested.py` | 0.4 |
| `test_ship_landing_method.py` | 9.9 | `test_system_sleep.py` | 0.3 |
| `test_git_worktree_detach.py` | 7.4 | `test_command_grants.py` | 0.2 |
| `test_index_mcp_root.py` | 6.6 | `test_command_runbooks.py` | 0.2 |
| `test_gh_issue_create.py` | 5.3 | `test_hook_timeouts.py` | 0.2 |

**`py -3.12`, not `python`.** Every CI job that runs Python pins 3.12, and a
newer interpreter is the hazard — it accepts APIs 3.12 does not, so the local
suite goes green on code the runner cannot execute. The scaffold *script* is a
different matter: running it is not a test of the floor, so plain `python` is
fine there.

## Docker is not optional

A test project needs a daemon when a class in it joins a collection carrying
`Category=Integration`, and the projects that do are the directories this
finds a file in:

```bash
rg -l '^\[Trait\("Category", "Integration"\)\]' tests/
```

The anchor matters: unanchored, the pattern also matches the doc comments
that mention the attribute.

Each such collection starts its own container set
([§12.4](backend-architecture/12-test-strategy.md)'s stated price). Without a
daemon they fail on `Failed to connect to Docker endpoint`, which is a true
statement about the machine rather than a defect in the branch.

`dotnet test Platform.slnx` runs all of those projects at once against one local
daemon, where CI shards them so that no runner's daemon carries them all, and a
daemon that busy can refuse a container start or an image build that a moment
later succeeds. Every fixture therefore makes those calls through `DaemonRetry`
in `Common.TestSupport`, which tries a refused one again a bounded number of
times and then lets the last refusal through, so a machine with no daemon still
fails rather than skips.

A retry cannot reach one failure: Testcontainers' `ResourceReaper` initialiser
parses an image tag under a one-second regex timeout, and a type initialiser
that threw never runs again in the process, so every fixture after it fails
alike. `TestcontainersWarmUp` in `Common.TestSupport` runs it before a
fixture builds its first container and names that failure, and only a new
process recovers. It first matches the same pattern once outside that timeout,
so the regex engine's first-use cost is already paid. Every fixture that
builds a container calls it before the first builder, through
`TestcontainersWarmUp.Build` where that builder is a field initialiser, and a
new fixture does the same. CI's integration shards take the new process: a
shard whose first attempt fails with that named failure runs once more, with
the first attempt's results removed, and the run is posted on
[#960](https://github.com/alexander-shamray/blueprint-backend/issues/960) for
the recurrence count. A first attempt without it is final. The retry runs the
whole shard, so a failure beside the named one gets a second run too.

**They are not skipped when the daemon is absent, and that is a decision.** A
skip on a missing daemon **fails open**: CI goes green on a runner whose Docker
broke, and nobody finds out until the thing those tests were guarding breaks in
production. [ADR-010](backend-architecture/adr/ADR-010-testcontainers-not-in-memory-providers.md)
already made real infrastructure non-optional, and this is the same rule one
layer down.

**A category is the opposite of a skip, which is why one exists and the other
does not.** Selecting a test *out* by category runs a smaller suite and says
so; skipping it runs the suite and reports a pass. The next section is about
the first.

## Categories

Every test that needs a container carries `Category=Integration`. Everything
else carries no category at all, so the two filters partition the suite:

```bash
dotnet test Platform.slnx --filter "Category!=Integration"   # no daemon needed
dotnet test Platform.slnx --filter "Category=Integration"    # the container half
```

**The trait is declared on the collection definition, not on each test class,
and that is the whole design.** xUnit v3 applies a collection's traits to every
test in it, so *joining the container collection is carrying the category*.
There is no per-class attribute for a new test class to forget, and no
reflection gate needed to check that nobody did — the thing that decides the
category is the same thing that decides whether the test gets a container.

```csharp
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
```

**No container starts in the fast half**, and the mechanism is the reason
rather than an observation: xUnit constructs a collection fixture only when a
test in that collection runs, so filtering the collection out means the
container is never asked for. Pointing `DOCKER_HOST` at a dead endpoint does
not prove this — Testcontainers can ignore the variable and pass against the
real daemon — so a claim that a run started nothing is checked with
`docker events --filter event=create` over the window, never inferred from a
green exit under an override.

The category goes on a **collection** rather than a project because the two
can differ: `Web.Bff.Tests` is one project in which some tests need no
container, some need an identity provider, and those over ADR-051's
projection need SQL Server and the broker through `BffIntegrationCollection`,
so a project-level split would have had nothing to split. What the category
buys there is a container start rather than a fast suite, because §9.7's
resilience tests wait on real timeouts either way.

**A test class that needs a container and forgets the collection fails loudly
rather than quietly**, which is the direction this has to fail in. It has no
fixture, so it does not run against one; it also carries no category, so it
runs in the fast half and fails there. What it cannot do is report a pass.

## Running a gate on its own

A gate with a suite is tested and then run, so a green suite says the gate
works and not that this checkout passes it; a gate without one is only run.
Most gate runs are the workflow step verbatim. The entries below are the ones
a step does not show whole, and the gates whose invocation
[`docs/change-locality.md`](change-locality.md) §3's Class D places here,
each pointing at the README that owns its claim.

**The stage gate needs three `dotnet test` runs first**, which are
[§15.1](backend-architecture/15-cicd-deployment.md)'s unit and integration
stages with the architecture gates split off in front, for the
instrumentation reason under *Coverage* below. CI runs the integration stage
as shards, each a job with its own build so that `--no-build` reads it, and
hands every stage's results to one last job, which runs this gate and the
coverage report over them; `ci.yml`'s `integration` job argues why the
shards' filters partition the stage. Run here, the stage is one invocation:

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName~ArchitectureTests" \
    --logger trx --results-directory ./TestResults/architecture
dotnet test Platform.slnx --filter "FullyQualifiedName!~ArchitectureTests&Category!=Integration" \
    --logger trx --results-directory ./TestResults/unit
dotnet test Platform.slnx --filter "Category=Integration" \
    --logger trx --results-directory ./TestResults/integration

py -3.12 .github/pipeline-gate/pipeline_gate.py stages \
    ./TestResults/architecture ./TestResults/unit ./TestResults/integration
```

**The logger and the directories are not decoration.** The gate counts from
TRX and looks for those three directory names, so a bare `dotnet test` runs
the stages and leaves it nothing to read. What it asserts over them is
[its README](../.github/pipeline-gate/README.md)'s.

**The domain-name gate needs nothing in front of it**, and its suite reads
this checkout's chapters as well as a fixture tree:

```bash
py -3.12 -m unittest discover -s .github/domain-name-gate
py -3.12 .github/domain-name-gate/domain_name_gate.py check
```

What it refuses is [its README](../.github/domain-name-gate/README.md)'s.

**The output gate needs a build in front of it**, and its step does not say
so because the build is the line above it in the same job. Run on its own it
reads a tree, so what it reports is about whichever build ran last — or, on a
fresh clone, about none:

```bash
dotnet restore Platform.slnx
dotnet build Platform.slnx
py -3.12 .github/output-gate/output_gate.py
```

`--no-restore` on the build is fine once a restore has happened. What the
gate asserts, and why it needs both, is
[its README](../.github/output-gate/README.md)'s.

**The closure gate and the locality gate need a pull request**, so their live
runs take a number and a `gh` session that CI has and a checkout does not:

```bash
gh pr view <n> --json number,url,body,commits,closingIssuesReferences,headRefOid,author |
    py -3.12 .github/closure-gate/closure_gate.py

gh api "repos/{owner}/{repo}/pulls/<n>/files" --paginate --jq '.[] | {filename, previous_filename, status}' |
    jq -s --argjson pr "$(gh pr view <n> --json number,body,changedFiles)" '$pr + {files: .}' |
    py -3.12 .github/locality-gate/locality_gate.py
```

The second reads the paginated files endpoint rather than
`gh pr view --json files`, which is one page; why it needs `changedFiles`,
`previous_filename` and `status` beside it is
[its README](../.github/locality-gate/README.md)'s. It judges ignored paths
against the checkout it runs in, so run it on the pull request's branch. What
the first compares is [its README](../.github/closure-gate/README.md)'s.

**The commit gate needs a pull request's number and a base**, and reads git
rather than `gh`, so a branch can run it once its pull request exists:

```bash
git fetch origin main
py -3.12 .github/commit-gate/commit_gate.py --pr <n> origin/main HEAD
```

What it reads and judges is [its README](../.github/commit-gate/README.md)'s.

**The comment gate needs only a base**, because it diffs the checkout's own
history, so a branch can run it before it has a pull request:

```bash
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
py -3.12 .github/comment-gate/comment_gate.py --tree src/Services/Catalog
```

The second form judges every line under the named paths rather than a diff,
which is how a sweep proves a tree is under the rule.

What it reads and judges is [its README](../.github/comment-gate/README.md)'s.

**The plan-size gate** reads a directory and needs no base:

```bash
py -3.12 .github/plan-size-gate/plan_size_gate.py
```

What it reads and claims is [its README](../.github/plan-size-gate/README.md)'s.

**The SQL style gate** reads the tracked tree and needs no base:

```bash
py -3.12 -m unittest discover -s .github/sql-style-gate
py -3.12 .github/sql-style-gate/sql_style_gate.py
```

What it reads and claims is [its README](../.github/sql-style-gate/README.md)'s.

**The Compose smoke is the one gate that needs a running daemon and the one
whose run changes the machine it runs on**, so its teardown is part of what
it asserts rather than tidying: RabbitMQ seeds its default user only on an
empty database, so ADR-036's removal of `guest` is true after a `down -v`
and false after a plain `down`.

```bash
docker compose -f deploy/compose/docker-compose.yml config -q
docker compose -f deploy/compose/docker-compose.yml up -d --wait --quiet-pull
docker compose -f deploy/compose/docker-compose.yml down -v
```

`config -q` runs first so that a YAML error costs no image download to find,
and `up --wait` is the assertion rather than the setup — it exits non-zero if
a healthcheck never passes, and a one-shot that another service gates on with
`service_completed_successfully` satisfies it by exiting 0, so the migrators'
exit codes are part of what this proves. `rabbitmq` is built rather than
pulled, so an image build rides on the `up`. The workflow then holds the
local Grafana's loaded rules and dashboards to `deploy/observability/`;
`deploy/compose/README.md`'s *What the local Grafana loads* gives the reads
to run it locally.

**The game day causes each alert on that stack and runs its runbook's first
step.** Its suite and its coverage test (every runbook has a script or a
stated reason) need no daemon; the run itself does, with the stack up:

```bash
cd tools/game-day && py -3.12 -m unittest
py -3.12 tools/game-day/game_day.py outbox-broker
```

A run takes minutes per runbook, because each waits out a rule's threshold
and its intervals, and over half an hour where the rule reads a 30-minute
window, so CI runs the suite on pull requests and the run itself only by
`workflow_dispatch`, one job and one stack per runbook and never on a
schedule. [Its README](../tools/game-day/README.md) owns what a pass proves,
and its *Running it in CI* why the run is not scheduled.

**`HELM=` is an override, not a gate run.** The chart gate runs
`helm dependency update` itself — `file://` dependencies resolve from disk, so
there is no network step and no chart repository — and this only tells it
where the executable is when `helm` is not on `PATH`. `PYTHON=` does the same
for the interpreter that reads its cases out of the deployables' descriptors,
and on Windows it is `py -3.12`, because `python3` there is the Store's stub:

```bash
HELM=/path/to/helm PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

## Coverage

**Reported, not gated** —
[the reporter's README](../.github/coverage/README.md) says why, and what it
reads. What this section carries is the invocation and what the figure is
measured over.

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName!~ArchitectureTests&Category!=Integration" \
    --collect:"Code Coverage" --settings coverage.runsettings \
    --results-directory ./TestResults/unit
dotnet test Platform.slnx --filter "Category=Integration" \
    --collect:"Code Coverage" --settings coverage.runsettings \
    --results-directory ./TestResults/integration
py -3.12 .github/coverage/domain_coverage.py ./TestResults/unit ./TestResults/integration
```

**`--results-directory` is not decoration either.** Without it the collector
writes under each *test project's* own `TestResults/`, and the reporter,
which defaults to `./TestResults` at the repo root, finds nothing and exits
non-zero.

`coverage.runsettings` filters the report to `.*\.Domain\.dll$` and emits
Cobertura. That is §12.9's "watch coverage of the domain layer specifically —
that is where it should be near-total, and where it is cheapest to achieve",
made into an artefact instead of an instruction. Three things about that
filter are deliberate:

- **It is a pattern, not a list.** Every service's Domain matches it the day
  it exists. A list would have to be edited by whoever adds a service, which
  is exactly the edit that gets missed.
- **It measures the domain assemblies over the *whole* run**, not the domain
  test projects. Domain types are exercised by application and API tests too,
  and a figure taken from `*.Domain.Tests` alone would under-report the thing
  it is named after.
- **The collector is `Microsoft.CodeCoverage`**, which arrives with
  `Microsoft.NET.Test.Sdk` and is therefore already in every test project. No
  package was added and no licence-register entry was needed
  ([Appendix B](backend-architecture/appendix-b-licences.md)); a coverage
  figure is not worth a new dependency.

> **The architecture gates run first and uninstrumented, and that seam is
> instrumentation rather than preference.** §4.2's gates read
> `GetReferencedAssemblies` on the Domain assemblies; `coverage.runsettings`
> instruments exactly those and nothing else; and on the Linux runner an
> instrumented Domain assembly reports a `netstandard` reference its source
> cannot have. It does not reproduce on Windows, where the same collector
> leaves the assembly byte-identical, so a green local suite says nothing
> about this and the runner is the only thing that can. Adding `netstandard`
> to the Domain allow-list was the one-line alternative and is the wrong one:
> it relaxes an architecture rule everywhere, for ever, and in every service
> the scaffold renders, to accommodate a test tool.

## Mutation

**Reported, not gated, and run by a person rather than by CI.** The figure
above says what the domain tests execute; a mutation run says what they
detect. `/mutation-sweep` runs every domain project at the committed `HEAD`
and files what survives, and
[its argument](commands/mutation-sweep.md) says why the runner and the
mutators are fixed as they are. One project by hand, with the same
configuration, from its test project's directory:

```bash
dotnet tool restore
cd tests/Ordering.Domain.Tests
dotnet restore
dotnet tool run dotnet-stryker --config-file ../../.claude/scripts/stryker-config.json \
    --target-framework "$(dotnet msbuild -getProperty:TargetFramework | tr -d '\r')" \
    --output ../../artifacts/mutation/Ordering.Domain
```

The report lands under `artifacts/`, which is git-ignored, as
`reports/mutation-report.json`. Its build and its test runs are the
project's own, and the configuration sets `break-on-initial-test-failure`,
so a failing build or a failing test stops it before any mutant runs.

**The project is restored, and its target framework named, before Stryker
runs.** Its analysis reads the framework from the project file alone, and
`Directory.Build.props` sets it here; left unnamed, a Windows host with
Visual Studio installed analyses with that MSBuild, which cannot resolve the
.NET SDK, and Stryker stops before it mutates anything. Its fallback
restore does not resolve an unrestored project on Windows either. The flag
takes what the project evaluates to, so the props file stays the
framework's one owner, and `mutation-run.sh` restores and names it the same
way for each project.

## What an assertion must hold

**A test that cannot fail is the costliest defect here**: it reads as
coverage while the behaviour behind it goes unverified, and from 2026-10-15
to 2026-10-23 about a third of the issues filed in this repository were
tests of that kind. Four rules close the shapes those issues share, and a
reviewer reads a new test against them.

- **Bound both sides.** A time, count, size or age is asserted between a
  lower and an upper bound, or exactly; never against a floor alone.
- **Wait on the test's own signal.** A test waits for the event it is about
  (the inbox's suppressed count, its own message's consumed signal, a
  `TaskCompletionSource` the handler completes), never for a fixed delay, a
  sentinel message, or the harness recording a fault before the code under
  test has unwound.
- **Assert the exact count.** `Assert.Equal(n, …)`, not a floor of `n`,
  unless the test says why more is correct.
- **Show that it fails.** A test added for a fix is run once against the
  unfixed code, and the pull request says it failed there.

## Where a test goes

[§12.1](backend-architecture/12-test-strategy.md)'s table is the answer and
every row names a project. Two things about it are easy to get wrong from
inside a checkout:

- **`*.TestSupport` is not a test project**
  ([§4.1](backend-architecture/04-solution-structure.md)). A service's exists
  so that two suites can share a fixture without referencing each other, and
  `Common.TestSupport` is the body those fixtures derive from
  ([ADR-056](backend-architecture/adr/ADR-056-a-services-fixture-derives-from-one-shared-body-under-tests.md)).
  None holds a `[Fact]`.
- **Ordering's handler tests live in `Ordering.Api.Tests`**, not in
  `Ordering.Application.Tests` where §12.1's row would put them: `ICurrentUser`
  is `HttpContextCurrentUser`, and a handler resolved in a bare scope has no
  principal to bind a subject from. `Ordering.Application.Tests` holds
  §12.5's saga suite instead, which needs no infrastructure at all.

## Architecture rules are tests

[§4.2](backend-architecture/04-solution-structure.md)'s dependency table is
enforced by an `ArchitectureTests` class in each service's test suites, a
worker's in its Worker suite where a host's is in its Api suite, and a
violation is a **build failure rather than a review comment**.
They read *emitted* assembly references, so a forbidden reference nothing uses
passes until code crosses it — §4.2 states that reach and what closing it would
cost. They need no container, so they are in the fast half:

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName~ArchitectureTests"
```

If a change needs one of those gates relaxed, the gate is probably right and
the design is probably wrong.

## The scaffold's suite

`tools/new-service/` renders a new service from `src/Services/Catalog` at run
time, so **changing Catalog can break the scaffold** and its tests render this
repository. They run on stdlib Python with no SDK, which means they inspect
text and never build what they render — so a Catalog test using a helper the
scaffold removes renders into a service that does not compile with every test
green. A change touching `tests/Catalog.*` is not verified until a scaffolded
service has been built; `docs/repo-map.md` carries the four commands and the
cleanup.

`tools/remove-service/` is the scaffold's inverse, and its suite is the other
half of the scaffold's table: it renders a service into a copy, removes it and
compares every file, so a shared file `new_service.py` learns to edit fails
there until `remove_service.py` reverts it. It runs without a daemon or an SDK:

```bash
cd tools/remove-service && py -3.12 -m unittest
```

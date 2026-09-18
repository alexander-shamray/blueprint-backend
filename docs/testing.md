# Testing

**What a checkout needs to know that neither §12, `Platform.slnx` nor the
workflows can tell it.**
[§12](backend-architecture/12-test-strategy.md) is the strategy — the pyramid,
the TDD cycle, what each level is for and what not to test. The solution file
is the list of test projects, and the workflows under `.github/workflows/`
are the list of everything else that runs. This file is the remainder: which
tests need a daemon and why they are never skipped, the Python floor, how to
run a gate on its own, and what the coverage figure is measured over.

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
suite writes itself, so no `gh` is needed. Its docstring is its own
inventory of what it covers.

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
can differ: `Web.Bff.Tests` is one project whose tests mostly need no
container and a few need an identity provider, so a project-level split would
have had nothing to split. What the category buys there is a container start
rather than a fast suite, because §9.7's resilience tests wait on real
timeouts either way.

**A test class that needs a container and forgets the collection fails loudly
rather than quietly**, which is the direction this has to fail in. It has no
fixture, so it does not run against one; it also carries no category, so it
runs in the fast half and fails there. What it cannot do is report a pass.

## Running a gate on its own

A gate with a suite is tested and then run, so a green suite says the gate
works and not that this checkout passes it; a gate without one is only run.
Most gate runs are the workflow step verbatim; these are the ones a step
does not show whole.

**The stage gate needs three `dotnet test` runs first**, which is
[§15.1](backend-architecture/15-cicd-deployment.md)'s `UT → IT` with the
architecture gates split off in front, for the instrumentation reason under
*Coverage* below. Three invocations rather than two, as separate steps in one
job rather than separate jobs: a job boundary would mean shipping the build
output between runners to keep `--no-build` honest, and the coverage figure
is the union of the last two, which wants one place to be merged.

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
gh pr view <n> --json number,url,body,commits,closingIssuesReferences,headRefOid |
    py -3.12 .github/closure-gate/closure_gate.py

gh api "repos/{owner}/{repo}/pulls/<n>/files" --paginate --jq '.[] | {filename, previous_filename}' |
    jq -s --argjson pr "$(gh pr view <n> --json number,body,changedFiles)" '$pr + {files: .}' |
    py -3.12 .github/locality-gate/locality_gate.py
```

The second reads the paginated files endpoint rather than
`gh pr view --json files`, which is one page; why it needs `changedFiles` and
`previous_filename` beside it is
[its README](../.github/locality-gate/README.md)'s.

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
pulled, so an image build rides on the `up`.

**`HELM=` is an override, not a gate run.** The chart gate runs
`helm dependency update` itself — `file://` dependencies resolve from disk, so
there is no network step and no chart repository — and this only tells it
where the executable is when `helm` is not on `PATH`:

```bash
HELM=/path/to/helm bash deploy/helm/smoke.sh
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

## Where a test goes

[§12.1](backend-architecture/12-test-strategy.md)'s table is the answer and
every row names a project. Two things about it are easy to get wrong from
inside a checkout:

- **`*.TestSupport` is not a test project**
  ([§4.1](backend-architecture/04-solution-structure.md)). It exists so that
  two suites can share a fixture without referencing each other, and it holds
  no `[Fact]`.
- **Ordering's handler tests live in `Ordering.Api.Tests`**, not in
  `Ordering.Application.Tests` where §12.1's row would put them: `ICurrentUser`
  is `HttpContextCurrentUser`, and a handler resolved in a bare scope has no
  principal to bind a subject from. `Ordering.Application.Tests` holds
  §12.5's saga suite instead, which needs no infrastructure at all.

## Architecture rules are tests

[§4.2](backend-architecture/04-solution-structure.md)'s dependency table is
enforced by `ArchitectureTests` in each service's Domain, Application and Api
suites, and a violation is a **build failure rather than a review comment**.
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

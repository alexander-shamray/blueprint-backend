# Repo map

**What every entry in `CLAUDE.md`'s tree is, and why it is shaped the way it
is.** `CLAUDE.md` carries a locator with one line per entry; this file is the
argument behind each line, and the master copy where the two differ in length.
Where a line here says more than the line there, that is the split working;
where the two disagree about *where a thing lives*, one of them is a bug
report against the other.

**It is load-bearing whenever a fact it records changes** — an entry added or
removed anywhere in the tree, a gate's shape, a service's project set, what a
`.TestSupport` library is for — and inert for a change that touches none of
them, which is what earns it a file rather than a permanent seat in every
session's context. **The trigger is deliberately not a directory list**: a
scope that names the directories somebody remembered is the same defect as a
gate whose selector stops matching the newest surface, and the tree below
records the root files, `src/` and `tests/` as much as `.github/`, `deploy/`
and `tools/`.

The short forms of the rules argued here — the unused-reference rule, Catalog
breaking the scaffold, the licence register, what a `.TestSupport` project is
— sit in [`change-locality.md`](change-locality.md)'s edge-case notes and
procedure, because each fires before anyone would think to open this file.
Each is argued in full below, and this file is the master copy of every one
of them.

## The tree

**One line per entry, because the inventory lives elsewhere.** What each
project *holds* is the job of §4.1 and of the project itself — a search for a
type's declaration under `src/` and `tests/` answers whether it exists; a
second copy here would be a second thing to reconcile, which `CLAUDE.md`'s one
rule forbids. This tree says where things are, not what is in them.

```
docs/backend-architecture/   the blueprint — README index, 01-purpose ..
                             15-cicd-deployment, appendix A (the ADR index),
                             B (licences), C (delivery plan), D (names the
                             solution does not declare)
docs/backend-architecture/adr/  one file per ADR, so two agents appending
                             one each meet only in Appendix A's table
docs/roadmap.md              estimates and a calendar laid over Appendix C —
                             closed
docs/pr-decision-log.md      what each PR in its range decided — closed; the
                             header names the range, and the record since is
                             commit bodies and PR bodies
docs/lessons.md              the lessons that generalise past the PR that
                             found them — closed. One level up from the log:
                             the log says what a PR decided, this says what
                             the next PR should do about it
docs/harness-boundaries.md   what the harness grants these commands and what
                             it refuses them — the deny list, the sandbox and
                             its residuals, and every grant wider than the
                             operation it buys. Read it before touching
                             anything under .claude/, and state a new residual
                             there rather than in `CLAUDE.md`
docs/change-locality.md      the operating contract — the trust order, the
                             one rule, the change classes; the paths each
                             class may reach live in classes.yml, cited
docs/change-locality-plan.md the PRs that make the contract fully true
docs/churn-plan.md           where the corpus churns, measured on a named
                             commit, and the refactor sequence that reduces
                             it
docs/repo-map.md             this file — what every entry here is and why
                             it is shaped that way. It lists itself because
                             the locator in `CLAUDE.md` does, and a map
                             missing an entry its own shorter twin carries
                             has falsified the header above rather than
                             merely gone short
docs/style-guide.md          the prose, C# and SQL dialect both artefacts
                             are written in, and which rules the build
                             enforces rather than a reviewer
docs/testing.md              what a checkout needs that §12, Platform.slnx
                             and the workflows cannot say — which tests need
                             Docker and why they are never skipped, the
                             Python floor, a gate run on its own, the
                             coverage filter. §12 keeps the strategy and
                             wins any disagreement
docs/secrets.md              how a secret reaches a pod and how each kind is
                             rotated — the operational half of §15.4 on
                             testing.md's exact terms, and §15.4 keeps the
                             inventory and wins any disagreement
docs/runbooks/               NOT one per alert: §13.8's ownership split makes
                             error rate two rules over one procedure, and
                             that one sharer is declared, with its reason, in
                             check.py's SHARED_RUNBOOKS. Plus a README that is
                             EXCLUDED from the pairing by name — one declared
                             exception, so a second non-runbook file has to
                             be argued for. check.py's check 9 reads §13.6's
                             and §13.9's tables and fails when they and the
                             directory disagree
docs/superpowers/            one frozen spec + plan per PR, written before it

global.json                  SDK pin (§4.4)
.config/dotnet-tools.json    dotnet-ef, pinned to the EF Core version —
                             `dotnet tool restore` is the whole setup
Directory.Build.props        shared MSBuild settings, ADR-019's analyser
                             policy, and §4.1's artifacts/ output location,
                             which has to be set here — the file's own Output
                             comment argues why, and what the build does to a
                             project that tries it anyway
artifacts/                   every build's output, git-ignored, in the shape
                             §4.1 draws. Not in the committed tree; listed
                             because it is the half of §4.1 that explains why
                             the walks under src/ carry no build output
Directory.Packages.props     central package management, exact pins
Platform.slnx                the solution, and the only list of its projects
coverage.runsettings         the report filtered to `.*\.Domain\.dll$` (§12.9)
.editorconfig                house style; a build input, not a hint
.github/workflows/ci.yml     every gate tested and then run, then
                             restore/build/test in three stages and the
                             coverage report — plus `scaffold-build`, which
                             renders a service and COMPILES it, because the
                             scaffold suite reads text and an SDK in that job
                             would cost every run. The job list is the file's
                             own. Unfiltered at its trigger, filtered per job
                             inside
.github/workflows/compose.yml  path-filtered smoke on deploy/compose/** —
                             config -q, up --wait, down -v, and an image build
.github/workflows/helm.yml   path-filtered smoke on deploy/helm/** — and on
                             every input smoke.sh reads outside it. **That list
                             is not written down here on purpose**: it lives
                             once, as SOURCE_INPUTS in smoke.sh beside the
                             reads, and the gate asserts both of the
                             workflow's triggers cover it. One of the
                             workflows that reaches outside its own tree
.github/workflows/observability.yml  path-filtered check on
                             deploy/observability/** — and on every input
                             check.py reads outside it, which it declares as
                             its own SOURCE_INPUTS beside the reads and
                             asserts both triggers cover
.github/workflows/deploy.yml §15.5's canary, `workflow_dispatch` ONLY — a
                             deploy on `push` would fail on every merge for
                             want of a cluster, and a pipeline red by design
                             trains everyone to ignore it. Its `check` job DOES
                             run on pull requests, and reaches outside its
                             tree: deploy/helm/**, src/** and
                             deploy/observability/**, declared as SOURCE_INPUTS
                             in canary.py. A trigger assertion cannot see a
                             read it was never told about, so a test over
                             the reads is what keeps that list complete, not
                             a more careful list
.github/workflows/closure-gate.yml  the closure gate's run, and one of the two
                             workflows with no path filter and with `edited`
                             in its triggers — what each judges is a property
                             of every pull request, and a filter could only
                             make it skippable. Its README owns why the judge
                             is read from the base commit
.github/workflows/locality-gate.yml  the other, and its own workflow rather
                             than a job in ci.yml because `edited` there would
                             rebuild the solution on every typo fix in a
                             description
.github/workflows/broker-permissions.yml  ADR-036's broker ACL, reaching
                             outside its own tree: src/Services/**,
                             Common.Contracts and Catalog.TestSupport,
                             declared as SOURCE_INPUTS in
                             check_permissions.py. Split from compose.yml
                             rather than added to it, because that smoke
                             pulls gigabytes and builds every image while
                             this gate reads text — and path filtering is
                             per-workflow, not per-job, so sharing one would
                             either drag the smoke onto every messaging
                             change or leave the gate blind to the code it
                             checks
.github/workflows/realm.yml  §11.3's token obligations on deploy/keycloak/**,
                             reaching outside its own tree for the realm
                             export that is its subject, the source the
                             lifetime a realm owes is READ from rather than
                             restated, and the rest of what the gate reads —
                             the list is SOURCE_INPUTS in realm_check.py,
                             which asserts the triggers cover every entry
                             and, in the other direction, that no path the
                             gate reads is missing from the list. Split from
                             compose.yml on broker-permissions.yml's
                             reasoning. Two jobs: `check` is the local half,
                             on the two triggers that carry a diff, and
                             `deployed` is the third moment — the rollout's
                             own derive-fetch-judge calls over every release
                             the canary plan names, hourly and on dispatch,
                             under the production Environment, opted in by
                             the repository variable REALM_CHECK_SCHEDULED
                             and filing a tracker issue when red. The realm a
                             rollout is about to land on is still judged from
                             deploy.yml; this is the moment between rollouts
.github/<gate>/              one directory per gate — licence-gate,
                             secret-scan, closure-gate, locality-gate,
                             pipeline-gate, output-gate, comment-gate —
                             holding the gate,
                             its suite and a README. The README is the ONE
                             owner of what the gate reads and what it claims,
                             and any other mention cites it rather than
                             restating it (docs/change-locality.md §2);
                             docs/testing.md carries the invocations only.
                             secret-scan is also a library, imported by the
                             scaffold
.github/coverage/            the domain-coverage reporter, laid out like a
                             gate — script, suite, README — and not one: it
                             reports and never fails on a figure
deploy/canary/               §15.5's rollout — the ladder as JSON, the weight
                             arithmetic and the promote/rollback verdict as
                             tested stdlib Python, and one file that reads
                             Prometheus. It reaches no cluster; the deciding
                             is tested and the acting is not
tools/new-service/           §4.5's scaffold — see the notes below
deploy/compose/              §14.1's model: `docker-compose.yml` is an index
                             that includes `infrastructure.yml` and one file
                             per deployable unit under `services/`, so a
                             service's environment is a file its own PR owns.
                             One application pair per service, and the gateway
                             on 5000 has no migrator beside it because the edge
                             owns no database. `rabbitmq/` is the one
                             infrastructure
                             image that is BUILT — ADR-021's delayed-exchange
                             plugin — so its build rides on the compose smoke.
                             It also carries `definitions.json`, the
                             per-service broker accounts (ADR-036), and
                             `check_permissions.py`, which derives what each
                             service may touch from that service's own source.
                             `guest` is NOT among the accounts, and the
                             mechanism matters: RabbitMQ seeds the default user
                             only on an empty database and skips it when
                             definitions are imported, so a stale volume keeps
                             it and `down -v` is what makes the removal true
deploy/helm/                 §15.3's charts. `common/` is a LIBRARY chart
                             holding every template once, and a deployable's
                             chart is values plus one-line includes. Two
                             carry a template of their own: the gateway's
                             `edge-config.yaml`, for the two keys no service
                             has, and Payments' `capabilities.yaml`, which
                             refuses the one capability its host registers
                             unconditionally. `platform/` is the umbrella.
                             `smoke.sh` renders every chart but the library,
                             which renders nothing, and asserts what comes
                             out — it reaches no cluster, and says so
deploy/observability/        §13.8's dashboards, §13.6's alert rules and
                             §13.7's k6 SLO run. TWO rule files, and the split
                             is the point: `platform-alerts.yaml` is loaded,
                             `awaiting-signal.yaml` holds the alerts whose
                             instrument nothing publishes yet and is NOT.
                             `check.py` pairs alerts with runbooks both ways,
                             and asserts the awaiting file's metrics are
                             published by NOTHING — which is what makes that
                             list self-clearing instead of a list of alerts
                             nobody ever turned on. It reaches no Prometheus
                             and does not validate rule syntax, and says so
deploy/keycloak/             §11's token obligations against a Keycloak realm
                             representation. `realm_check.py` decides,
                             `read_admin.py` is the one file that talks to
                             anything, and the suite is the mutations the
                             decision has to refuse. ADR-042's one predicate
                             has TWO subjects and they reach differently:
                             `realm.yml`'s `check` job judges §14.1's Compose
                             export and reaches only files — that export and
                             the `AccessTokenLifetime` the obligation is READ
                             out of rather than restated — while `deploy.yml`'s
                             rollout job judges the realm a deployment points
                             at, and `realm.yml`'s own `deployed` job judges
                             the realm every release points at between
                             rollouts (ADR-043). `--kind` has no default
                             because one obligation inverts between the two:
                             §11.2's password grant is on locally and off in a
                             deployed realm

src/BuildingBlocks/
  Common.Domain/               Entity<TId>, AggregateRoot<TId>, IDomainEvent
                               and friends — no packages
  Common.Application/          Result and Error; §6.2's dispatcher; all
                               of §6.3's behaviours; §6.5's CursorPage<T>;
                               §7.5's ports; and §8.5's marker port and its
                               scoped key carrier
  Common.Contracts/            §4.3's one assembly that crosses a service
                               boundary. No packages and no project references,
                               and both absences are the point — anything this
                               referenced would travel into every service
  Common.Infrastructure/       §8's Redis helpers and §8.5's durable
                               idempotency marker, §9's outbox, inbox,
                               consumers and the retention purge over all
                               three tables
  Common.Web/                  §10.4, §10.5, §10.6, §11.3, §11.4, §13.2, §13.4
                               and §13.5, and nothing else — the only building
                               block with a FrameworkReference
src/Gateway/Gateway.Api/     the edge, and the second host. One
                             ProjectReference (Common.Web), no Application and
                             no Infrastructure — §10.1 gives it no domain and
                             no database, so neither layer has anything to
                             hold. appsettings.json is the deliverable as much
                             as Program.cs is, and is under test
src/BFF/Web.Bff/             the third host, and the ONE that calls a peer
                             synchronously (§9.7, ADR-017) — which is what
                             makes it the only one holding client credentials
                             (§11.5). Same shape as the gateway
src/Services/Catalog/        §4.1's project set — Domain, Application,
                             Infrastructure, Migrator, Api. The first real
                             service, the scaffold's template, and the
                             platform's one gRPC server
src/Services/Ordering/       the same set, rendered by the scaffold rather
                             than written, then given §5's Order aggregate,
                             §6.6's price projection with its withdrawal
                             watermark behind the solution's first receive
                             endpoint, and §9.6's saga with its receive
                             endpoints and the solution's only MassTransit EF
                             persistence reference
tests/                       per service: .Domain.Tests, .Application.Tests,
                             .Api.Tests and .TestSupport — the last is NOT a
                             test project (§4.1). Per building block with
                             behaviour to test, one suite. Per host, one
                             suite, and the BFF's has a .TestSupport of its
                             own. Plus Platform.IntegrationTests, the only
                             suite that references every service (§4.1),
                             where §12.1 homes Common.Contracts' shape tests
```

**A `.TestSupport` project is not a test project (§4.1), and it exists for a
reason rather than by convention.** The reason is two suites sharing a fixture
and unable to reference each other — which is why `Catalog.TestSupport` exists
for Catalog's second consumer rather than for the service.

**That is why the project type exists; it is not when a service gets one.**
The scaffold emits the library with the service (§4.5), so
`Ordering.TestSupport` arrived with Ordering and has one consumer,
`Ordering.Api.Tests`. `Ordering.Application.Tests` deliberately does not
reference it and says so in its csproj, because §12.1 homes handler tests at
that level and Ordering's live in the API suite instead. A **host** is the case
that gets none, so `Gateway.Api.Tests` carries its own `TestAuthHandler`
as a **copy of Catalog's, deliberately**, as every suite that needs one does:
§4.3 permits exactly one assembly to cross a service boundary and a test
helper is not it.
`Web.Bff.TestSupport` is the exception that proves the shape — one BFF suite,
but `StubCatalog` must compile the *server* half of a `.proto` whose *client*
half `Web.Bff` already compiles, and both in one assembly makes every message
type a CS0436, which ADR-019 turns into an error.

**The other thing shared as a linked file rather than as an assembly is the
same relationship one level apart.** `pricing.proto` is Catalog's, because
Catalog serves the RPC; `PricingContract.cs` is Web.Bff's, because only a
consumer can say what it needs — and `Catalog.Api.Tests` compiles it through
a `<Compile Link>` so the provider can be held to it. A file crosses no
boundary §4.3 draws, which is what keeps a test helper from becoming the
second assembly that does. **A third copy of this pattern owes the scaffold
an entry**: `tools/new-service` drops both the link and the suite that uses
it, because a contract copied to a service no consumer calls is an
expectation nobody holds.

Planned, per §4.1 — do not invent a different shape for it. `src/Services/`
gains Inventory and Payments with the same project set, Shipping with a
Worker in place of the Api, and Notifications with one fewer (no Domain, and
a Worker). `deploy/` still owes `k8s/`, and `helm/` and `k8s/` are not
alternatives: §4.1 gives `k8s/` the raw manifests "where Helm is overkill",
which is a decision no chapter has yet taken about any particular object.

Three things sit outside that tree because §4.1 does not draw them:
`global.json`, whose SDK pin §4.1's prose relies on for the `.slnx` floor;
`.config/dotnet-tools.json`, which pins `dotnet-ef` to the EF Core version,
because a tool a major behind the runtime fails with an error that names
neither; and `src/AppHost`, the optional Aspire host of §14.2. **Aspire is not
adopted** — Compose is the baseline (§14.1), nothing references an `Aspire.*`
package, and §4.4 therefore pins none. If it is adopted, `src/AppHost` is the
only project taking `Aspire.Hosting.*`, but each service picks up the client
integrations for the resources it consumes — so backing it out again costs a
line per resource per service, not one deletion (§14.2).

## Edges between building blocks

Three exist, and every one waited for a type that could not be written without
it. **An unused project reference is a claim about the dependency graph that
nothing makes true**, which is why each was drawn late and deliberately:

- `Common.Application → Common.Domain`. §4.2 permitted it from the start;
  what it lacked was a member naming a domain type. §7.5's
  `IDomainEventCollector` returns `IReadOnlyList<IDomainEvent>` and settled it.
- `Common.Infrastructure → Common.Application`, `Common.Domain` and
  `Common.Contracts`, all three drawn by the outbox — `MessageTypeMap`
  selects on `IDomainEvent` **or** `IIntegrationEvent`, which is why the last
  two arrive together.
- `Common.Web → Common.Application`, the oldest, because `ToHttpResult` maps an
  `Error` and cannot be written without one.

**The reference existing is not permission to start using it.** §6.3's
`TransactionBehavior` still reads `ModifiedAggregateCount` as an `int`, because
the `is IAggregateRoot` test it derives from lives in `EfUnitOfWork`, on
Infrastructure's side of §4.2 — counting behind the port is what keeps it
there. `IUnitOfWork.ExecuteRawAsync` takes `string` and `object` for the same
reason.

## Files outside the blueprint tree, and why

- **The licence gate** lives under `.github/` rather than a `build/` directory
  because it is CI-only and §4.1 draws no such tree. **Adding a package means
  adding its backticked identity to Appendix B in the same change**, or the
  gate fails the build before anything compiles; its README says what else it
  reads and refuses.
- **The secret scan** sits beside it under `.github/` on the same argument and
  runs in the same job, first — §15.1 draws "SCA + secret scan" as one node.
  Its README owns what it can and cannot find, and the README in `allowed/`
  owns what an accepted finding may say.
- **`docs/roadmap.md`** is a schedule, not a specification, and goes stale on a
  different clock. Nothing in it states a requirement. **Where it and Appendix C
  disagree, Appendix C wins**, always. Being outside the tree, no nav footer or
  index row catches its drift — `/validate-blueprint` check 10 is the only
  thing that does, which is why it is named in that command's scope.
- **`docs/pr-decision-log.md`** is beside the roadmap for the same reason —
  outside the blueprint tree, so in no index and behind no nav footer — and
  one step further: the roadmap at least has check 10, where the log is in
  the scope of **neither** `/validate-blueprint` nor `/check-links`. The one
  rule in `CLAUDE.md` is the only thing carrying it.
- **`docs/lessons.md` and `docs/harness-boundaries.md`** are the log's
  position exactly: in the scope of neither command, so the one rule carries
  them too. **Their subjects differ in what falsifies them**, which is the
  thing to know before editing either: a lesson is falsified by a
  *measurement*, so it is amended when the code it describes changes and never
  because it reads oddly; a harness boundary is falsified by *running the
  offending form*, and every entry in that file was found that way rather
  than by reasoning. Neither takes a correction argued from the file alone.
- **`docs/repo-map.md` and `docs/style-guide.md`** are that position again,
  so **this file is one of them** and the rule above is not hypothetical
  here. What falsifies them differs once more: a map entry is falsified by
  the tree it describes — read the directory, not the paragraph — and a style
  rule is falsified by the corpus, which is why `/style-pass` changes every
  site before it changes the rule.
- **`docs/testing.md`** is outside the tree on the same terms and lands
  between the two: named in `/validate-blueprint`'s scope like the roadmap,
  reached by no link check like the log. It needs **no check of its own** —
  it is the operational remainder of §12, so every claim in it is a claim
  about a chapter or about the code, and checks 1–9 reach all of them. **§12
  wins where they disagree**, exactly as Appendix C wins over the roadmap.
  The split is deliberate: a runner flag goes stale on a different clock than
  a strategy does, and a chapter that carried both would be edited for the
  wrong half. What the solution file and the workflows can say, it does not
  restate: `Platform.slnx` is the list of test projects and the workflows
  are the list of every other suite, so adding either needs no edit to it.
- **`docs/superpowers/`** is a **frozen historical record**. Each pair — a
  design spec and the plan derived from it — records how one PR was thought
  through *before* it was built. **Where one disagrees with the blueprint, the
  blueprint wins**, and the disagreement is not a defect: it is the record
  showing where the design moved during implementation. So these are
  **outside `/validate-blueprint`'s scope** and, unlike the roadmap, not named
  in it either — a drift check on a document whose whole value is being stale
  would fail on every entry by design. **Do not edit a spec or a plan to match
  the code that followed it**; amend the chapter instead.
- **`.remember/`** is session state, not content. Never edit it as part of a
  change.

## The scaffold

`tools/new-service/` took the opposite decision to the licence gate's, and the
difference is what each thing is. The gate is CI-only, so it lives under the CI
provider's directory. The scaffold is a developer tool that happens to be
tested in CI, so filing it under `.github/` would file it by its least
important property — **§4.1's tree gained a `tools/` entry and §4.5 documents
the script instead**, the honest fix for "the blueprint draws no such tree"
being to draw it.

**Catalog is the template, read at run time.** There is no template directory,
so there is one copy of the wiring rather than two that drift — and the
scaffold's tests render *this* repository.

**Changing Catalog can therefore break the scaffold, and the failure is loud.**
The script names exact text inside `src/Services/Catalog` and `tests/Catalog.*`
and every anchor must match exactly once. It also classifies **every** file
under those roots as template or slice and refuses to run on one it has never
seen — so a new file in Catalog is a decision the scaffold forces. If
`py -3.12 -m unittest` in `tools/new-service` goes red after a Catalog change,
reconcile the script in the same change.

**One class of breakage is silent, and only compiling the output catches it.**
The suite runs on stdlib Python with no SDK, so it renders a service and
inspects the text — it never builds one. A Catalog test using a helper the
scaffold *removes* renders into a service that does not compile with every test
green. **A change touching `tests/Catalog.*` is not verified until a scaffolded
service has been built**, which is four commands and a cleanup.

**CI does this too, in a `scaffold-build` job of its own**, so the class is
caught on the pull request rather than in six months. That does not retire the
block below: the job renders `Yankee` and builds it after the fact, where
running these four locally is how you find out before pushing — and the
cleanup half exists only here, because the runner discards its checkout.

```bash
python tools/new-service/new_service.py Yankee --port 5199
dotnet build tests/Yankee.Api.Tests/Yankee.Api.Tests.csproj
rm -rf src/Services/Yankee tests/Yankee.* deploy/compose/services/yankee.yml
git checkout -- Platform.slnx deploy/compose/ .github/secret-scan/allowed/
```

**That `git checkout` reverts uncommitted work in `deploy/compose/` and in the
allow-list, including work you did *during* the dogfood.** A fix made because
the render exposed something — the case that actually happens — is in that
tree and is reverted by the cleanup, leaving a commit whose message describes
it and whose diff does not.

**Commit the fix before the cleanup; do not copy the file aside and restore
it.** Copying back is the obvious repair and it is wrong, because by then the
file also holds what the scaffold wrote — restoring it returns the
`| Yankee API |` row along with the fix, and that is how probe output reaches a
commit. `git add -p` the intentional hunks and commit them, then run the
`git checkout` against what is left.

**A rendered service carries credential-shaped literals, and the scaffold
writes their allow-list entries itself.** §15.1's secret scan reads the
working tree, so a render with no `.github/secret-scan/allowed/`
entries beside it could not be committed at all. The scaffold **loads the
real scanner**, runs it over what it has just rendered, and appends one
`path | rule | fingerprint | reason` line per distinct finding. The
fingerprints are the gate's own: a scaffold computing them would be a second
implementation of which substring each rule matches, and a fingerprint
matching nothing is a stale entry the scanner fails the build on. **It writes
nothing where `.github/` is absent**, which is the scaffold suite's synthetic
root — a degraded path that suite therefore cannot assert — and refuses a
root that has `.github/` but no scanner in it.

The scaffold edits tracked files as well as creating its own — the solution
file, the compose tree and the allow-list, which is why the `git checkout`
above names all three — so that cleanup is part of the procedure rather than
tidying after it. **Commit before dogfooding**, though, if the PR itself
changes `deploy/compose/` or that allow-list — the cleanup reverts the tree's
own changes.

**The probe is `Yankee` at 5199, and must not be a real service's name.** The
create refuses a taken name and port, and the `rm -rf`, followed literally
against a real service's name, deletes the service. `Yankee` is one of the
probes the scaffold's own suite uses, chosen because a probe cannot quietly
become a service later.

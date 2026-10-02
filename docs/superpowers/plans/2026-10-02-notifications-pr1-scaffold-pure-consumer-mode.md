# Notifications PR-1 — sixth service from the scaffold's pure-consumer mode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `tools/new-service` the pure-consumer mode §4.5 says is owed,
take `Notifications` off the scaffold's refusal and onto that mode, tell every
gate that reads a service's shape about a service with no Domain project and no
outbox — by a selector read from the tree, never by the service's name — render
`src/Services/Notifications` with the mode, strip its Redis, and land the
`Notification` record with its state rules in `Notifications.Application`, its
`NotificationLog` table and the `AddNotificationLog` migration — plus the
Compose pair, the narrow broker account `notifications-svc`, and CI's filter,
outputs, matrix legs and the `images` job's `if:`.

**Architecture:** the mode is a set of omissions from the one template rather
than a second template. `Names` gains a `pure_consumer` flag; `render.py`
leaves out a declared list of template files (`PURE_CONSUMER_OMITTED`), the two
outbox migrations by shape, the outbox entity from every remaining designer
and so from the snapshot, and the `AssemblyMarker`; it writes one file the
template does not carry, a `NoDomainEventDispatcher` in the Application
project, because §6.3's `TransactionBehavior` still needs a dispatcher and
§7.5's needs the collector, mapper and publisher this shape lacks. Two new
tables in `patch.py` carry the rest: `PURE_CONSUMER_PATCHES` (anchored
replacements, as `PATCHES` are) and `PURE_CONSUMER_SPANS` (a cut from one
anchor through another, both bound exactly once, so a whole outbox test leaves
without being quoted line by line). The broker account the render writes is a
consumer's, not the template publisher's. Two building-block facts the spec
does not name are fixed first: `RetentionPurgeService` composes no outbox
statement for a service that registers no `OutboxTable`, and the template's
Compose unit comes under the comment budget, because a unit the render creates
is all added lines and CI judges every block in it. The gates are told by one
selector, "the service has a `<Name>.Domain` project", in
`deploy/observability/check.py` and `deploy/compose/rabbitmq/check_permissions.py`,
and the scaffold's suite asserts each gate's own selector over the render.

**Tech Stack:** .NET at `global.json`'s pin, EF Core with SQL Server,
MassTransit (registered, no consumer yet), xUnit v3 with Shouldly and
Testcontainers, stdlib Python 3.12 for the scaffold and the gates.

**Spec:** `docs/superpowers/specs/2026-10-02-notifications-service-design.md`,
sections 1 (the Redis and outbox answers), 2 (the mode, the gates by selector,
§2's and §4.5's sentences), 3 (PR-1's row, and why CI joins and Helm does not),
5 (the record and its table), 6 (`NotificationLog`, schema, the first
migration), 10 (the broker account), 11 (PR-1's keys, no published port), 13
(the Application suite and the scaffold's suite) and 14 (§2 and §4.5 move in
PR-1).

**Proven before it was written.** Every code block below was run in a scratch
copy of this tree: the mode rendered `Zulu` and `Notifications`, the whole
solution built with 0 warnings, the pure render's suites passed (16 and 61,
37 of them against containers), Notifications' suites passed after Tasks 6,
8 and 9 (36 and 65), `dotnet ef migrations add AddNotificationLog` emitted one
`CreateTable` and a snapshot diff that only adds the entity, both images built
with `docker build`, `docker compose config` resolved the unit, and the comment
gate's `judge` found nothing over every added line. Where an anchor below
quotes rendered text, it is the text that render produced.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs`, `tests/Common.Infrastructure.Tests/**`, `tests/Common.TestSupport/ServiceFixture.cs`, `src/Services/Notifications/**`, `tests/Notifications.*`, `tools/new-service/**`, `deploy/compose/**`, `deploy/observability/check.py`, `deploy/observability/README.md`, `.github/secret-scan/allowed/**`, `.github/workflows/ci.yml`, `Platform.slnx`, `docs/backend-architecture/02-architecture-at-a-glance.md`, `docs/backend-architecture/04-solution-structure.md`, `docs/backend-architecture/07-persistence.md`, `docs/backend-architecture/09-messaging.md`
- Reasons, since the row above is paths only. A is one building block
  (`Common.Infrastructure`, its suite and the shared fixture's two calls into
  it) and the Notifications slice; D is the scaffold, the Compose model, the
  two gates, the workflow and the four chapters; E is `Platform.slnx` and the
  `*.csproj` files the render writes, which sit inside `src/Services/Notifications/**`
  and `tests/Notifications.*` already. **One building block, not two**: the
  contract's §3 makes crossing two Class B, which `A+D+E` cannot carry, so the
  pure consumer's dispatcher is a file the render writes into the service
  rather than a helper in `Common.Application`. The repo-wide mutexes this PR
  holds are `Platform.slnx`, `deploy/compose/docker-compose.yml`,
  `deploy/compose/docker-compose.infra-only.yml` and
  `tests/Common.TestSupport/ServiceFixture.cs`; the per-service ones are all
  Notifications', which nothing else holds.
- Depends on nothing earlier: this is Notifications' first PR. PR-2 to PR-5
  consume the names Task 8's and Task 9's Interfaces list.
- No new package: no `Directory.Packages.props` change and no Appendix B row.
- **The render commit is the scaffold's output and nothing else.**
  `tools/new-service/README.md`'s *A scaffold PR is the scaffold's output*
  says the scaffold is reviewed in its own pull request; this PR carries both,
  as Shipping's PR-1 did, so the commits are cut along that line: every
  scaffold and gate change lands before Task 5, Task 5's commit is the render
  untouched, and Task 11 proves it with `--verify`. The render needs no
  `known-differences.txt` line, because a pure consumer has no outbox meter
  and so no hand edit to §13.2's `Required` list.
- Notifications reaches **no** Redis key and registers no
  `IConnectionMultiplexer` (spec, section 1). A rendered line that exists only
  to feed Redis is cut, not commented out.
- Comments say why and cite the owner; no history, no PR, no test named. The
  comment gate's `BLOCK_LIMIT` is 5, a touched block counts whole, and a file
  the branch creates is all added lines — the render included. Prose at 80
  columns, code at 120, British spelling, explicit local types, file-scoped
  namespaces, braces on two statements or more, one space before `=`, `=>`
  and `{`.
- `py -3.12`, never `python`. Container tests are
  `[Collection(nameof(IntegrationCollection))]` and never skipped.
- Every step that adds behaviour writes its test first, and a gate's test has
  the gate's selector as its subject.

---

### Task 1: The purge composes no outbox statement for a service with no outbox

**Files:**
- Modify: `src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs`
- Modify: `tests/Common.TestSupport/ServiceFixture.cs` — the two direct
  constructions
- Create: `tests/Common.Infrastructure.Tests/RetentionPurgeServiceTests.cs`
- Modify: `docs/backend-architecture/09-messaging.md` — §9.5, one sentence

**Interfaces:**
- Changes: `RetentionPurgeService(IServiceScopeFactory scopes, InboxTable inbox,
  IdempotencyMarkerTable markers, IIdempotencyStore claims, RetentionPolicy
  policy, ILogger<RetentionPurgeService> log, OutboxTable? outbox = null)`.
  The outbox moves last because only a trailing parameter can be optional, and
  the container resolves an unregistered optional parameter to its default.
  `PurgeAsync` keeps its `(int Outbox, int Inbox, int Idempotency)` and reports
  `Outbox: 0` when there is no table.

The spec strips the outbox table from a pure consumer and keeps the purge; the
purge as it stands takes `OutboxTable` unconditionally and deletes from it every
pass, so a host with no table would fail its own `ValidateOnBuild` — or, given
one, log a failed delete every hour. §9.5 already says the service "composes
the statements each registered table needs", so this makes the code say what
the chapter does.

- [ ] **Step 1: Write the failing test**

`tests/Common.Infrastructure.Tests/RetentionPurgeServiceTests.cs`:

```csharp
using Common.Application;
using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>§9.5's purge composes a statement per registered table, so a service with no outbox resolves it.</summary>
public class RetentionPurgeServiceTests
{
    [Fact]
    public void It_resolves_for_a_service_that_registers_no_outbox_table()
    {
        // ValidateOnBuild, as every host builds: the outbox is the one table a pure consumer does not have (§4.1).
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(new InboxTable("probe"));
        services.AddSingleton(new IdempotencyMarkerTable("probe"));
        services.AddSingleton(Substitute.For<IIdempotencyStore>());
        services.AddSingleton(new RetentionPolicy());
        services.AddSingleton<RetentionPurgeService>();

        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        provider.GetRequiredService<RetentionPurgeService>().ShouldNotBeNull();
    }
}
```

`NSubstitute` is already a reference of that suite; `AddLogging` comes with
`Microsoft.Extensions.Logging`, which `Common.Infrastructure` carries.

- [ ] **Step 2: Run it to see it fail**

```bash
dotnet test tests/Common.Infrastructure.Tests --filter "FullyQualifiedName~RetentionPurgeServiceTests"
```

Expected: FAIL — `AggregateException` from `BuildServiceProvider`, naming
`OutboxTable` as unresolvable for `RetentionPurgeService`.

- [ ] **Step 3: Make the outbox optional**

In `RetentionPurgeService.cs`, the field. Before:

```csharp
    private readonly string _outboxSql;
```

After:

```csharp
    // Null for a service that registers no OutboxTable, which §4.1's pure consumer does not (§9.5).
    private readonly string? _outboxSql;
```

The constructor's signature. Before:

```csharp
    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        OutboxTable outbox,
        InboxTable inbox,
        IdempotencyMarkerTable markers,
        IIdempotencyStore claims,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log)
    {
```

After:

```csharp
    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        InboxTable inbox,
        IdempotencyMarkerTable markers,
        IIdempotencyStore claims,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log,
        OutboxTable? outbox = null)
    {
```

The statement. Before:

```csharp
        // ProcessedAt IS NOT NULL keeps the abandoned rows §13.6's alert surfaces.
        _outboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {outbox.QualifiedName}
            WHERE ProcessedAt IS NOT NULL
                AND ProcessedAt < @Before;
            """;
```

After:

```csharp
        // ProcessedAt IS NOT NULL keeps the abandoned rows §13.6's alert surfaces.
        _outboxSql = outbox is null
            ? null
            : $"""
              DELETE TOP (@BatchSize) FROM {outbox.QualifiedName}
              WHERE ProcessedAt IS NOT NULL
                  AND ProcessedAt < @Before;
              """;
```

The raw literal's content lines sit at the closing `"""`'s fourteen spaces,
which is what the compiler strips.

The pass. Before:

```csharp
        int outbox = await DeleteAsync(
            connection,
            _outboxSql,
            new { _policy.BatchSize, Before = now - _policy.OutboxWindow },
            ct);
        Purged(_log, outbox, "outbox", null);
```

After:

```csharp
        int outbox = 0;
        if (_outboxSql is not null)
        {
            outbox = await DeleteAsync(
                connection,
                _outboxSql,
                new { _policy.BatchSize, Before = now - _policy.OutboxWindow },
                ct);
            Purged(_log, outbox, "outbox", null);
        }
```

No log line for a table that does not exist: `Purged` says how many rows went
from a table, and "0 from outbox" on a service with none reads as a table
nobody purges.

- [ ] **Step 4: The shared fixture's two constructions**

`tests/Common.TestSupport/ServiceFixture.cs`, in `PurgeWithAsync(RetentionPolicy,
IIdempotencyStore)`. Before:

```csharp
        RetentionPurgeService purge = new(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<OutboxTable>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            claims,
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>());
```

After:

```csharp
        RetentionPurgeService purge = new(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            claims,
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>(),
            Factory.Services.GetService<OutboxTable>());
```

And in `PurgeWithSkewedClockAsync`. Before:

```csharp
            Factory.Services.GetRequiredService<OutboxTable>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            Factory.Services.GetRequiredService<IIdempotencyStore>(),
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>());
```

After:

```csharp
            Factory.Services.GetRequiredService<InboxTable>(),
            Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            Factory.Services.GetRequiredService<IIdempotencyStore>(),
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>(),
            Factory.Services.GetService<OutboxTable>());
```

`GetService`, not `GetRequiredService`: the fixture passes on whatever the host
registered, so every existing service's pass is unchanged and a pure consumer's
has no outbox.

- [ ] **Step 5: §9.5's sentence**

`docs/backend-architecture/09-messaging.md`, the paragraph that introduces the
service. Before:

> `RetentionPurgeService` in `Common.Infrastructure.Messaging` is that service:
> it composes the statements each registered table needs, takes its windows and
> its batch size from a registered `RetentionPolicy`, and exposes `PurgeAsync`
> publicly so tests drive one pass rather than racing a timer — the seam
> `OutboxDispatcher.ProcessBatchAsync` already offers, for the same reason.

After:

> `RetentionPurgeService` in `Common.Infrastructure.Messaging` is that service:
> it composes the statements each registered table needs, takes its windows and
> its batch size from a registered `RetentionPolicy`, and exposes `PurgeAsync`
> publicly so tests drive one pass rather than racing a timer — the seam
> `OutboxDispatcher.ProcessBatchAsync` already offers, for the same reason. A
> service that registers no `OutboxTable` — §4.1's pure consumer, which
> publishes nothing — is given no outbox statement, and its pass reports no
> outbox rows rather than failing on a table it never created.

- [ ] **Step 6: Run the building block's suite and one service's purge**

```bash
dotnet build Platform.slnx
dotnet test tests/Common.Infrastructure.Tests --filter "FullyQualifiedName~RetentionPurgeServiceTests"
dotnet test tests/Catalog.Api.Tests --filter "FullyQualifiedName~RetentionPurgeTests"
```

Expected: 0 warnings; the new test green; Catalog's fifteen purge tests green
unchanged, which is the evidence that a service with an outbox still purges
it. Docker is needed for the last.

- [ ] **Step 7: Commit**

```bash
git add src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs \
        tests/Common.Infrastructure.Tests/RetentionPurgeServiceTests.cs \
        tests/Common.TestSupport/ServiceFixture.cs docs/backend-architecture/09-messaging.md
git commit -m "feat(common): RetentionPurgeService composes no outbox statement for a service with no OutboxTable"
```

The body says the purge took the outbox unconditionally while §9.5 said it
composed a statement per registered table, that a pure consumer has no outbox
table, and that the parameter moved last because only a trailing one can be
optional.

---

### Task 2: The template's Compose unit comes under the comment budget

**Files:**
- Modify: `deploy/compose/services/catalog.yml` — three comment blocks
- Modify: `tools/new-service/test_new_service.py` — one test in
  `RendersInsideTheCommentBudget`

The render *creates* the service's Compose unit from this file, so the pull
request that lands a rendered service adds every line of it, and the comment
gate judges every block whole. Three blocks here run ten lines against
`BLOCK_LIMIT`'s five: the bus key's, the two Redis keys' and the Redis
`depends_on` entries'. The suite's `budget_breaches` cannot see them, because it
judges a created file only where it differs from its template — CI judges it
whole. Without this task the Notifications render fails the comment gate on
the bus block, and the next API render on all three.

- [ ] **Step 1: Write the failing test**

In `RendersInsideTheCommentBudget`, after `test_a_worker_render`:

```python
    def test_the_compose_unit_a_render_creates_is_inside_the_budget_whole(self):
        # Created rather than spliced, so the pull request that adds it is every
        # line of it: the gate judges the template's blocks in the copy too.
        gate = comment_gate_module()
        for rendered in (render(), worker()):
            unit = rendered.created[UNIT].replace("\r\n", "\n")
            lines = gate.scan(UNIT, unit)
            every = {line.number for line in lines}
            self.assertEqual([], [message for _, _, message in gate.findings(UNIT, lines, every)])
```

- [ ] **Step 2: Run it to see it fail**

```bash
cd tools/new-service && py -3.12 -m unittest test_new_service.RendersInsideTheCommentBudget
```

Expected: FAIL — three `a comment block runs 10 lines, over 5` for the API
render.

- [ ] **Step 3: Cut the three blocks**

`deploy/compose/services/catalog.yml`, the bus key's block. Before:

```yaml
      # The bus (§9), read by AddMassTransitMessaging — which throws without
      # it, so this line is what lets the host start. A plain value, no ${…}
      # variable: this is §14.1's local default, not a secret with a
      # production shape, and .env.example's contract covers variables.
      #
      # `catalog-svc` rather than `guest`: that account is tagged
      # `administrator` and the base image ships `loopback_users.guest =
      # false`, so one principal reachable from any container could publish
      # onto `ordering-commands` as system-initiated (§9.4). The broker's
      # per-service write permissions live in rabbitmq/Dockerfile.
```

After:

```yaml
      # The bus (§9). AddMassTransitMessaging throws without it, so this line is
      # what lets the host start. A plain value, §14.1's local default rather
      # than a secret, under this service's own account rather than `guest`,
      # which is tagged administrator and reachable from any container (ADR-036).
```

The Redis keys' block. Before:

```yaml
      # §8.1's two connections. Both keys are required whether or not a
      # service reads both: AddRedisConnections is one call by design (§8.2),
      # so a service either has Redis or does not, and it reads both eagerly
      # and throws naming the missing one — which is what makes an absent key
      # a host that will not start rather than a cache silently reading the
      # database. §8.5's IdempotencyBehavior reads the coordination one,
      # claiming a {service}:idem: key before any command runs.
      # 6379 on both: the ports differ only on the host side (6379/6380),
      # and inside the network each container listens on Redis's own port,
      # so naming 6380 here would reach nothing.
```

After:

```yaml
      # §8.1's two connections, both read eagerly by AddRedisConnections (§8.2),
      # so an absent key is a host that will not start rather than a cache
      # silently reading the database. 6379 on both: each container listens on
      # Redis's own port, and 6380 exists only on the host side.
```

The Redis `depends_on` block. Before:

```yaml
      # Both Redis instances, and the coordination one is load-bearing since
      # §8.5's behaviour claims a key before any command runs. Ordered after
      # keycloak only because this list reads top-down; compose treats every
      # entry alike.
      #
      # service_healthy, not service_started: AddRedisConnections sets
      # AbortOnConnectFail = false (§8.1's "degrade, don't die"), so the host
      # WOULD start against a Redis still booting — and the first protected
      # command would then fail on a claim rather than on anything a reader
      # could connect to a missing container.
```

After:

```yaml
      # service_healthy, not service_started: AddRedisConnections sets
      # AbortOnConnectFail = false (§8.1), so the host would start against a
      # Redis still booting and its first command would fail on §8.5's claim.
```

Only comments move: no value, key or `depends_on` entry changes, so
`SCAN_REASONS`' markers — values, not comments — still bind, and Catalog's own
allow-list entries are fingerprints of values that did not move. The other
services' units carry the same long blocks and are not touched: a block this
branch neither adds nor edits is not one the gate judges.

- [ ] **Step 4: Run the suite and the scan**

```bash
cd tools/new-service && py -3.12 -m unittest
cd ../.. && py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: green, apart from `VerifiesAScaffoldCommit` only on a shallow clone;
the scan reports 0 unexplained.

- [ ] **Step 5: Commit**

```bash
git add deploy/compose/services/catalog.yml tools/new-service/test_new_service.py
git commit -m "fix(compose): the template unit's three long comment blocks come under the gate's budget"
```

The body says the unit is created by every render, so a rendered service's pull
request is every line of it, and the suite judged it only where it differed
from the template.

---

### Task 3: The pure-consumer mode

**Files:**
- Modify: `tools/new-service/scaffold/__init__.py` — `Names.pure_consumer`
- Modify: `tools/new-service/new_service.py` — the flag, the refusals, the
  project list, the meter line
- Modify: `tools/new-service/scaffold/render.py` — the omissions, the spans,
  the outbox entity, the dispatcher, the solution, the broker grant
- Modify: `tools/new-service/scaffold/patch.py` — `PURE_CONSUMER_PATCHES`,
  `PURE_CONSUMER_SPANS`
- Modify: `tools/new-service/scaffold/reproduce.py` — `--verify` reads the shape
- Modify: `tools/new-service/test_new_service.py`
- Modify: `tools/new-service/README.md`
- Modify: `docs/backend-architecture/04-solution-structure.md` — §4.5
- Modify: `docs/backend-architecture/07-persistence.md` — §7.5, one paragraph

**Interfaces:**
- Produces: `Names(pascal, host=API_HOST, pure_consumer=False)`;
  `plan(repo_root, name, port, migration_id, host=API_HOST, pure_consumer=False)`;
  `project_suffixes(host, pure_consumer=False)`;
  `new_service.PURE_CONSUMER_ONLY_SERVICES`; `scaffold.render.PURE_CONSUMER_OMITTED`,
  `PURE_CONSUMER_MIGRATIONS`, `NO_DOMAIN_EVENT_DISPATCHER`,
  `replace_span(text, first, last, replacement, where)`,
  `pure_consumer_omits(relative)`, `without_outbox_entity(designer, where)`;
  `scaffold.patch.PURE_CONSUMER_PATCHES`, `PURE_CONSUMER_SPANS`;
  `Scaffolded.pure_consumer`; the command-line flag `--pure-consumer`.
- Removes: `new_service.UNRENDERABLE_SERVICES`.

**What the mode renders, measured.** Fifty-two created files and seven
updated against a real checkout — seven projects (no `<Name>.Domain`, no
`<Name>.Domain.Tests`), five migrations and their designers and a snapshot
describing the inbox and the marker table alone, the Application project's
`NoDomainEventDispatcher.cs` in place of the Domain project's
`AssemblyMarker.cs`, no outbox meter line, and a broker grant that writes its
own endpoints and the fault exchanges and no contract.

- [ ] **Step 1: Write the failing tests**

In `tools/new-service/test_new_service.py`, after `EveryGateSeesTheWorkerRender`
and before `RefusesToRun`:

```python
def pure_consumer(name: str = PROBE, repo_root: Path = REPO_ROOT) -> Plan:
    return plan(repo_root, name, None, MIGRATION_ID, host=new_service.WORKER_HOST, pure_consumer=True)


class RendersAPureConsumer(unittest.TestCase):
    """§4.1's third shape: a Worker with no Domain project, no outbox and nothing to publish."""

    @classmethod
    def setUpClass(cls):
        cls.rendered = pure_consumer()
        cls.prefix = f"src/Services/{PROBE}/{PROBE}.Infrastructure/Persistence/Migrations"

    def created(self, path: str) -> str:
        return self.rendered.created[path].replace("\r\n", "\n")

    def test_it_writes_seven_projects_and_no_domain(self):
        projects = sorted(p for p in self.rendered.created if p.endswith(".csproj"))
        self.assertEqual(
            [
                f"src/Services/{PROBE}/{PROBE}.Application/{PROBE}.Application.csproj",
                f"src/Services/{PROBE}/{PROBE}.Infrastructure/{PROBE}.Infrastructure.csproj",
                f"src/Services/{PROBE}/{PROBE}.Migrator/{PROBE}.Migrator.csproj",
                f"src/Services/{PROBE}/{PROBE}.Worker/{PROBE}.Worker.csproj",
                f"tests/{PROBE}.Application.Tests/{PROBE}.Application.Tests.csproj",
                f"tests/{PROBE}.TestSupport/{PROBE}.TestSupport.csproj",
                f"tests/{PROBE}.Worker.Tests/{PROBE}.Worker.Tests.csproj",
            ],
            projects)
        for path, text in self.rendered.created.items():
            self.assertNotIn(f"{PROBE}.Domain", path)
            self.assertNotIn(f"{PROBE}.Domain", text, path)

    def test_nothing_rendered_names_the_outbox_the_mapper_or_the_collector(self):
        # Type names rather than the word, which a comment may use to say there is none,
        # and the service's code rather than its suites, which name one to assert its absence.
        for path, text in ((p, t) for p, t in self.rendered.created.items() if p.startswith("src/")):
            for name in ("OutboxDispatcher", "OutboxPublisher", "OutboxMessage", "OutboxMetrics", "OutboxTable",
                         "OutboxJson", "MessageTypeMap", "MessageTypeSource", "IntegrationEventMapper",
                         "DomainEventCollector", "AssemblyMarker", "AddDomainEventDispatcher"):
                self.assertNotIn(name, text, f"{path} names {name}")

    def test_it_keeps_the_inbox_the_purge_the_migrator_the_probes_and_the_bus(self):
        infrastructure = self.created(f"src/Services/{PROBE}/{PROBE}.Infrastructure/DependencyInjection.cs")
        self.assertIn("services.AddSingleton(new InboxTable(schema));", infrastructure)
        self.assertIn("services.AddSingleton(new IdempotencyMarkerTable(schema));", infrastructure)
        self.assertIn("services.AddHostedService<RetentionPurgeService>();", infrastructure)
        self.assertIn("services.AddHostedService<MetricsInitialiser>();", infrastructure)
        self.assertIn("services.AddMassTransitMessaging(configuration);", infrastructure)
        program = self.created(f"src/Services/{PROBE}/{PROBE}.Worker/Program.cs")
        self.assertIn("app.MapCommonHealthEndpoints();", program)
        self.assertIn(f"src/Services/{PROBE}/{PROBE}.Migrator/MigrationRunner.cs", self.rendered.created)

    def test_the_application_registers_the_dispatcher_that_stages_nothing(self):
        application = self.created(f"src/Services/{PROBE}/{PROBE}.Application/DependencyInjection.cs")
        self.assertIn("services.AddScoped<IDomainEventDispatcher, NoDomainEventDispatcher>();", application)
        self.assertIn(
            "internal sealed class NoDomainEventDispatcher : IDomainEventDispatcher",
            self.created(f"src/Services/{PROBE}/{PROBE}.Application/NoDomainEventDispatcher.cs"))
        self.assertNotIn("IIntegrationEventMapper", application)
        # The command pipeline stays, and TransactionBehavior is what needs a dispatcher at all.
        self.assertIn("typeof(TransactionBehavior<,>)", application)

    def test_the_initialiser_forces_what_the_service_still_registers(self):
        initialiser = self.created(
            f"src/Services/{PROBE}/{PROBE}.Infrastructure/Observability/MetricsInitialiser.cs")
        self.assertIn("public MetricsInitialiser(MessagingMetrics messaging, RequestMetrics requests)", initialiser)

    def test_the_migrations_build_the_inbox_and_the_markers_and_no_outbox(self):
        migrations = sorted(p for p in self.rendered.created if p.startswith(self.prefix))
        self.assertEqual(11, len(migrations), migrations)
        for path in migrations:
            self.assertNotRegex(PurePosixPath(path).name, r"_AddOutbox", path)
            self.assertNotIn("OutboxMessage", self.rendered.created[path], path)

    def test_the_snapshot_is_the_model_ef_would_write_for_a_pure_consumer(self):
        snapshot = self.created(f"{self.prefix}/{PROBE}DbContextModelSnapshot.cs")
        self.assertEqual(2, snapshot.count("modelBuilder.Entity("))
        self.assertIn('modelBuilder.Entity("Common.Infrastructure.Inbox.InboxMessage"', snapshot)
        self.assertIn('modelBuilder.Entity("Common.Infrastructure.Idempotency.IdempotencyMarker"', snapshot)
        # The last block closes the model with no blank line before the pragma, as EF writes it.
        self.assertIn("                });\n#pragma warning restore 612, 618\n", snapshot)

    def test_the_migration_ids_keep_the_template_s_order(self):
        names = sorted(PurePosixPath(p).name for p in self.rendered.created
                       if p.startswith(self.prefix) and not p.endswith(("Designer.cs", "Snapshot.cs")))
        self.assertEqual(
            [f"{MIGRATION_ID}_InitialCreate.cs", f"{INBOX_MIGRATION_ID}_AddInbox.cs",
             "20260809120400_AddIdempotencyMarkers.cs", "20260809120500_IdempotencyMarkerCommittedAtDefault.cs",
             "20260809120600_AddIdempotencyMarkerRowVersion.cs"],
            names)

    def test_both_images_restore_a_closure_with_no_domain_project(self):
        for image in (new_service.WORKER_HOST, "Migrator"):
            dockerfile = self.created(f"src/Services/{PROBE}/{PROBE}.{image}/Dockerfile")
            self.assertNotIn(f"{PROBE}.Domain", dockerfile, image)

    def test_the_solution_folder_holds_four_projects_and_three_suites(self):
        solution = self.rendered.updated["Platform.slnx"]
        self.assertEqual(4, solution.count(f'<Project Path="src/Services/{PROBE}/'))
        self.assertEqual(3, solution.count(f'<Project Path="tests/{PROBE}.'))

    def test_the_broker_account_writes_its_own_endpoints_and_no_contract(self):
        import json

        definitions = json.loads(self.rendered.updated["deploy/compose/rabbitmq/definitions.json"])
        permission = next(e for e in definitions["permissions"] if e["user"] == f"{PROBE.lower()}-svc")
        self.assertEqual(f"^({PROBE.lower()}-|MassTransit:)", permission["write"])
        for verb in ("configure", "read"):
            self.assertEqual(f"^({PROBE.lower()}-|Common\\.Contracts|MassTransit:)", permission[verb])

    def test_no_outbox_meter_line_is_written(self):
        self.assertNotIn(new_service.OBSERVABILITY, self.rendered.updated)

    def test_the_compose_pair_is_migrator_and_worker_and_publishes_no_port(self):
        unit = self.created(UNIT)
        declared = [line for line in unit.split("\n") if new_service.SERVICE_KEY.fullmatch(line)]
        self.assertEqual(declared, [f"  {PROBE.lower()}-migrator:", f"  {PROBE.lower()}-worker:"])
        self.assertNotIn("ports:", unit)
```

In `RendersInsideTheCommentBudget`, after `test_a_worker_render`:

```python
    def test_a_pure_consumer_render(self):
        breaches = budget_breaches(pure_consumer(), Names(PROBE, new_service.WORKER_HOST, True))
        self.assertEqual([], breaches, "\n".join(breaches))
```

and Task 2's whole-unit test takes the third render: its loop becomes
`for rendered in (render(), worker(), pure_consumer()):`.

In `RefusesToRun`, replace `test_the_service_with_no_domain_project_is_refused_in_either_mode`
with:

```python
    def test_the_service_with_no_domain_project_is_refused_outside_the_pure_consumer_mode(self):
        # §4.1 gives Notifications no Domain project, so it renders under
        # --pure-consumer or not at all, and the message names the flag.
        for call in (lambda: render(name="Notifications", port=5198),
                     lambda: worker(name="Notifications")):
            with self.assertRaises(ScaffoldError) as raised:
                call()
            self.assertIn("--pure-consumer", str(raised.exception))

    def test_a_pure_consumer_is_a_worker(self):
        with self.assertRaises(ScaffoldError) as raised:
            plan(REPO_ROOT, PROBE, PORT, MIGRATION_ID, pure_consumer=True)
        self.assertIn("implies --worker", str(raised.exception))

    def test_a_pure_consumer_render_refuses_a_port(self):
        with self.assertRaises(ScaffoldError) as raised:
            plan(REPO_ROOT, PROBE, PORT, MIGRATION_ID, host=new_service.WORKER_HOST, pure_consumer=True)
        self.assertIn("publishes no port", str(raised.exception))

    def test_an_omission_the_manifest_does_not_hold_refuses_the_run(self):
        stray = scaffold.render.PURE_CONSUMER_OMITTED | {"src/Services/Catalog/Catalog.Api/Nothing.cs"}
        with mock.patch.object(scaffold.render, "PURE_CONSUMER_OMITTED", stray):
            with self.assertRaises(ScaffoldError) as raised:
                pure_consumer()
        self.assertIn("PURE_CONSUMER_OMITTED names files COPIED does not", str(raised.exception))

    def test_a_pure_consumer_table_naming_an_omitted_file_refuses_the_run(self):
        omitted = "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs"
        with mock.patch.dict(scaffold.patch.PURE_CONSUMER_PATCHES, {omitted: (("x", "y"),)}):
            with self.assertRaises(ScaffoldError) as raised:
                pure_consumer()
        self.assertIn("a pure-consumer table names files the mode omits", str(raised.exception))

    def test_a_span_whose_anchor_has_moved_refuses_the_run(self):
        factory = "tests/Catalog.TestSupport/CatalogApiFactory.cs"
        spans = (("// an anchor the template does not hold", "}\n", ""),)
        with mock.patch.dict(scaffold.patch.PURE_CONSUMER_SPANS, {factory: spans}):
            with self.assertRaises(ScaffoldError) as raised:
                pure_consumer()
        self.assertIn(factory, str(raised.exception))

    def test_a_span_whose_last_anchor_comes_first_is_refused(self):
        with self.assertRaises(ScaffoldError) as raised:
            scaffold.render.replace_span("b a", "a", "b", "", "probe")
        self.assertIn("does not follow its first", str(raised.exception))
```

and in `test_a_name_refusal_answers_before_the_port_one`, the Notifications
assertion becomes `self.assertIn("--pure-consumer", str(raised.exception))`.

There is deliberately no test rendering `Notifications` itself under the
mode: once Task 5 commits it, the service's directory and its Compose unit
refuse a second render by name, and `template_copy` copies every unit the
index includes. Task 5 is that render, and Task 11's `--verify` is its proof.

In `TheCommandLine`, after `test_a_worker_run_reports_that_it_publishes_no_port`:

```python
    def test_a_pure_consumer_run_reports_its_count_and_that_it_publishes_no_port(self):
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))

            code, out, err = self.run_main(
                "Zulu", "--pure-consumer", "--repo-root", str(root), "--migration-id", MIGRATION_ID,
            )

            self.assertEqual(0, code)
            self.assertEqual("", err)
            # Five shared files and not six: no outbox meter line, and no `.github/` here.
            self.assertIn("52 files created, 5 updated, publishing no port.", out)
            self.assertFalse((root / "src/Services/Zulu/Zulu.Domain").exists())
```

And a class of its own before `if __name__ == "__main__":`, because
`VerifiesAScaffoldCommit` is set up from Shipping's commit and these two are
not about it:

```python
class ReadsAScaffoldCommitsShape(unittest.TestCase):
    """`--verify`'s reading of what a commit added, over a repository built for the case."""

    def test_a_pure_consumer_commit_is_rendered_with_its_own_flag(self):
        scaffolded = scaffold.reproduce.Scaffolded(
            "c" * 40, "p" * 40, "Notifications", True, None, MIGRATION_ID, True)
        self.assertEqual(["Notifications", "--pure-consumer", "--migration-id", MIGRATION_ID], scaffolded.argv)

    def test_a_worker_with_no_domain_project_is_read_as_a_pure_consumer(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            def git(*args: str, stdin: bytes | None = None) -> str:
                return scaffold.reproduce.git(root, *args, stdin=stdin).decode("ascii").strip()

            identity = ("-c", "user.name=scaffold", "-c", "user.email=scaffold@example.invalid")
            git("init", "--quiet")
            empty = git("hash-object", "-w", "-t", "tree", "--stdin", stdin=b"")
            parent = git(*identity, "commit-tree", empty, "-m", "parent")
            for relative in (
                    f"src/Services/{PROBE}/{PROBE}.Worker/Program.cs",
                    f"src/Services/{PROBE}/{PROBE}.Infrastructure/Persistence/Migrations/"
                    f"{MIGRATION_ID}_InitialCreate.cs"):
                (root / relative).parent.mkdir(parents=True, exist_ok=True)
                (root / relative).write_text("//\n", encoding="utf-8")
            git("add", "--all")
            child = git(*identity, "commit-tree", git("write-tree"), "-p", parent, "-m", "render")
            git("update-ref", "HEAD", child)

            scaffolded = scaffold.reproduce.arguments(root, child)

        self.assertTrue(scaffolded.pure_consumer)
        self.assertEqual([PROBE, "--pure-consumer", "--migration-id", MIGRATION_ID], scaffolded.argv)
```

`hash-object --stdin` takes an empty `stdin` explicitly, as the existing
`commit-tree` test does: without it the call waits on the terminal. Plumbing
rather than `git commit`, so no hook and no signing configuration reaches it.

- [ ] **Step 2: Run them to see them fail**

```bash
cd tools/new-service && py -3.12 -m unittest
```

Expected: FAIL — `TypeError: plan() got an unexpected keyword argument
'pure_consumer'`, `AttributeError` on `PURE_CONSUMER_OMITTED`,
`replace_span` and `PURE_CONSUMER_PATCHES`, and `Scaffolded` refusing a seventh
argument.

- [ ] **Step 3: `Names` carries the shape**

`tools/new-service/scaffold/__init__.py`. Before:

```python
@dataclass(frozen=True)
class Names:
    """The three casings every rename needs, and the host the service runs as."""

    pascal: str
    host: str = API_HOST
```

After:

```python
@dataclass(frozen=True)
class Names:
    """The three casings every rename needs, the host the service runs as, and its shape.

    A pure consumer has no Domain project and publishes nothing (§4.1, §3.2): it
    changes which files a render copies, never what any of them is renamed to.
    """

    pascal: str
    host: str = API_HOST
    pure_consumer: bool = False
```

- [ ] **Step 4: The command line, the refusals and the project list**

`tools/new-service/new_service.py`. The refusal sets. Before:

```python
# And the one this script still cannot render at all: §4.1 gives Notifications
# no Domain project, which is a second mode. It comes off with that mode.
UNRENDERABLE_SERVICES = frozenset({"Notifications"})


def project_suffixes(host: str) -> tuple[str, ...]:
    """The nine projects a render creates, by suffix.

    A function of the host rather than a constant, because the host project and
    its suite take the host's own name — and the identity check below and the
    solution writer must agree about them.
    """
    return (
        "Domain",
        "Application",
        "Infrastructure",
        "Migrator",
        host,
        "Domain.Tests",
        "Application.Tests",
        f"{host}.Tests",
        "TestSupport",
    )
```

After:

```python
# The stricter sibling: §4.1 gives these no Domain project, so they render
# under `--pure-consumer` or not at all.
PURE_CONSUMER_ONLY_SERVICES = frozenset({"Notifications"})


def project_suffixes(host: str, pure_consumer: bool = False) -> tuple[str, ...]:
    """The projects a render creates, by suffix: nine, or seven for a pure consumer.

    A function of the host and the shape, because the identity check below and
    the solution writer must agree about both.
    """
    domain = () if pure_consumer else ("Domain",)
    domain_tests = () if pure_consumer else ("Domain.Tests",)
    return (
        *domain,
        "Application",
        "Infrastructure",
        "Migrator",
        host,
        *domain_tests,
        "Application.Tests",
        f"{host}.Tests",
        "TestSupport",
    )
```

`WORKER_ONLY_SERVICES` keeps `Notifications`: §4.1 gives it a Worker, and the
stricter refusal below answers first.

`plan`'s signature becomes
`plan(repo_root, name, port, migration_id, host=API_HOST, pure_consumer=False)`,
and the host and name refusals. Before:

```python
    if host not in HOSTS:
        raise ScaffoldError(f"'{host}' is not a host this script renders; §4.1 names {HOSTS}")

    # The name before the port, because the name decides the host and the host
    # decides whether a port is owed. The narrower refusal first: Notifications
    # is in both sets, and only the message naming what no flag can fix is
    # worth printing.
    if name.lower() in {service.lower() for service in UNRENDERABLE_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} no Domain project and this script renders one. That is a "
            f"second mode, and it joins with the PR that builds the first such host.")
```

After:

```python
    if host not in HOSTS:
        raise ScaffoldError(f"'{host}' is not a host this script renders; §4.1 names {HOSTS}")
    if pure_consumer and host != WORKER_HOST:
        raise ScaffoldError(
            "--pure-consumer implies --worker: a service that publishes nothing and accepts "
            "no command has no API to serve (§3.2)")

    # The name before the port, because the name decides the host and the host
    # decides whether a port is owed. The narrower refusal first: Notifications
    # is in both sets, and the message naming the stricter flag is the one that
    # leaves nothing for a second run to refuse.
    if not pure_consumer and name.lower() in {s.lower() for s in PURE_CONSUMER_ONLY_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} no Domain project. Render it with --pure-consumer; a render "
            f"with a Domain project under this name would contradict the chapter.")
```

`names = Names(name, host)` becomes `names = Names(name, host, pure_consumer)`,
and the identity check's project list takes the shape:

```python
    generated = {
        f"{names.pascal}.{suffix}": suffix for suffix in project_suffixes(host, pure_consumer)
    }
```

The shared-file map loses the meter line for a pure consumer. Before:

```python
        "deploy/compose/rabbitmq/definitions.json":
            update_broker_definitions(repo_root, names),
        OBSERVABILITY: update_observability_meters(repo_root, names),
    }
```

After:

```python
        "deploy/compose/rabbitmq/definitions.json":
            update_broker_definitions(repo_root, names),
    }
    # The outbox meter's line, which a pure consumer is owed no more than the
    # gauges it would collect (§13.6).
    if not names.pure_consumer:
        updated[OBSERVABILITY] = update_observability_meters(repo_root, names)
```

In `main`, after `--worker`:

```python
    parser.add_argument(
        "--pure-consumer",
        action="store_true",
        help=(
            "render §4.1's pure consumer: a Worker with no Domain project, no outbox and "
            "nothing to publish; implies --worker"
        ),
    )
```

`--verify`'s exclusivity check takes it. Before:

```python
        if args.name is not None or args.port is not None or args.worker or args.migration_id is not None:
```

After:

```python
        if (args.name is not None or args.port is not None or args.worker or args.pure_consumer
                or args.migration_id is not None):
```

And the render call. Before:

```python
        host = WORKER_HOST if args.worker else API_HOST
        rendered = plan(args.repo_root, args.name, args.port, migration_id, host)
```

After:

```python
        host = WORKER_HOST if args.worker or args.pure_consumer else API_HOST
        rendered = plan(args.repo_root, args.name, args.port, migration_id, host, args.pure_consumer)
```

- [ ] **Step 5: The omissions, the spans, the outbox entity and the dispatcher**

`tools/new-service/scaffold/render.py`. The import:

```python
from scaffold.patch import PATCHES, PURE_CONSUMER_PATCHES, PURE_CONSUMER_SPANS, WORKER_PATCHES
```

Above `ASSEMBLY_MARKER`, the manifest and the one file the mode writes:

```python
# What a pure consumer is not given, all of it COPIED for every other render:
# the Domain project and its suite (§4.1), and §9.4's outbox and §9.3's mapper,
# since a service that publishes nothing stages nothing (§3.2). `classify`
# refuses an entry COPIED does not hold.
PURE_CONSUMER_OMITTED = frozenset(
    {
        "src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj",
        "src/Services/Catalog/Catalog.Application/Integration/CatalogIntegrationEventMapper.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/EfDomainEventCollector.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxPublisher.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Persistence/OutboxMessageConfiguration.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/IOutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxMetrics.cs",
        "tests/Catalog.Domain.Tests/ArchitectureTests.cs",
        "tests/Catalog.Domain.Tests/Catalog.Domain.Tests.csproj",
        "tests/Catalog.Api.Tests/MessageTypeMapValidatorTests.cs",
        "tests/Catalog.Api.Tests/OutboxDispatcherTests.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxRows.cs",
        "tests/Catalog.TestSupport/Outbox/OutboxTestEvents.cs",
    }
)

# A pure consumer's one file with no counterpart in Catalog: §6.3's TransactionBehavior
# needs a dispatcher, and §7.5's needs the collector, mapper and publisher this shape lacks.
NO_DOMAIN_EVENT_DISPATCHER = """using Common.Application;

namespace Catalog.Application;

/// <summary>§7.5's dispatcher for a service §4.1 gives no Domain project, where no aggregate raises an event.</summary>
internal sealed class NoDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct) => Task.CompletedTask;
}
"""
```

`MetricsInitialiser.cs` is not omitted: it forces `MessagingMetrics` and
`RequestMetrics` too, which a consumer still registers and §13.6 still wants
at zero, so a patch drops only its outbox parameter. The Domain project's own
`AssemblyMarker.cs` is not in `COPIED` and so not in the list either; the
render loop below simply does not write it.

After `LATER_MIGRATION`:

```python
# The template migrations that build §9.4's outbox, which a pure consumer does
# not copy. Its other migrations keep their offsets, so their ids keep the gaps.
PURE_CONSUMER_MIGRATIONS = (OUTBOX_MIGRATION, RETENTION_INDEX_MIGRATION)
```

The gaps are deliberate: an id is the base plus the migration's position in
`TEMPLATE_MIGRATIONS`, so `AddInbox` stays two minutes after `InitialCreate`
and EF's order is the template's.

`classify`'s inert check grows to the four tables, and the two lists are held
to the manifest. Before:

```python
    # And no patch may be inert. A PATCHES or WORKER_PATCHES key for a file
    # that is not copied never reaches `require_once`, so the anchor it guards
    # would be unbound while every other anchor still looked enforced. The
    # parentheses matter: `-` binds tighter than `|`.
    if (inert := (set(PATCHES) | set(WORKER_PATCHES)) - set(copied)):
        raise ScaffoldError(
            "PATCHES or WORKER_PATCHES names files the scaffold does not copy: "
            + ", ".join(sorted(inert))
            + ". A patch that never runs is an anchor that guards nothing."
        )
    return copied
```

After:

```python
    # And no patch may be inert. A key for a file that is not copied never
    # reaches `require_once`, so the anchor it guards would be unbound while
    # every other anchor still looked enforced. The parentheses matter: `-`
    # binds tighter than `|`.
    tables = set(PATCHES) | set(WORKER_PATCHES) | set(PURE_CONSUMER_PATCHES) | set(PURE_CONSUMER_SPANS)
    if (inert := tables - set(copied)):
        raise ScaffoldError(
            "a patch table names files the scaffold does not copy: "
            + ", ".join(sorted(inert))
            + ". A patch that never runs is an anchor that guards nothing."
        )

    # The pure consumer's two lists, held to the manifest on the same terms: an
    # omission COPIED does not hold omits nothing, and a patch for a file the
    # mode omits is an anchor no pure render reaches.
    if (stray := PURE_CONSUMER_OMITTED - COPIED):
        raise ScaffoldError(
            "PURE_CONSUMER_OMITTED names files COPIED does not: " + ", ".join(sorted(stray)))
    if (unreached := (set(PURE_CONSUMER_PATCHES) | set(PURE_CONSUMER_SPANS)) & PURE_CONSUMER_OMITTED):
        raise ScaffoldError(
            "a pure-consumer table names files the mode omits: " + ", ".join(sorted(unreached)))
    return copied


def replace_span(text: str, first: str, last: str, replacement: str, where: str) -> str:
    """The text with everything from `first` through `last` replaced, each anchor bound exactly once."""
    require_once(text, first, where)
    require_once(text, last, where)
    start, end = text.index(first), text.index(last)
    if end < start + len(first):
        raise ScaffoldError(f"{where}: a span's last anchor does not follow its first")
    return text[:start] + replacement + text[end + len(last):]


def pure_consumer_omits(relative: str) -> bool:
    """Whether a pure-consumer render leaves this template file out."""
    if relative in PURE_CONSUMER_OMITTED:
        return True
    name = PurePosixPath(relative).name
    return relative.startswith(MIGRATIONS + "/") and any(
        shape.fullmatch(name) for shape in PURE_CONSUMER_MIGRATIONS)
```

Before `snapshot_from_designer`, the outbox entity's removal:

```python
# §9.4's outbox entity, the last block in every designer after AddOutbox: EF
# orders entities by name. A pure consumer's model has no outbox, so its
# designers and its snapshot describe none (§7.4).
OUTBOX_ENTITY = '\n            modelBuilder.Entity("Common.Infrastructure.Outbox.OutboxMessage", b =>\n'
LAST_ENTITY_END = "                });\n#pragma warning restore 612, 618\n"


def without_outbox_entity(designer: str, where: str) -> str:
    """The model body with the outbox entity and the blank line above it removed."""
    require_once(designer, OUTBOX_ENTITY, where)
    start = designer.index(OUTBOX_ENTITY)
    end = designer.find(LAST_ENTITY_END, start)
    if end == -1:
        raise ScaffoldError(f"{where}: the outbox entity is no longer the model's last block")
    return designer[:start] + designer[end + len("                });\n"):]
```

The anchor carries the blank line above the block, and the cut keeps the
`#pragma` line, so what is left ends the inbox block directly on the pragma —
the shape EF writes for a model whose last entity is the inbox. Verified, not
argued: Task 9's `migrations add` against this snapshot emits one `CreateTable`
and rewrites the snapshot by adding the new entity and nothing else.

In `render_projects`, the loop skips what the mode omits and applies the
mode's tables after the others. Before:

```python
    for relative in classify(repo_root, labels):
        text, newline = read(repo_root, relative)
```

After:

```python
    for relative in classify(repo_root, labels):
        if names.pure_consumer and pure_consumer_omits(relative):
            continue
        text, newline = read(repo_root, relative)
```

Before:

```python
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)
```

After:

```python
        for needle, replacement in patches:
            require_once(text, needle, relative)
            text = text.replace(needle, replacement)
        # Spans first, then the pure consumer's own patches, so a patch is bound
        # against what the spans left rather than against text they remove.
        if names.pure_consumer:
            for first, last, replacement in PURE_CONSUMER_SPANS.get(relative, ()):
                text = replace_span(text, first, last, replacement, relative)
            for needle, replacement in PURE_CONSUMER_PATCHES.get(relative, ()):
                require_once(text, needle, relative)
                text = text.replace(needle, replacement)
```

The designers lose the outbox entity after the slice's. Before:

```python
            text = without_slice_entity(text)
```

After:

```python
            text = without_slice_entity(text)
            if names.pure_consumer:
                text = without_outbox_entity(text, relative)
```

The snapshot is derived from the last designer's `text` further down, so it
inherits the removal with no edit of its own.

The marker. Before:

```python
    created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
        restore(names.rename(ASSEMBLY_MARKER), csharp_newline)
    )
    return created
```

After:

```python
    # A pure consumer has no Domain project for the marker to anchor (§4.1), and
    # the dispatcher it registers in that project's place.
    if names.pure_consumer:
        created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Application/NoDomainEventDispatcher.cs")] = (
            restore(names.rename(NO_DOMAIN_EVENT_DISPATCHER), csharp_newline)
        )
    else:
        created[names.rename(f"src/Services/{TEMPLATE}/{TEMPLATE}.Domain/AssemblyMarker.cs")] = (
            restore(names.rename(ASSEMBLY_MARKER), csharp_newline)
        )
    return created
```

`update_solution`'s two lists take the shape:

```python
    domain = () if names.pure_consumer else ("Domain",)
    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\n'
            for layer in sorted(("Application", *domain, "Infrastructure", "Migrator", names.host))
        ),
        "  </Folder>\n",
    ]
```

and

```python
    domain_tests = () if names.pure_consumer else ("Domain.Tests",)
    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\n'
        for suite in sorted(("Application.Tests", *domain_tests, "TestSupport", f"{names.host}.Tests"))
    ]
```

`update_broker_definitions` writes a consumer's grant. Before:

```python
    definitions["permissions"].append({
        "user": user,
        "vhost": template_permission["vhost"],
        "configure": names.rename(template_permission["configure"]),
        "write": names.rename(template_permission["write"]),
        "read": names.rename(template_permission["read"]),
    })
```

After:

```python
    grant = {verb: names.rename(template_permission[verb]) for verb in ("configure", "write", "read")}
    if names.pure_consumer:
        # A consumer's shape, not the template's publisher's: it declares and reads
        # the contract exchanges it binds, and writes only its own endpoints and
        # the fault exchanges, since §3.2 gives it nothing to publish (ADR-036).
        bound = f"^({names.lower}-|Common\\.Contracts|MassTransit:)"
        grant = {"configure": bound, "write": f"^({names.lower}-|MassTransit:)", "read": bound}

    definitions["permissions"].append({
        "user": user,
        "vhost": template_permission["vhost"],
        **grant,
    })
```

The spec puts the account in PR-1 as an item of its own; it is rendered here
instead, because the template's publisher grant is wrong by construction for a
service with nothing to publish, and section 2 makes a hand fix after the
render a scaffold defect.

- [ ] **Step 6: The two tables**

`tools/new-service/scaffold/patch.py`, after `WORKER_PATCHES`. Every needle
below is matched against the text `PATCHES` and `WORKER_PATCHES` already
produced, which is why several quote a replacement of theirs rather than the
template:

```python
# The edits a PURE-CONSUMER render makes on top of PATCHES and WORKER_PATCHES,
# matched against the text those already produced. §4.1 gives the service no
# Domain project and §3.2 nothing to publish, so what leaves is everything that
# names the Domain assembly, §9.4's outbox or §9.3's mapper; what stays is the
# inbox, the purge, the migrator, the probes and the bus (§9.5).
PURE_CONSUMER_PATCHES: dict[str, tuple[tuple[str, str], ...]] = {
    # Both images restore the host's project closure, which has no Domain project to copy.
    "src/Services/Catalog/Catalog.Api/Dockerfile": (
        ("COPY src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj src/Services/Catalog/Catalog.Domain/\n", ""),
    ),
    "src/Services/Catalog/Catalog.Migrator/Dockerfile": (
        ("COPY src/Services/Catalog/Catalog.Domain/Catalog.Domain.csproj src/Services/Catalog/Catalog.Domain/\n", ""),
    ),
    "src/Services/Catalog/Catalog.Application/DependencyInjection.cs": (
        ("using Catalog.Application.Integration;\n", ""),
        (
            "        // Explicit rather than scanned, beside the dispatcher it serves —\n"
            "        // §4.2's registration sample is the shape. §7.5's real dispatcher,\n"
            "        // and no null one beside it: a dispatcher that drops every domain\n"
            "        // event is deleted rather than disabled, so nothing can register it\n"
            "        // back by accident.\n"
            "        services.AddDomainEventDispatcher();\n"
            "\n"
            "        // §9.3's allow-list, explicit so what this service publishes is not whichever types the assembly holds.\n"
            "        services.AddScoped<IIntegrationEventMapper, CatalogIntegrationEventMapper>();\n",
            "        // §7.5's dispatcher for a service §4.1 gives no Domain project, where nothing raises an event.\n"
            "        services.AddScoped<IDomainEventDispatcher, NoDomainEventDispatcher>();\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Application/Catalog.Application.csproj": (
        (
            "    Domain and Common.Application, §4.2's second row; Common.Contracts joins with the §9.3 mapper's first entry.\n",
            "    Common.Application, §4.2's second row without the Domain project §4.1 does not give this service.\n",
        ),
        ("    <ProjectReference Include=\"..\\Catalog.Domain\\Catalog.Domain.csproj\" />\n", ""),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj": (
        (
            "  <!-- Domain and Application, and any package besides, per §4.2's third row. -->\n",
            "  <!-- Application, and any package besides, per §4.2's third row; §4.1 gives this service no Domain. -->\n",
        ),
        (
            "    <!-- OutboxStats' MemoryCache (§13.6), named directly though Common.Infrastructure carries it. -->\n"
            "    <PackageReference Include=\"Microsoft.Extensions.Caching.Memory\" />\n",
            "",
        ),
        ("    <ProjectReference Include=\"..\\Catalog.Domain\\Catalog.Domain.csproj\" />\n", ""),
        (
            "    <!-- §9.4's outbox, and §8's Redis helpers beside it. -->\n",
            "    <!-- §9.5's inbox and its purge, and §8's Redis helpers beside them. -->\n",
        ),
        (
            "    <!-- MessageTypeSource's Broker half, through IIntegrationEvent until this service has a contract (§9.4). -->\n"
            "    <ProjectReference Include=\"..\\..\\..\\BuildingBlocks\\Common.Contracts\\Common.Contracts.csproj\" />\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs": (
        ("using Catalog.Domain;\n", ""),
        ("using Common.Contracts;\n", ""),
        ("using Common.Infrastructure.Outbox;\n", ""),
        ("using Microsoft.Data.SqlClient;\n", ""),
        (
            "        // §7.5's two halves, scoped because the context they share is.\n"
            "        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();\n"
            "        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();\n"
            "\n",
            "",
        ),
        (
            "        // One local, so the tables of §9.4, §9.5 and §8.5 cannot name different schemas.\n"
            "        const string schema = \"catalog\";\n"
            "        services.AddSingleton(new OutboxTable(schema));\n",
            "        // One local, so the tables of §9.5 and §8.5 cannot name different schemas; there is no outbox (§3.2).\n"
            "        const string schema = \"catalog\";\n",
        ),
        (
            "        // Resolves the metrics classes at start, before the bus and the dispatcher, so every instrument\n"
            "        // exists before the first message (§13.6).\n",
            "        // Resolves the metrics classes at start, before the bus, so every instrument exists before the first\n"
            "        // message (§13.6).\n",
        ),
        (
            "        // The poll loop of §9.4, by AddHostedService<T> because §12.4's fixture removes it by ImplementationType.\n"
            "        // After the bus and before the purge: hosted services stop in reverse, so it drains into a live transport.\n"
            "        services.AddHostedService<OutboxDispatcher>();\n"
            "\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Observability/MetricsInitialiser.cs": (
        (
            "    public MetricsInitialiser(OutboxMetrics outbox, MessagingMetrics messaging, RequestMetrics requests)\n"
            "    {\n"
            "        ArgumentNullException.ThrowIfNull(outbox);\n",
            "    public MetricsInitialiser(MessagingMetrics messaging, RequestMetrics requests)\n"
            "    {\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/InboxMessageConfiguration.cs": (
        (
            "/// <summary>§9.5's table, mapped here for the reason <see cref=\"OutboxMessageConfiguration\"/> gives.</summary>\n",
            "/// <summary>§9.5's table, mapped here because the schema is this service's and the scan looks here.</summary>\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/CatalogDbContext.cs": (
        ("using Common.Infrastructure.Outbox;\n", ""),
        (
            "    /// <summary>§9.4's outbox, on this context so a row enlists in the aggregate's transaction.</summary>\n"
            "    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();\n"
            "\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations/20260811125717_AddInbox.cs": (
        (
            "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>\n"
            "/// on <c>AddOutbox</c>'s terms: the configuration is the source of truth, and\n"
            "/// the <c>.Designer.cs</c> and snapshot beside it are machine-owned.\n",
            "/// §9.5's inbox table, generated from <see cref=\"InboxMessageConfiguration\"/>: the\n"
            "/// configuration is the source of truth, and the <c>.Designer.cs</c> and snapshot\n"
            "/// beside it are machine-owned.\n",
        ),
    ),
    "src/Services/Catalog/Catalog.Infrastructure/Persistence/Migrations/20260901141908_IdempotencyMarkerCommittedAtDefault.cs": (
        (
            "/// <see cref=\"IdempotencyMarkerConfiguration\"/>; the outbox and inbox, whose windows are housekeeping, are left alone.\n",
            "/// <see cref=\"IdempotencyMarkerConfiguration\"/>; the inbox, whose window is housekeeping, is left alone.\n",
        ),
    ),
    "tests/Catalog.Application.Tests/ArchitectureTests.cs": (
        ("using System.Reflection;\nusing Catalog.Domain;\n", ""),
        (
            "        // §4.2's second row as an allow-list: Dapper is §6.5's read side and brings System.Data.Common, and\n"
            "        // Common.Domain is here because the mapper's IDomainEvent puts it among the references.\n",
            "        // §4.2's second row as an allow-list, with no Domain project because §4.1 gives none: Dapper is §6.5's\n"
            "        // read side and brings System.Data.Common.\n",
        ),
        ("            \"Catalog.Domain\",\n", ""),
        (
            "    public void Application_and_domain_do_not_reference_masstransit()\n"
            "    {\n"
            "        // §9.3's must-not list, whose one exemption is a saga's receive endpoint and its outbox (ADR-032).\n"
            "        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(AssemblyMarker).Assembly];\n"
            "        foreach (Assembly assembly in assemblies)\n"
            "        {\n"
            "            Types\n"
            "                .InAssembly(assembly)\n"
            "                .ShouldNot().HaveDependencyOn(\"MassTransit\")\n"
            "                .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);\n"
            "        }\n"
            "    }\n",
            "    public void Application_does_not_reference_masstransit()\n"
            "    {\n"
            "        // §9.3's must-not list, over the one layer of the two it names that §4.1 gives this service.\n"
            "        Types\n"
            "            .InAssembly(typeof(DependencyInjection).Assembly)\n"
            "            .ShouldNot().HaveDependencyOn(\"MassTransit\")\n"
            "            .GetResult().IsSuccessful.ShouldBeTrue();\n"
            "    }\n",
        ),
    ),
    "tests/Catalog.Api.Tests/ArchitectureTests.cs": (
        ("using Catalog.Domain;\n", "using Common.Domain;\n"),
        ("        typeof(AssemblyMarker).Assembly,\n", ""),
        (
            "    [Fact]\n"
            "    public void Nothing_in_this_service_references_the_migrator()\n",
            "    [Fact]\n"
            "    public void Nothing_in_this_service_raises_a_domain_event()\n"
            "    {\n"
            "        // The premise of the dispatcher that stages nothing (§7.5): §4.1 gives this service no Domain project.\n"
            "        string[] raisers =\n"
            "        [\n"
            "            .. ServiceAssemblies\n"
            "                .SelectMany(assembly => assembly.GetTypes())\n"
            "                .Where(type => typeof(IDomainEvent).IsAssignableFrom(type) ||\n"
            "                    typeof(IHasDomainEvents).IsAssignableFrom(type))\n"
            "                .Select(type => type.FullName ?? type.Name)\n"
            "        ];\n"
            "\n"
            "        raisers.ShouldBeEmpty($\"a domain event needs §7.5's real dispatcher: {string.Join(\", \", raisers)}\");\n"
            "    }\n"
            "\n"
            "    [Fact]\n"
            "    public void Nothing_in_this_service_references_the_migrator()\n",
        ),
    ),
    "tests/Catalog.Api.Tests/DatabaseSmokeTests.cs": (
        (
            "        applied.Length.ShouldBe(7);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddOutbox\");\n"
            "        applied[2].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[3].ShouldEndWith(\"_AddOutboxRetentionIndex\");\n"
            "        applied[4].ShouldEndWith(\"_AddIdempotencyMarkers\");\n"
            "        applied[5].ShouldEndWith(\"_IdempotencyMarkerCommittedAtDefault\");\n"
            "        applied[6].ShouldEndWith(\"_AddIdempotencyMarkerRowVersion\");\n",
            "        applied.Length.ShouldBe(5);\n"
            "        applied[0].ShouldEndWith(\"_InitialCreate\");\n"
            "        applied[1].ShouldEndWith(\"_AddInbox\");\n"
            "        applied[2].ShouldEndWith(\"_AddIdempotencyMarkers\");\n"
            "        applied[3].ShouldEndWith(\"_IdempotencyMarkerCommittedAtDefault\");\n"
            "        applied[4].ShouldEndWith(\"_AddIdempotencyMarkerRowVersion\");\n",
        ),
    ),
    "tests/Catalog.Api.Tests/MetricsRegistrationTests.cs": (
        ("using System.Diagnostics.Metrics;\n", ""),
        ("using Microsoft.Extensions.Logging;\nusing Microsoft.Extensions.Logging.Abstractions;\n", ""),
        (
            "/// <summary>§13.6's registration rules, over a <c>ServiceCollection</c> and a <see cref=\"Meter\"/>.</summary>\n",
            "/// <summary>§13.6's registration rules, over a <c>ServiceCollection</c>; there are no outbox gauges (§3.2).</summary>\n",
        ),
        ("        registered.ShouldContain(typeof(OutboxMetrics));\n", ""),
    ),
    "tests/Catalog.Api.Tests/RetentionPurgeTests.cs": (
        ("using Catalog.TestSupport.Outbox;\n", ""),
        ("using Common.Infrastructure.Outbox;\n", ""),
        (
            "    public async Task A_skewed_clock_purges_the_outbox_and_the_inbox_and_leaves_the_marker()\n"
            "    {\n"
            "        // Two clocks, one age, opposite outcomes: the outbox's and inbox's cutoffs read the skewed clock, the\n",
            "    public async Task A_skewed_clock_purges_the_inbox_and_leaves_the_marker()\n"
            "    {\n"
            "        // Two clocks, one age, opposite outcomes: the inbox's cutoff reads the skewed clock, the\n",
        ),
        ("            OutboxWindow = window,\n", ""),
        (
            "        // One instant for all three rows, so only the clock a statement read varies.\n",
            "        // One instant for both rows, so only the clock a statement read varies.\n",
        ),
        (
            "        OutboxMessage row = OutboxRows.Healthy(fixture);\n"
            "        await fixture.StageOutboxAsync(row);\n"
            "        await fixture.SetOutboxProcessedAtAsync(row.MessageId, justNow);\n"
            "\n",
            "",
        ),
        (
            "        outbox.ShouldBe(1, \"the outbox cutoff is subtracted from the registered clock, which is two days ahead\");\n"
            "        inbox.ShouldBe(1, \"§9.5 keeps the inbox on that same application-computed cutoff, deliberately\");\n",
            "        outbox.ShouldBe(0, \"this service registers no outbox table, so the pass has none to purge (§9.5)\");\n"
            "        inbox.ShouldBe(1, \"the inbox cutoff is subtracted from the registered clock, which is two days ahead\");\n",
        ),
        (
            "        // §9.5 asks for one hosted service covering every table.\n"
            "        OutboxMessage row = OutboxRows.Healthy(fixture);\n"
            "        await fixture.StageOutboxAsync(row);\n"
            "        await fixture.SetOutboxProcessedAtAsync(row.MessageId, LongAgo);\n"
            "\n",
            "        // §9.5 asks for one hosted service covering every table this service registers, which has no outbox.\n",
        ),
        (
            "        (await fixture.PurgeRetentionAsync()).ShouldBe((Outbox: 1, Inbox: 1, Idempotency: 1));\n",
            "        (await fixture.PurgeRetentionAsync()).ShouldBe((Outbox: 0, Inbox: 1, Idempotency: 1));\n",
        ),
    ),
    "tests/Catalog.TestSupport/CatalogApiFactory.cs": (
        ("using Catalog.TestSupport.Outbox;\nusing Common.Application;\n", ""),
        ("using Common.Infrastructure.Outbox;\n", ""),
        (
            "                // §9.5's purge, removed by the same match, so a test that a row survives retention drives the pass.\n",
            "                // §9.5's purge, matched by the ImplementationType AddHostedService<T> sets, so a test drives each pass.\n",
        ),
    ),
}

# The pure consumer's larger cuts, each from its first anchor through its last
# with both bound exactly once, so a whole test leaves without being quoted.
# Applied before PURE_CONSUMER_PATCHES, which are bound against what is left.
PURE_CONSUMER_SPANS: dict[str, tuple[tuple[str, str, str], ...]] = {
    "src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs": (
        (
            "        // The persisted type names (§9.4).",
            "        services.AddSingleton<OutboxJson>();\n\n",
            "",
        ),
        (
            "        // §13.6's per-lane gauges,",
            "        services.AddSingleton<OutboxMetrics>();\n\n",
            "",
        ),
    ),
    "tests/Catalog.Application.Tests/DependencyInjectionTests.cs": (
        (
            "    [Fact]\n    public void AddCatalogApplication_registers_the_real_domain_event_dispatcher_scoped()\n",
            "            .ImplementationType!.Name.ShouldBe(\"CatalogIntegrationEventMapper\");\n    }\n",
            "    [Fact]\n"
            "    public void AddCatalogApplication_registers_the_dispatcher_that_stages_nothing()\n"
            "    {\n"
            "        // Named, since §4.1 gives this service no Domain project and the real one would not resolve (§7.5).\n"
            "        ServiceCollection services = new();\n"
            "\n"
            "        services.AddCatalogApplication();\n"
            "\n"
            "        ServiceDescriptor dispatcher = services\n"
            "            .Where(d => d.ServiceType == typeof(IDomainEventDispatcher))\n"
            "            .ShouldHaveSingleItem();\n"
            "        dispatcher.Lifetime.ShouldBe(ServiceLifetime.Scoped);\n"
            "        dispatcher.ImplementationType!.Name.ShouldBe(\"NoDomainEventDispatcher\");\n"
            "\n"
            "        // Nothing is published, so §9.3's mapper and §7.5's registry have nothing to serve.\n"
            "        services.ShouldNotContain(d => d.ServiceType == typeof(IIntegrationEventMapper));\n"
            "        services.ShouldNotContain(d => d.ServiceType == typeof(IProjectionRegistry));\n"
            "    }\n",
        ),
    ),
    "tests/Catalog.Api.Tests/MetricsRegistrationTests.cs": (
        (
            "    [Fact]\n    public void The_outbox_gauges_report_one_measurement_per_lane_on_the_registered_meter()\n",
            "        return \"\";\n    }\n\n",
            "",
        ),
        (
            "\n    /// <summary>Stands in for an unreachable database.</summary>\n",
            "        public int AbandonedCount(OutboxLane lane) => lane == OutboxLane.Broker ? 61 : 62;\n    }\n",
            "",
        ),
    ),
    "tests/Catalog.Api.Tests/RetentionPurgeTests.cs": (
        (
            "    [Fact]\n    public async Task A_processed_outbox_row_past_the_window_is_deleted()\n",
            "        (await fixture.OutboxAsync()).ShouldHaveSingleItem();\n    }\n\n",
            "",
        ),
        (
            "    [Fact]\n    public async Task A_backlog_larger_than_one_batch_drains_over_batches_and_stops_at_the_ceiling()\n",
            "        (await fixture.OutboxAsync()).ShouldBeEmpty();\n    }\n",
            "    [Fact]\n"
            "    public async Task A_backlog_larger_than_one_batch_drains_over_batches_and_stops_at_the_ceiling()\n"
            "    {\n"
            "        // A policy of its own: five inbox rows in batches of two show both edges.\n"
            "        for (int row = 0; row < 5; row++)\n"
            "            await fixture.StageInboxAsync(new InboxMessage(Guid.CreateVersion7(), \"catalog-events\", LongAgo));\n"
            "\n"
            "        RetentionPolicy twoAtATime = new() { BatchSize = 2, MaxBatchesPerPass = 2 };\n"
            "\n"
            "        // Four of five: two batches of two, then the ceiling.\n"
            "        (await fixture.PurgeWithAsync(twoAtATime)).Inbox.ShouldBe(4);\n"
            "        (await fixture.InboxAsync()).Count.ShouldBe(1);\n"
            "\n"
            "        // The next pass takes the remainder and stops short of its ceiling, on the partial batch.\n"
            "        (await fixture.PurgeWithAsync(twoAtATime)).Inbox.ShouldBe(1);\n"
            "        (await fixture.InboxAsync()).ShouldBeEmpty();\n"
            "    }\n",
        ),
    ),
    "tests/Catalog.TestSupport/CatalogApiFactory.cs": (
        (
            "                // Only the outbox dispatcher: MassTransit's bus is a hosted service too.",
            "                services.AddSingleton<OutboxDispatcher>();\n\n",
            "",
        ),
        (
            "\n                // §9.4: added to rather than replaced,",
            "                services.AddPluggableFrom(typeof(AlwaysThrows).Assembly);\n",
            "",
        ),
        (
            "\nfile static class ServiceDescriptorExtensions\n",
            "\"assembly's events cannot be added to it before the map is built (§9.4).\");\n}\n",
            "",
        ),
    ),
}
```

What each entry is for, where it is not obvious from the text:

- **The two Dockerfiles** are the one failure no suite sees: both COPY the
  Domain project's csproj into the restore layer, and a render without the
  project builds and tests green and then fails `docker build` on a missing
  file. Task 11 builds both images.
- **The inbox configuration's summary** pointed at `OutboxMessageConfiguration`,
  which the mode omits; the cref would dangle and the sentence would send a
  reader to a file the service does not have.
- **The two migration headers** named `AddOutbox` and "the outbox"; a pure
  consumer has neither.
- **`MetricsRegistrationTests`** keeps the forced-or-explained test, the
  selector test and the hosted-service test, and loses the three gauge tests
  and the three doubles they used; the selector still asserts
  `MessagingMetrics` and `RequestMetrics`, so it is not vacuous.
- **`RetentionPurgeTests`** loses the three outbox tests, keeps the batching
  test by moving it to the inbox — the batching is per table and still worth a
  case — and asserts `Outbox: 0`, which is Task 1's behaviour over a real host.
- **The factory** loses the dispatcher's removal, the type-map extension and the
  test assembly's projection scan, all of them about the outbox's `Local` lane.

- [ ] **Step 7: `--verify` reads the shape**

`tools/new-service/scaffold/reproduce.py`. Before:

```python
    port: int | None
    migration_id: str

    @property
    def argv(self) -> list[str]:
        flags = ["--worker"] if self.worker else ["--port", str(self.port)]
        return [self.name, *flags, "--migration-id", self.migration_id]
```

After:

```python
    port: int | None
    migration_id: str
    pure_consumer: bool = False

    @property
    def argv(self) -> list[str]:
        if self.pure_consumer:
            flags = ["--pure-consumer"]
        else:
            flags = ["--worker"] if self.worker else ["--port", str(self.port)]
        return [self.name, *flags, "--migration-id", self.migration_id]
```

In `arguments`, after the `worker` line:

```python
    # A worker with no Domain project is §4.1's pure consumer, the one shape that omits it.
    pure_consumer = worker and not any(path.startswith(f"{root}{name}.Domain/") for path in added)
```

and the return becomes
`return Scaffolded(full, parent, name, worker, port, ids[0], pure_consumer)`.
Shipping's commit adds `Shipping.Domain/`, so its arguments are unchanged and
`VerifiesAScaffoldCommit` stays green.

- [ ] **Step 8: Run the suite**

```bash
cd tools/new-service && py -3.12 -m unittest
```

Expected: green, and every existing test unchanged, which is the evidence the
API and worker renders are byte-identical to what they were.
`VerifiesAScaffoldCommit` needs history and errors on a shallow clone, as
CI's `fetch-depth: 0` exists to prevent.

- [ ] **Step 9: The comment budget over what the mode writes**

`test_a_pure_consumer_render` above is the check; it judges every line the
render writes that the template does not carry, and the rendered C#'s comment
share against `COMMENT_CEILING`. Task 2's whole-unit test now covers the pure
unit too. Both are in Step 8's run.

- [ ] **Step 10: The scaffold's README**

`tools/new-service/README.md`. In the argument table, after the `--worker`
row:

```markdown
| `--pure-consumer` | Render §4.1's pure consumer, and imply `--worker`: seven projects, no Domain project, no outbox, no mapper and no outbox meter line |
```

In the shared-files table, the `ObservabilityExtensions.cs` row:

```markdown
| `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` | the `AddMeter` line for the service's outbox meter, which §13.2's export names one by one; a pure consumer has no outbox and gets none |
```

The closing worker paragraph's last sentence. Before:

> **`Shipping` is refused without
> `--worker` and rendered with it**; `Notifications` is refused in both modes,
> because §4.1 gives it no Domain project and that is a second mode this
> script does not have.

After, as that sentence and a paragraph of its own:

> **`Shipping` is refused without `--worker` and rendered with it.**
>
> **`--pure-consumer` renders §4.1's third shape** and implies `--worker`: the
> seven projects without `<Name>.Domain` and `<Name>.Domain.Tests`, and nothing
> of §9.4's outbox or §9.3's mapper — no outbox table or its two migrations, no
> dispatcher, publisher, gauges or meter line, no collector and no mapper —
> because §3.2 gives such a service nothing to publish. The inbox, the purge,
> the marker table, the migrator, the probes and the bus stay. The render
> writes a `NoDomainEventDispatcher` into the Application project, because
> §6.3's `TransactionBehavior` still needs one, and the worker suite's
> architecture gate holds its premise. Its broker account writes its own
> endpoints and the fault exchanges and no contract. **`Notifications` is
> refused without it.** The mode is three tables in `scaffold/`:
> `PURE_CONSUMER_OMITTED`, `PURE_CONSUMER_PATCHES` and `PURE_CONSUMER_SPANS`,
> the last a cut from one anchor through another, each bound exactly once.

- [ ] **Step 11: §4.5**

`docs/backend-architecture/04-solution-structure.md`. The opening. Before:

> **Five** of §4.1's six services share the shape below — Catalog, Ordering,
> Inventory and Payments as API hosts, Shipping as a worker — and writing the
> fifth by hand is how it ends up subtly different from the first four. One
> command renders it instead:

After:

> **All six** of §4.1's services share the shape below — Catalog, Ordering,
> Inventory and Payments as API hosts, Shipping as a worker and Notifications
> as a pure consumer — and writing one by hand is how it ends up subtly
> different from the rest. One command renders it instead:

After the `--worker` paragraph, which ends "edit a file's text but not its
path.", a paragraph of its own:

> `--pure-consumer` renders §4.1's third shape and implies `--worker`. §4.1
> gives such a service no Domain project and §3.2 nothing to publish, so the
> render is seven projects with none of §9.4's outbox or §9.3's mapper: no
> outbox table or its two migrations, no dispatcher, publisher or gauges and no
> `AddMeter` line, no collector and no mapper. §9.5's inbox and the purge over
> it, §8.5's marker table, the migrator, the probes and the bus stay. Its
> broker account writes its own endpoints and the fault exchanges and no
> contract exchange
> ([ADR-036](adr/ADR-036-the-broker-has-a-per-service-identity.md)). §6.3's
> `TransactionBehavior` still calls a domain-event dispatcher, so the render
> writes one that stages nothing ([§7.5](07-persistence.md)), and the
> service's own architecture gate holds its premise. The mode is a set of
> omissions from the one template rather than a second template, so
> `--verify` reproduces its commit like any other.

The project count. Before:

> It writes §4.1's five service projects, its three test projects and its
> `TestSupport` library — nine in all, and §4.1 is explicit that the last is not
> a test project — with everything the service template has accumulated: the

After:

> It writes §4.1's five service projects, its three test projects and its
> `TestSupport` library — nine in all, seven for a pure consumer, and §4.1 is
> explicit that the last is not a test project — with everything the service
> template has accumulated: the

The closing callout. Before:

> **The scaffold refuses `Notifications` by name, and `Shipping` only without
> `--worker`.** Documenting the gap left the script willing to render either
> as an API service, which would have contradicted §4.1 quietly. A note is not
> a guard, so the guard stayed and narrowed: an API render under either name
> is still refused, and `Notifications` is refused in both modes because §4.1
> gives it no Domain project at all — which is a second mode, and it comes off
> with the PR that builds it.

After:

> **The scaffold refuses `Shipping` without `--worker` and `Notifications`
> without `--pure-consumer`.** Documenting the gap left the script willing to
> render either in a shape §4.1 does not give it, which would have contradicted
> the chapter quietly. A note is not a guard, so the guard stayed and narrowed
> with each mode that joined.

The ninety-four and the forty-six are the API render's and do not move: no
template file changed what it tests.

- [ ] **Step 12: §7.5**

`docs/backend-architecture/07-persistence.md`, after the
`AddDomainEventDispatcher` code block and before "**The dispatcher performs no
I/O beyond staging rows.**":

> **A service §4.1 gives no Domain project is the one that registers a
> dispatcher of its own.** §4.5's pure-consumer mode renders it — a
> `NoDomainEventDispatcher` in the service's Application project that stages
> nothing — because §6.3's `TransactionBehavior` still calls a dispatcher, and
> the real one needs the collector, mapper and publisher such a service has no
> use for. Its premise is a gate rather than a hope: the service's architecture
> suite fails the day any type in it implements `IDomainEvent` or
> `IHasDomainEvents`, and that day the real dispatcher is owed.

Run `/check-links` and `/validate-blueprint`; `docs/change-locality.md` owes
the audit after a chapter edit, and a finding is fixed here.

- [ ] **Step 13: Commit, then the comment gate**

```bash
git add tools/new-service docs/backend-architecture/04-solution-structure.md \
        docs/backend-architecture/07-persistence.md
git commit -m "feat(scaffold): new_service.py --pure-consumer renders a Worker with no Domain project and no outbox"
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: exit 0. The gate reads `git show HEAD:<path>`, so it runs after the
commit. The body argues that the mode is omissions from one template, that the
dispatcher is a rendered file rather than a building-block helper because two
building blocks in one pull request is Class B, that the Dockerfiles were the
failure no suite sees, and that the broker grant is rendered because the
template's publisher grant is wrong by construction for this shape.

---

### Task 4: The gates are told by selector

**Files:**
- Modify: `deploy/observability/check.py` — check 8's selector
- Modify: `deploy/observability/README.md` — check 8's row and paragraph
- Modify: `deploy/compose/rabbitmq/check_permissions.py` — `publishes`, check 3
- Modify: `deploy/compose/rabbitmq/test_check_permissions.py`
- Modify: `tools/new-service/test_new_service.py` — `EveryGateSeesThePureConsumerRender`
- Modify: `.github/workflows/ci.yml` — the `scaffold-build` job's third render

**Interfaces:**
- Produces: `check.has_domain_project(service: Path) -> bool`;
  `check_permissions.publishes(service: str) -> bool`.

**What each gate looks at, read rather than assumed:**

| Gate | Its selector | A pure consumer under it |
|---|---|---|
| `deploy/observability/check.py` check 8 | services whose source registers `AddHostedService<OutboxDispatcher>` | passes vacuously today — nothing says the absence is meant — so the selector gains the Domain project, both ways |
| `check_permissions.py` check 3 | every `*-svc` account | demands a write on `Common.Contracts:IIntegrationEvent`, which the spec's narrowest grant does not hold, so it is owed only by a service with a Domain project, and a service without one is refused any contract write |
| `coverage.runsettings` | `.*\.Domain\.dll$` | sees nothing of the service; asserted, and left (see the self-review) |
| `licence_gate.py`, `secret_scan.py`, `comment_gate.py`, `pipeline_gate.py` | suffixes, prefixes and Dockerfile paths | cover the render with no change; asserted over it |
| the rendered architecture suites, `DependencyInjectionTests`, `MetricsRegistrationTests`, `MessageTypeMapValidatorTests`, `OutboxDispatcherTests` | the template's own anchors | patched, omitted or narrowed by Task 3's tables |

- [ ] **Step 1: Write the failing tests**

`deploy/compose/rabbitmq/test_check_permissions.py`, before
`AScaffoldedServiceIsNotRefused`:

```python
class AServiceThatPublishesNothing(unittest.TestCase):
    """The selector check 3 reads, over a tree of its own rather than the repository's."""

    def run_over_tree(self, domain: bool) -> bool:
        with tempfile.TemporaryDirectory() as directory:
            services = Path(directory)
            (services / "Probe" / "Probe.Infrastructure" / "Messaging").mkdir(parents=True)
            if domain:
                (services / "Probe" / "Probe.Domain").mkdir(parents=True)
                (services / "Probe" / "Probe.Domain" / "Probe.Domain.csproj").write_text("<Project />")
            original = gate.SERVICES
            gate.SERVICES = services
            try:
                return gate.publishes("Probe")
            finally:
                gate.SERVICES = original

    def test_a_service_with_a_domain_project_publishes(self):
        self.assertTrue(self.run_over_tree(domain=True))

    def test_a_service_with_no_domain_project_publishes_nothing(self):
        self.assertFalse(self.run_over_tree(domain=False))

    def test_the_selector_finds_a_publisher_in_the_repository(self):
        # The floor: a glob that matched nothing would call every service a pure consumer.
        self.assertTrue(any(gate.publishes(name) for name in gate.messaging_dirs()))
```

`tools/new-service/test_new_service.py`, after `RendersAPureConsumer`:

```python
class EveryGateSeesThePureConsumerRender(unittest.TestCase):
    """Each gate's own selector, over the render with no Domain project and no outbox."""

    @classmethod
    def setUpClass(cls):
        cls.rendered = pure_consumer()
        cls.paths = sorted({**cls.rendered.created, **cls.rendered.updated})

    def test_the_render_is_the_seven_projects_the_suite_is_about(self):
        projects = [p for p in self.paths if p.endswith(".csproj")]
        self.assertEqual(len(projects), 7, projects)

    def test_the_licence_gate_walks_every_rendered_project_file(self):
        gate = gate_module("licence-gate", "licence_gate.py")
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            apply(root, pure_consumer(repo_root=root))
            walked = {path.relative_to(root).as_posix() for path in gate.find_projects(root)}
        for path in (p for p in self.rendered.created if p.endswith(".csproj")):
            self.assertIn(path, walked, f"{path} is outside the licence gate's walk")

    def test_the_secret_scan_s_allow_list_covers_every_rendered_tree(self):
        gate = load_scan_gate(REPO_ROOT)
        covers = new_service.allow_list_trees(REPO_ROOT, gate)
        nested = [p for p in self.paths if "/" in p]
        self.assertTrue(nested)
        for path in nested:
            self.assertTrue(any(gate.covers_path(prefix, path) for prefix in covers), path)

    def test_the_comment_gate_reads_every_rendered_source_file(self):
        gate = comment_gate_module()
        unread = [p for p in self.rendered.created if gate.reader_for(p) is None]
        self.assertTrue(len(unread) < len(self.rendered.created))
        for path in unread:
            self.assertTrue(PurePosixPath(path).name == "Dockerfile" or path.endswith(".json"), path)

    def test_the_coverage_filter_sees_no_assembly_of_a_service_with_no_domain_project(self):
        # §12.9 measures the Domain assemblies, and §4.1 gives this shape none: the
        # filter's silence about it is the render's fact rather than the filter's miss.
        module_path = re.search(
            r"<ModulePath>(.+?)</ModulePath>",
            (REPO_ROOT / "coverage.runsettings").read_text(encoding="utf-8")).group(1)
        assemblies = [f"{PurePosixPath(p).stem}.dll" for p in self.rendered.created if p.endswith(".csproj")]
        self.assertTrue(assemblies)
        self.assertEqual([], [a for a in assemblies if re.search(module_path, a)])

    def test_the_architecture_gates_name_no_domain_and_hold_the_dispatcher_s_premise(self):
        worker = self.rendered.created[f"tests/{PROBE}.{new_service.WORKER_HOST}.Tests/ArchitectureTests.cs"]
        self.assertIn("public void Nothing_in_this_service_raises_a_domain_event()", worker)
        self.assertIn(f"typeof({PROBE}.Application.DependencyInjection).Assembly,", worker)
        application = self.rendered.created[f"tests/{PROBE}.Application.Tests/ArchitectureTests.cs"]
        for body in (worker, application):
            self.assertNotIn("AssemblyMarker", body)
            self.assertNotIn(f"{PROBE}.Domain", body)

    def test_the_pipeline_gate_would_see_both_rendered_dockerfiles(self):
        dockerfiles = [p for p in self.rendered.created if p.endswith("/Dockerfile")]
        self.assertEqual(
            sorted(PurePosixPath(p).parent.name for p in dockerfiles),
            [f"{PROBE}.Migrator", f"{PROBE}.{new_service.WORKER_HOST}"])

    def test_the_observability_gate_owes_it_no_outbox_gauges_and_owes_a_domain_one(self):
        # check.py's check 8 over a tree holding the template and this render: the
        # selector is the Domain project, so adding one is what makes a dispatcher owed.
        check = importlib.util.spec_from_file_location(
            "observability_check", REPO_ROOT / "deploy/observability/check.py")
        gate = importlib.util.module_from_spec(check)
        check.loader.exec_module(gate)
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            apply(root, pure_consumer(repo_root=root))
            gate.ROOT, gate.failures = root, []
            gate.check_outbox_metrics_per_service()
            self.assertEqual([], gate.failures)

            domain = root / f"src/Services/{PROBE}/{PROBE}.Domain/{PROBE}.Domain.csproj"
            domain.parent.mkdir(parents=True)
            domain.write_text("<Project />", encoding="utf-8")
            gate.failures = []
            gate.check_outbox_metrics_per_service()
            self.assertTrue(any(f.startswith(f"{PROBE} has a Domain project") for f in gate.failures), gate.failures)

    def test_the_broker_gate_reads_it_as_publishing_nothing(self):
        check = importlib.util.spec_from_file_location(
            "broker_check", REPO_ROOT / "deploy/compose/rabbitmq/check_permissions.py")
        gate = importlib.util.module_from_spec(check)
        check.loader.exec_module(gate)
        with tempfile.TemporaryDirectory() as directory:
            root = template_copy(Path(directory))
            apply(root, pure_consumer(repo_root=root))
            gate.SERVICES = root / "src" / "Services"
            self.assertFalse(gate.publishes(PROBE))
            self.assertTrue(gate.publishes(new_service.TEMPLATE))
```

`check.py` has no suite of its own and gains none here: its selector is
exercised from the scaffold's suite over a real render applied to a copy of
the template, which is the tree shape the gate will meet.

- [ ] **Step 2: Run them to see them fail**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
cd tools/new-service && py -3.12 -m unittest test_new_service.EveryGateSeesThePureConsumerRender
```

Expected: FAIL — `AttributeError: ... has no attribute 'publishes'` in both,
and `check 8` reporting nothing on the mutated tree, so the observability case
fails its second assertion.

- [ ] **Step 3: Check 8's selector**

`deploy/observability/check.py`, before `check_outbox_metrics_per_service`:

```python
def has_domain_project(service: Path) -> bool:
    """Whether §4.1 gives a service a Domain project, the selector for owing §9.4's outbox."""
    return any(service.glob(f"{service.name}.Domain/*.csproj"))
```

In the function, the collection. Before:

```python
    dispatching, instrumented = set(), set()
    for service in services:
        if not service.is_dir():
            continue
```

After:

```python
    dispatching, instrumented, owed = set(), set(), set()
    for service in services:
        if not service.is_dir():
            continue
        if has_domain_project(service):
            owed.add(service.name)
```

And before the existing `for name in sorted(dispatching - instrumented - …)`
loop:

```python
    # A Domain project raises the events §9.4's outbox carries, so its service
    # hosts the dispatcher; a service with none publishes nothing (§3.2), hosts
    # none, and is owed no gauges. Both directions, read from the tree.
    for name in sorted(owed - dispatching):
        fail(
            f"{name} has a Domain project and hosts no OutboxDispatcher, so the events "
            f"it raises reach no outbox (§9.4)")
    for name in sorted(dispatching - owed):
        fail(
            f"{name} hosts OutboxDispatcher with no Domain project; §4.1 gives such a "
            f"service nothing to publish (§3.2)")
```

The module docstring is not touched: it is one thirty-eight-line block, and the
comment gate judges a touched block whole.

- [ ] **Step 4: Check 3's selector**

`deploy/compose/rabbitmq/check_permissions.py`, before `contract_prefixes`:

```python
def publishes(service: str) -> bool:
    """Whether a service can publish at all: §4.1 gives it a Domain project.

    One with none raises no event for §9.3's mapper to translate (§3.2), so its
    account is owed no write on any `Common.Contracts` exchange.
    """
    return any((SERVICES / service).glob(f"{service}.Domain/*.csproj"))
```

Check 3. Before:

```python
        # 3. It declares and writes the framework's fault exchanges and the
        #    polymorphic interface exchange.
        for resource in (f"{FRAMEWORK_PREFIX}ReceiveFault", INTERFACE_EXCHANGE):
            for verb in ("configure", "write"):
                if not matches(entry[verb], resource):
                    fail(f"{user}: {verb} does not cover `{resource}`")
```

After:

```python
        # 3. It declares and writes the framework's fault exchanges and, if it
        #    publishes, the polymorphic interface exchange.
        service = next(k for k in directories if k.lower() == name)
        owed = (f"{FRAMEWORK_PREFIX}ReceiveFault", *((INTERFACE_EXCHANGE,) if publishes(service) else ()))
        for resource in owed:
            for verb in ("configure", "write"):
                if not matches(entry[verb], resource):
                    fail(f"{user}: {verb} does not cover `{resource}`")

        # 3b. A service that publishes nothing writes no contract exchange at
        #     all, the interface one and its own context's included (ADR-036).
        if not publishes(service):
            for resource in (INTERFACE_EXCHANGE, f"{owned_contract(user)}Anything"):
                if matches(entry["write"], resource):
                    fail(f"{user}: write COVERS `{resource}`, and the service has no Domain "
                         f"project to publish from (§4.1, §3.2)")
```

`SERVICES` is already in `SOURCE_INPUTS`, so check 7's workflow-coverage
assertion needs nothing.

**The selector is the code's; whether MassTransit's consume topology ever
writes the interface exchange is the broker's.** The gate's own comment on
`INTERFACE_EXCHANGE` says the binding to it is the sender's, and nothing in
this PR consumes; the measurement is PR-4's broker-binding test over a live
broker, and the self-review says what changes if it refuses.

- [ ] **Step 5: The observability README**

`deploy/observability/README.md`, check 8's row:

```markdown
| 8 | Every service with a Domain project hosts §9.4's dispatcher and publishes its gauges, or is on a declared exemption; one with none hosts neither |
```

and at the end of the "**Check 8 exists because…**" paragraph:

> **Which services owe a dispatcher is read from the tree**: one with a Domain
> project raises the events §9.4's outbox carries, and one with none — §4.1's
> pure consumer — publishes nothing, so it hosts no dispatcher and is owed no
> gauges. Both directions fail here too.

- [ ] **Step 6: The scaffold-build job's third render**

`.github/workflows/ci.yml`, after the "Compile the worker it rendered" step:

```yaml
      # And the pure consumer, whose tables cut what the other two keep: a
      # template change that leans on the outbox or the Domain project compiles
      # in both of those and fails here. It publishes no port either.
      - name: Render a pure consumer
        run: python tools/new-service/new_service.py Whiskey --pure-consumer

      - name: Compile the pure consumer it rendered
        run: dotnet build tests/Whiskey.Worker.Tests/Whiskey.Worker.Tests.csproj
```

`Whiskey` beside `Yankee` and `Xray`: a NATO probe no service will take, and
one with no port to collide.

- [ ] **Step 7: Run every suite and gate this touched**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 deploy/observability/check.py
cd tools/new-service && py -3.12 -m unittest
```

Expected: green; both gates exit 0 over today's tree, where every service has
a Domain project and a dispatcher.

- [ ] **Step 8: Commit**

```bash
git add deploy/observability/check.py deploy/observability/README.md \
        deploy/compose/rabbitmq/check_permissions.py deploy/compose/rabbitmq/test_check_permissions.py \
        tools/new-service/test_new_service.py .github/workflows/ci.yml
git commit -m "feat(gates): check 8 and broker check 3 select by a Domain project, and the suite asserts each gate over a pure render"
```

---

### Task 5: Render Notifications

**Files:**
- Create (by the script): `src/Services/Notifications/**`,
  `tests/Notifications.*/**`, `deploy/compose/services/notifications.yml`
- Modify (by the script): `Platform.slnx`, `deploy/compose/docker-compose.yml`,
  `deploy/compose/docker-compose.infra-only.yml`, `deploy/compose/.env.example`,
  `deploy/compose/rabbitmq/definitions.json`,
  `.github/secret-scan/allowed/deploy.txt`, `.github/secret-scan/allowed/tests.txt`

**Interfaces:**
- Produces: `Notifications.Worker`, `Notifications.Application`,
  `Notifications.Infrastructure`, `Notifications.Migrator`;
  `NotificationsDbContext` with default schema `notifications`;
  `AddNotificationsApplication()`, `AddNotificationsInfrastructure(IConfiguration)`;
  `Notifications.Application.NoDomainEventDispatcher` (internal);
  `ServiceFixture`, `NotificationsWorkerFactory`, `TestAuthHandler` in
  `tests/Notifications.TestSupport`; the broker account `notifications-svc`
  with `configure`/`read` `^(notifications-|Common\.Contracts|MassTransit:)`
  and `write` `^(notifications-|MassTransit:)`.

- [ ] **Step 1: Confirm the tree is clean**

`git status --short` (expect empty) and
`grep -rn "Notifications" deploy/compose/services/ Platform.slnx` (expect no
match).

- [ ] **Step 2: Run the scaffold**

```bash
py -3.12 tools/new-service/new_service.py Notifications --pure-consumer
```

No `--migration-id`: the default is the current UTC time, and Task 9's
`migrations add` must sort after every id this writes. Expected:
`Notifications: 52 files created, 7 updated, publishing no port.`

- [ ] **Step 3: Build and run the rendered suites**

```bash
dotnet restore Platform.slnx && dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
dotnet test tests/Notifications.Worker.Tests
```

Expected: 0 warnings; 16 and 61 green, 37 of the second against containers.
A failure on `Failed to connect to Docker endpoint` is the daemon.

- [ ] **Step 4: The scan and the gates over the render**

```bash
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 deploy/observability/check.py
```

Expected: all exit 0. The broker gate passes because Task 4 made check 3 read
the Domain project; the observability gate because Notifications has neither
a Domain project nor a dispatcher.

- [ ] **Step 5: Commit the render alone**

```bash
git add -A
git commit -m "feat(notifications): sixth service from the scaffold's pure-consumer mode"
```

Nothing else goes in this commit — not the Redis strip, not a comment, not
§13.2's list — because Task 11's `--verify` compares it byte for byte with
what the parent's scaffold renders. Rendered text reads `Notifications's`
where the template said `Catalog's` (the fixture's summary, `.env.example`, an
allow-list reason); it is left as rendered, for the same reason.

---

### Task 6: Notifications has no Redis, and §2 says so

**Files:**
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Idempotency/NoClaimsIdempotencyStore.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Notifications.Infrastructure.csproj`
- Modify: `deploy/compose/services/notifications.yml`
- Modify: `.github/secret-scan/allowed/deploy.txt` — the two Redis rows the render wrote
- Modify: `tests/Notifications.TestSupport/ServiceFixture.cs`,
  `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`
- Modify: `tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs`,
  `tests/Notifications.Worker.Tests/MetricsRegistrationTests.cs`,
  `tests/Notifications.Worker.Tests/HostSmokeTests.cs`,
  `tests/Notifications.Worker.Tests/RetentionPurgeTests.cs`
- Create: `tests/Notifications.Worker.Tests/NoRedisTests.cs`,
  `tests/Notifications.Worker.Tests/NoClaimsIdempotencyStoreTests.cs`
- Modify: `docs/backend-architecture/02-architecture-at-a-glance.md`

**Interfaces:**
- Produces: `new NotificationsWorkerFactory(string connectionString, string
  rabbitConnectionString)` — two parameters, as every later plan uses it.

The same cut Shipping made, on Shipping's terms: the purge still resolves
`IIdempotencyStore` for ADR-039's marker purge, so the service registers the
store that never claims; the two purge tests that took a live Redis claim
leave, and the held-key batching test substitutes a store reporting one key
held. Every anchor below is rendered text.

- [ ] **Step 1: Inventory every rendered Redis mention**

```bash
grep -rn -i "redis\|IdempotencyClaims" src/Services/Notifications tests/Notifications.* deploy/compose/services/notifications.yml
```

Every hit is one Step 4 names: the `using` and the `AddRedisConnections` call,
the two readiness rows, the package, the Compose unit's two keys and two
`depends_on` entries, the factory's parameters, constant and settings, the
fixture's flag and arguments, two suites' configuration keys, the readiness
test's two lookups, and three purge tests' claims. `ArchitectureTests.cs`
names `StackExchange.Redis` in its forbidden list and stays. A hit of any
other kind is a stop.

- [ ] **Step 2: Write the failing tests**

`tests/Notifications.Worker.Tests/NoRedisTests.cs`:

```csharp
using Common.Infrastructure.Messaging;
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Notifications reaches neither Redis instance: it takes no §8.5 key and caches nothing (§2).</summary>
public sealed class NoRedisTests
{
    [Fact]
    public void The_host_starts_with_no_redis_key_and_registers_no_connection()
    {
        using NotificationsWorkerFactory factory = new(
            "Server=sql.invalid;Database=Notifications;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://notifications-svc:x@rabbit.invalid:5672");

        IServiceProvider services = factory.Services;

        // By name, not by a package reference: a test proving the service has no
        // Redis should not be the thing that gives its project one. The type is
        // still loadable, because Common.Infrastructure carries the package.
        Type multiplexer = Type.GetType("StackExchange.Redis.IConnectionMultiplexer, StackExchange.Redis")
            ?? throw new InvalidOperationException(
                "StackExchange.Redis did not load; the assertions below would prove nothing.");
        IKeyedServiceProvider keyed = (IKeyedServiceProvider)services;

        services.GetService(multiplexer).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Cache).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Coordination).ShouldBeNull();

        // RetentionPurgeService resolves IIdempotencyStore for ADR-039's purge, which this proves works without Redis.
        services.GetRequiredService<RetentionPurgeService>().ShouldNotBeNull();
    }
}
```

`tests/Notifications.Worker.Tests/NoClaimsIdempotencyStoreTests.cs`:

```csharp
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Notifications' own store never claims or holds, so ADR-039's purge resolves one without Redis.</summary>
public sealed class NoClaimsIdempotencyStoreTests
{
    private static NotificationsWorkerFactory Factory() =>
        new(
            "Server=sql.invalid;Database=Notifications;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://notifications-svc:x@rabbit.invalid:5672");

    [Fact]
    public async Task UnheldAsync_reports_every_key_it_is_given()
    {
        using NotificationsWorkerFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        string[] keys = ["a", "b"];

        IReadOnlyCollection<string> unheld = await store.UnheldAsync(keys, TestContext.Current.CancellationToken);

        unheld.ShouldBe(keys, ignoreOrder: true);
    }

    [Fact]
    public async Task A_claim_attempt_throws()
    {
        using NotificationsWorkerFactory factory = Factory();
        IIdempotencyStore store = factory.Services.GetRequiredService<IIdempotencyStore>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => store.TryClaimAsync("k", TimeSpan.FromHours(1), TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 3: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NoRedisTests|FullyQualifiedName~NoClaimsIdempotencyStoreTests"
```

Expected: compile failure — the factory takes four arguments — and, once Step
4's factory edit is in, a host that throws naming a missing Redis key.

- [ ] **Step 4: Cut every hit**

`Notifications.Infrastructure/DependencyInjection.cs`. The usings: add
`using Notifications.Infrastructure.Idempotency;` above
`using Notifications.Infrastructure.Messaging;` and remove
`using Common.Infrastructure.Redis;`. After the marker store's registration:

```csharp
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §2: no Redis. The purge still asks the claim store (ADR-039), so this one never claims.
        services.AddSingleton<IIdempotencyStore, NoClaimsIdempotencyStore>();
```

Remove, with the blank line after it:

```csharp
        // §8's two connections, one call by design (§8.2). Both strings are read eagerly, so a missing key
        // stops the host.
        services.AddRedisConnections(configuration);
```

Readiness. Before:

```csharp
        // Readiness (§13.5). Both Redis rows: AbortOnConnectFail is false, and §8.1 puts the two instances on
        // different servers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Notifications")!, name: "sql", tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Cache)!,
                name: "redis-cache",
                tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Coordination)!,
                name: "redis-coordination",
                tags: ["ready"]);
```

After:

```csharp
        // Readiness (§13.5): SQL here, and the bus check AddMassTransit registers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Notifications")!, name: "sql", tags: ["ready"]);
```

`Idempotency/NoClaimsIdempotencyStore.cs`:

```csharp
using Common.Application;

namespace Notifications.Infrastructure.Idempotency;

/// <summary>Notifications has no IIdempotentCommand, so nothing claims a key; the purge calls this (ADR-039).</summary>
internal sealed class NoClaimsIdempotencyStore : IIdempotencyStore
{
    public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
        throw NoIdempotentCommand();

    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
        throw NoIdempotentCommand();

    public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct) =>
        throw NoIdempotentCommand();

    public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
        throw NoIdempotentCommand();

    // No claim is ever taken, so every key the purge asks about is unheld (ADR-039).
    public Task<IReadOnlyCollection<string>> UnheldAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        Task.FromResult(keys);

    private static InvalidOperationException NoIdempotentCommand() =>
        new("Notifications has no IIdempotentCommand (§8.5); giving it one means registering the " +
            "Redis-backed IIdempotencyStore, not this one.");
}
```

`Notifications.Infrastructure.csproj`: remove
`    <PackageReference Include="AspNetCore.HealthChecks.Redis" />`, and the
building-block reference's comment. Before:

```xml
    <!-- §9.5's inbox and its purge, and §8's Redis helpers beside them. -->
```

After:

```xml
    <!-- §9.5's inbox and its purge, and §8.5's marker store; this service reaches no Redis (§2). -->
```

`deploy/compose/services/notifications.yml`, remove from the worker's
`environment:` this four-line comment and the two lines under it, the
`ConnectionStrings__RedisCache` and `ConnectionStrings__RedisCoordination`
keys (their values are not quoted here, because §15.1's scan reads this plan
too and would want an allow-list entry for a literal the plan does not need):

```yaml
      # §8.1's two connections, both read eagerly by AddRedisConnections (§8.2),
      # so an absent key is a host that will not start rather than a cache
      # silently reading the database. 6379 on both: each container listens on
      # Redis's own port, and 6380 exists only on the host side.
```

and from its `depends_on:`:

```yaml
      # service_healthy, not service_started: AddRedisConnections sets
      # AbortOnConnectFail = false (§8.1), so the host would start against a
      # Redis still booting and its first command would fail on §8.5's claim.
      redis-cache: { condition: service_healthy }
      redis-coordination: { condition: service_healthy }
```

`.github/secret-scan/allowed/deploy.txt`, remove the two rows the render
wrote, which would otherwise be stale entries the scan refuses:

```text
deploy/compose/services/notifications.yml | credential-assignment | 12e48b0a29c6 | A Redis cache endpoint. Host and port only, no credential in it.
deploy/compose/services/notifications.yml | credential-assignment | 7dd6402e12f4 | A Redis coordination endpoint. Host and port only, no credential.
```

`tests/Notifications.TestSupport/ServiceFixture.cs`: the base call's
`("Notifications", redis: true)` becomes `("Notifications")`, and the factory.
Before:

```csharp
    // Real servers rather than the factory's unreachable default, because §8.5's store claims keys on one.
    protected override NotificationsWorkerFactory CreateFactory() =>
        new(ConnectionString, BrokerConnectionString, RedisCacheConnectionString, RedisCoordinationConnectionString);
```

After:

```csharp
    protected override NotificationsWorkerFactory CreateFactory() => new(ConnectionString, BrokerConnectionString);
```

`tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`: remove
`using Common.Infrastructure.Redis;`; the primary constructor becomes

```csharp
public class NotificationsWorkerFactory(string connectionString, string rabbitConnectionString)
    : WebApplicationFactory<Program>
```

and remove the `UnreachableRedis` constant with its summary and remarks, and
the two `UseSetting` calls that fed it:

```csharp
            .UseSetting(
                $"ConnectionStrings:{RedisConnections.Cache}",
                redisCacheConnectionString ?? UnreachableRedis)
            .UseSetting(
                $"ConnectionStrings:{RedisConnections.Coordination}",
                redisCoordinationConnectionString ?? UnreachableRedis)
```

`DatabaseSmokeTests.cs` and `MetricsRegistrationTests.cs`, the same edit in
each. Before:

```csharp
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@notifications-rabbit.invalid:5672",
                    // AddRedisConnections throws without both, unreachable on the same convention.
                    ["ConnectionStrings:RedisCache"] = "notifications-redis.invalid:6379",
                    ["ConnectionStrings:RedisCoordination"] = "notifications-redis.invalid:6380"
```

After:

```csharp
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@notifications-rabbit.invalid:5672"
```

`HostSmokeTests.cs`, the readiness test. Before:

```csharp
    public void Ready_probe_reports_the_sql_redis_and_bus_checks()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // The count is the assertion, since a readiness check dropped from a growing list turns nothing red.
        options.Registrations.Count.ShouldBe(4);

        HealthCheckRegistration sql = options.Registrations.Single(r => r.Name == "sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        // A host with a connection string has a readiness check (§13.5), and §8.1 puts the two Redis instances
        // on different servers.
        HealthCheckRegistration cache = options.Registrations.Single(r => r.Name == "redis-cache");
        cache.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

        HealthCheckRegistration coordination =
            options.Registrations.Single(r => r.Name == "redis-coordination");
        coordination.Tags.ShouldContain("ready", "§8.5's claims are written to this instance");

```

After:

```csharp
    public void Ready_probe_reports_the_sql_and_bus_checks()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // The count is the assertion, since a readiness check dropped from a growing list turns nothing red.
        options.Registrations.Count.ShouldBe(2);

        HealthCheckRegistration sql = options.Registrations.Single(r => r.Name == "sql");
        sql.Tags.ShouldContain("ready", "an untagged check is invisible to the /health/ready predicate");

```

and at the test's end, after the bus tag assertions:

```csharp
        bus.Tags.ShouldContain("masstransit", "both tags are the documented contract (§13.5), so both are pinned");

        // By name, since a dependency added to readiness later is a rollout the relay could block (§15.3).
        options.Registrations.Select(r => r.Name).ShouldBe(["sql", "masstransit-bus"], ignoreOrder: true);
    }
```

This is the readiness half of the spec's section 11 claim — SQL and the bus —
pinned from the first commit; the relay and Keycloak are absent because
nothing registers them yet, and PR-2 and PR-3 inherit the assertion.

`RetentionPurgeTests.cs`: delete
`A_marker_whose_claim_is_still_held_survives_however_old_the_row_is` and
`The_same_row_goes_once_the_claim_behind_it_has_been_released` whole, from
each `[Fact]` to its closing brace and the blank line after; in
`A_held_key_costs_its_own_batch_and_not_the_rest_of_the_pass`, delete the
claim:

```csharp
        (await fixture.IdempotencyClaims.TryClaimAsync(
            held,
            IdempotencyRetention.Window,
            TestContext.Current.CancellationToken)).ShouldNotBeNull();

```

and replace its pass. Before:

```csharp
        (await fixture.PurgeWithAsync(twoAtATime)).Idempotency.ShouldBe(
            4,
            "stopping on a partial batch would have ended the pass at three");

        IdempotencyMarker survivor = (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
        survivor.Key.ShouldBe(held, "the row whose claim is still live is the one that stays");
```

After:

```csharp
        (await fixture.PurgeWithAsync(twoAtATime, new WithOneKeyHeld(held))).Idempotency.ShouldBe(
            4,
            "stopping on a partial batch would have ended the pass at three");

        IdempotencyMarker survivor = (await fixture.IdempotencyMarkersAsync()).ShouldHaveSingleItem();
        survivor.Key.ShouldBe(held, "the row the store still reports held is the one that stays");
```

and after `ReplacingClaims`, before the class's closing brace:

```csharp
    /// <summary>Reports one key held and every other unheld, as a live claim would (ADR-039).</summary>
    private sealed class WithOneKeyHeld(string held) : IIdempotencyStore
    {
        public Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task CompleteAsync(string key, string claim, string payload, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task ReleaseAsync(string key, string claim, CancellationToken ct) =>
            throw new NotSupportedException("This double answers only UnheldAsync.");

        public Task<IReadOnlyCollection<string>> UnheldAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
            Task.FromResult<IReadOnlyCollection<string>>([.. keys.Where(k => k != held)]);
    }
```

`A_marker_replaced_between_the_select_and_the_delete_is_not_the_row_that_goes`
keeps `fixture.IdempotencyClaims`, which now resolves the no-claims store; it
reports every key unheld, which is all the replacement case asks of it.

- [ ] **Step 5: §2's sentence**

`docs/backend-architecture/02-architecture-at-a-glance.md`. Before:

> revoked token with no error. Payments and Shipping reach neither: both
> cache nothing, and §8.5's keys belong to HTTP write commands, which neither
> has — Payments' idempotency is the payment provider's key and its own rows,
> and Shipping's is the lease its workers take in SQL.

After:

> revoked token with no error. Payments, Shipping and Notifications reach
> neither: none caches anything, and §8.5's keys belong to HTTP write commands,
> which none has — Payments' idempotency is the payment provider's key and its
> own rows, Shipping's is the lease its workers take in SQL, and Notifications'
> is §9.5's inbox and the unique key on its record.

Run `/check-links` and `/validate-blueprint`.

- [ ] **Step 6: Run the suites and the scan**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: 0 warnings; 62 green (61, less the two claim tests, plus three);
the scan exits 0 with no stale entry.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Notifications tests/Notifications.* deploy/compose/services/notifications.yml \
        .github/secret-scan/allowed/deploy.txt docs/backend-architecture/02-architecture-at-a-glance.md
git commit -m "feat(notifications): Notifications drops AddRedisConnections and registers NoClaimsIdempotencyStore, and §2 says it reaches no Redis"
```

---

### Task 7: The broker account, held to its narrowness

**Files:**
- Modify: `deploy/compose/rabbitmq/test_check_permissions.py`

Task 5 rendered `notifications-svc` with a consumer's grant and Task 4 taught
check 3 what that grant owes. What is left is the case the gate's suite could
not hold until a service with no Domain project existed: a contract write on
that account, refused.

- [ ] **Step 1: Write the test**

In `AServiceThatPublishesNothing`:

```python
    def test_a_service_that_publishes_nothing_is_refused_a_contract_write(self):
        definitions = real()
        consumers = [name for name in gate.messaging_dirs() if not gate.publishes(name)]
        self.assertTrue(consumers, "no service in the tree publishes nothing: the selector, or the tree")
        entry = permission(definitions, f"{consumers[0].lower()}-svc")
        entry["write"] = entry["write"].replace("|MassTransit:", "|Common\\.Contracts|MassTransit:")

        failures = run_against(definitions)
        self.assertTrue(
            any("no Domain project to publish from" in f for f in failures),
            f"the gate accepted a contract write for a service that publishes nothing: {failures}")
```

The consumer is found by the selector, not named. The floor assertion is what
keeps it from passing over an empty list.

- [ ] **Step 2: Run it, then mutate the gate to see it go red**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
```

Expected: green. Then, temporarily, delete the `3b` block from
`check_permissions.py` and rerun: the new case fails naming the account.
Restore the block; an exit code alone makes a vacuous test.

- [ ] **Step 3: Run the gate**

```bash
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Expected: `broker permission gate: OK`.

- [ ] **Step 4: Commit**

```bash
git add deploy/compose/rabbitmq/test_check_permissions.py
git commit -m "test(broker): a contract write on an account whose service publishes nothing is refused"
```

The body says the account is §10's narrowest — its own endpoints and the fault
exchanges, no contract exchange — that the scaffold rendered it, and that
whether MassTransit's consume topology ever writes the interface exchange is
measured by PR-4's live binding test, not assumed here.

---

### Task 8: The `Notification` record and its state rules

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Records/NotificationStatus.cs`
- Create: `src/Services/Notifications/Notifications.Application/Records/NotificationReasons.cs`
- Create: `src/Services/Notifications/Notifications.Application/Records/NotificationLimits.cs`
- Create: `src/Services/Notifications/Notifications.Application/Records/Notification.cs`
- Test: `tests/Notifications.Application.Tests/NotificationTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Notifications.Application.Records;
public enum NotificationStatus { Pending, Sent, Suppressed, Undeliverable }
public static class NotificationReasons
{
    public const string NoSuchCustomer = "no_such_customer";
    public const string RecipientRefused = "recipient_refused";
    public const string GaveUp = "gave_up";
    public const string Erased = "erased";
    public static IReadOnlySet<string> All { get; }
}
public static class NotificationLimits
{
    public const int MaxTemplateKeyLength = 64;
    public const int MaxLanguagesLength = 32;
    public const int MaxReasonLength = 32;
    public const int MaxParametersLength = 2000;
}
public sealed class Notification
{
    public static Notification Pending(
        Guid eventId, string templateKey, Guid orderId, string parameters, DateTimeOffset now);
    public bool AssignCustomer(Guid customerId);
    public bool Suppress(DateTimeOffset now);
    public bool MarkUndeliverable(string reason, DateTimeOffset now);
    public bool StartSend(int templateVersion, string languages, DateTimeOffset now);
    public bool MarkSent(DateTimeOffset now);
}
```

The namespace is `Records`, not `Notifications`: a namespace
`Notifications.Application.Notifications` would make `Notifications` inside it
resolve to itself first.

**The moves, against the spec's section 5 table.** Each row is a method but
the `not_a_mailbox` row, which is left to PR-5: it adds the reason to
`NotificationReasons.All` with the send that meets it. Every other arrival
returns `false` and changes nothing, never a throw, because a throw from the
worker is a row retried for ever. Three decisions the table leaves to the
code, each with a test:

- **`AssignCustomer`** is a move the table does not list: section 6 says the
  customer's id is copied from the order record when the worker resolves it,
  and `NULL` until then, so something has to set it. Once only.
- **`MarkSent` requires the intent.** Section 4 commits `SendStartedAt` before
  the send, so an acceptance on a row without it is a pass out of order.
- **`StartSend` stamps once.** Section 4 resends a row claimed with the intent
  set "under the same `Message-ID`", so the second call keeps the first
  version and languages rather than restamping them.

`erased` joins the closed set because section 6's erasure path marks waiting
rows `Undeliverable: erased`; it is the same move with its own reason, and the
table-driven test drives it.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/NotificationTests.cs`:

```csharp
using Notifications.Application.Records;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The record's moves, a row per move, and every arrival the row has outgrown as a no-op (ADR-052).</summary>
public class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(5);

    private static Notification Pending() =>
        Notification.Pending(Guid.CreateVersion7(), "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now);

    private static Notification Started()
    {
        Notification notification = Pending();
        notification.StartSend(1, "en", Now).ShouldBeTrue();
        return notification;
    }

    /// <summary>Each move from <c>Pending</c>, with the state it leaves and the reason it stamps.</summary>
    public static TheoryData<string, Func<Notification, bool>, NotificationStatus, string?> Moves => new()
    {
        { "a decline the customer cancelled", n => n.Suppress(Later), NotificationStatus.Suppressed, null },
        {
            "the owner answers that the customer does not exist",
            n => n.MarkUndeliverable(NotificationReasons.NoSuchCustomer, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.NoSuchCustomer
        },
        {
            "the relay refuses the recipient for good",
            n => n.MarkUndeliverable(NotificationReasons.RecipientRefused, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.RecipientRefused
        },
        {
            "the give-up age passes",
            n => n.MarkUndeliverable(NotificationReasons.GaveUp, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.GaveUp
        },
        {
            "the customer is erased",
            n => n.MarkUndeliverable(NotificationReasons.Erased, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.Erased
        },
        { "the send is about to start", n => n.StartSend(2, "en,kk", Later), NotificationStatus.Pending, null },
    };

    [Fact]
    public void An_event_owes_a_pending_notification_naming_nobody_yet()
    {
        Notification notification = Pending();

        notification.Status.ShouldBe(NotificationStatus.Pending);
        notification.CustomerId.ShouldBeNull("the id is copied from the order record, which may not exist yet");
        notification.SendStartedAt.ShouldBeNull();
        notification.CompletedAt.ShouldBeNull();
        notification.Attempts.ShouldBe(0);
        notification.NextAttemptAt.ShouldBe(Now, "a new row is due at once");
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void Each_move_from_pending_lands_where_the_record_says(
        string because,
        Func<Notification, bool> move,
        NotificationStatus status,
        string? reason)
    {
        Notification notification = Pending();

        move(notification).ShouldBeTrue(because);

        notification.Status.ShouldBe(status, because);
        notification.Reason.ShouldBe(reason, because);
        if (status == NotificationStatus.Pending)
            notification.CompletedAt.ShouldBeNull(because);
        else
            notification.CompletedAt.ShouldBe(Later, because);
    }

    [Fact]
    public void The_intent_stamps_the_version_and_languages_it_rendered()
    {
        Notification notification = Pending();

        notification.StartSend(2, "en,kk", Later).ShouldBeTrue();

        notification.TemplateVersion.ShouldBe(2);
        notification.Languages.ShouldBe("en,kk");
        notification.SendStartedAt.ShouldBe(Later);
    }

    [Fact]
    public void The_relay_accepting_a_started_send_makes_it_sent()
    {
        Notification notification = Started();

        notification.MarkSent(Later).ShouldBeTrue();

        notification.Status.ShouldBe(NotificationStatus.Sent);
        notification.CompletedAt.ShouldBe(Later);
    }

    [Fact]
    public void A_send_whose_intent_was_never_committed_is_not_sent()
    {
        // The intent is committed before the send (ADR-052), so an acceptance without one is a pass out of order.
        Notification notification = Pending();

        notification.MarkSent(Later).ShouldBeFalse();

        notification.Status.ShouldBe(NotificationStatus.Pending);
    }

    [Fact]
    public void A_second_intent_keeps_the_first()
    {
        // A row claimed with the intent already set is resent under it, never restamped.
        Notification notification = Started();

        notification.StartSend(3, "ru", Later).ShouldBeFalse();

        notification.TemplateVersion.ShouldBe(1);
        notification.Languages.ShouldBe("en");
        notification.SendStartedAt.ShouldBe(Now);
    }

    [Fact]
    public void The_customer_is_named_once()
    {
        Notification notification = Pending();
        Guid first = Guid.CreateVersion7();

        notification.AssignCustomer(first).ShouldBeTrue();
        notification.AssignCustomer(Guid.CreateVersion7()).ShouldBeFalse();

        notification.CustomerId.ShouldBe(first);
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void Every_arrival_on_a_terminal_row_is_a_no_op_rather_than_a_throw(
        string because,
        Func<Notification, bool> move,
        NotificationStatus status,
        string? reason)
    {
        _ = status;
        _ = reason;

        foreach (Notification terminal in Terminals())
        {
            NotificationStatus before = terminal.Status;
            string? reasonBefore = terminal.Reason;

            move(terminal).ShouldBeFalse(because);
            terminal.MarkSent(Later).ShouldBeFalse(because);
            terminal.AssignCustomer(Guid.CreateVersion7()).ShouldBeFalse(because);

            terminal.Status.ShouldBe(before, because);
            terminal.Reason.ShouldBe(reasonBefore, because);
        }
    }

    [Fact]
    public void A_reason_outside_the_closed_set_is_the_caller_s_defect()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => Pending().MarkUndeliverable("relay_down", Later));
    }

    [Fact]
    public void A_value_the_columns_cannot_hold_is_refused_at_the_door()
    {
        Should.Throw<ArgumentException>(() =>
            Notification.Pending(Guid.CreateVersion7(), " ", Guid.CreateVersion7(), """{"v":1}""", Now));
        Should.Throw<ArgumentException>(() =>
            Notification.Pending(Guid.CreateVersion7(), "order-placed", Guid.CreateVersion7(), "", Now));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Notification.Pending(
                Guid.CreateVersion7(),
                new string('k', NotificationLimits.MaxTemplateKeyLength + 1),
                Guid.CreateVersion7(),
                """{"v":1}""",
                Now));
        Should.Throw<ArgumentOutOfRangeException>(() => Pending().StartSend(0, "en", Later));
        Should.Throw<ArgumentOutOfRangeException>(() =>
            Pending().StartSend(1, new string('x', NotificationLimits.MaxLanguagesLength + 1), Later));
    }

    private static IEnumerable<Notification> Terminals()
    {
        Notification suppressed = Pending();
        suppressed.Suppress(Now);

        Notification undeliverable = Pending();
        undeliverable.MarkUndeliverable(NotificationReasons.GaveUp, Now);

        Notification sent = Started();
        sent.MarkSent(Now);

        return [suppressed, undeliverable, sent];
    }
}
```

The `_ = status; _ = reason;` discards are what the shared theory data costs:
the terminal case reuses the moves and ignores what they land on, and xUnit
v3's analyser refuses a theory parameter nothing reads.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests
```

Expected: compile failure on every type in `Notifications.Application.Records`.

- [ ] **Step 3: The status, the reasons and the widths**

`Records/NotificationStatus.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>Where a notification stands; every state but <c>Pending</c> is terminal (ADR-052).</summary>
/// <remarks>Stored by name, never by number (§7.2), so the member order is no storage contract.</remarks>
public enum NotificationStatus
{
    Pending,
    Sent,
    Suppressed,
    Undeliverable,
}
```

`Records/NotificationReasons.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>Why a notification is undeliverable, a closed set ADR-052's outcomes and §11.7's erasure name.</summary>
public static class NotificationReasons
{
    public const string NoSuchCustomer = "no_such_customer";
    public const string RecipientRefused = "recipient_refused";
    public const string GaveUp = "gave_up";
    public const string Erased = "erased";

    /// <summary>Every reason a row may carry, so a caller's typo is refused rather than stored.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NoSuchCustomer, RecipientRefused, GaveUp, Erased };
}
```

`Records/NotificationLimits.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>The widths a notification's strings are stored at, named once so the record and columns agree.</summary>
public static class NotificationLimits
{
    public const int MaxTemplateKeyLength = 64;
    public const int MaxLanguagesLength = 32;
    public const int MaxReasonLength = 32;
    public const int MaxParametersLength = 2000;
}
```

Sixty-four holds the longest of section 7's keys, `shipment-dispatched`,
three times over; thirty-two holds every subset of `en,kk,ru` and the language
sets ADR-053 rule 2 lets a deployment add; two thousand is bounded rather
than `max` so a stored payload cannot grow without a migration saying so.

- [ ] **Step 4: The record**

`Records/Notification.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>One row of §3.2's <c>NotificationLog</c>: a notice owed for one event, and what became of it.</summary>
/// <remarks>
/// ADR-053 rule 4's record, naming nobody but the customer's id. Each move returns whether it moved the row, and
/// an arrival the row has outgrown returns false, because a throw is a row its worker retries for ever (ADR-052).
/// </remarks>
public sealed class Notification
{
    public Guid NotificationId { get; private set; }

    /// <summary>The event the consumer wrote this row for; with the key it is unique (§9.5's second line).</summary>
    public Guid EventId { get; private set; }

    public string TemplateKey { get; private set; } = "";

    public Guid OrderId { get; private set; }

    /// <summary>Copied from the order record when the worker resolves it, and null until then (ADR-053).</summary>
    public Guid? CustomerId { get; private set; }

    /// <summary>The version rendered, stamped with the intent so the row names what was sent (ADR-053).</summary>
    public int? TemplateVersion { get; private set; }

    public string? Languages { get; private set; }

    /// <summary>The values the template's placeholders take, as their own writer formats them.</summary>
    public string Parameters { get; private set; } = "";

    public NotificationStatus Status { get; private set; }

    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The intent, committed before the send, so a resend is a row that already carries it.</summary>
    public DateTimeOffset? SendStartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    // EF Core materialisation only (§5.4).
    private Notification() { }

    private Notification(Guid eventId, string templateKey, Guid orderId, string parameters, DateTimeOffset now)
    {
        NotificationId = Guid.CreateVersion7();
        EventId = eventId;
        TemplateKey = templateKey;
        OrderId = orderId;
        Parameters = parameters;
        Status = NotificationStatus.Pending;
        CreatedAt = now;
        NextAttemptAt = now;
    }

    /// <summary>An event's consumer owes a notice: the record's first row.</summary>
    public static Notification Pending(
        Guid eventId,
        string templateKey,
        Guid orderId,
        string parameters,
        DateTimeOffset now)
    {
        Require(templateKey, NotificationLimits.MaxTemplateKeyLength, nameof(templateKey));
        Require(parameters, NotificationLimits.MaxParametersLength, nameof(parameters));

        return new Notification(eventId, templateKey, orderId, parameters, now);
    }

    /// <summary>The order record named the customer; a second answer does not overwrite the first.</summary>
    public bool AssignCustomer(Guid customerId)
    {
        if (Status != NotificationStatus.Pending || CustomerId is not null)
            return false;

        CustomerId = customerId;
        return true;
    }

    /// <summary>A decline whose order the customer cancelled is never sent (ADR-049).</summary>
    public bool Suppress(DateTimeOffset now)
    {
        if (Status != NotificationStatus.Pending)
            return false;

        Status = NotificationStatus.Suppressed;
        CompletedAt = now;
        return true;
    }

    /// <summary>A terminal answer: the customer does not exist, the relay refused them, or the wait ran out.</summary>
    public bool MarkUndeliverable(string reason, DateTimeOffset now)
    {
        if (!NotificationReasons.All.Contains(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not one of NotificationReasons.All.");

        if (Status != NotificationStatus.Pending)
            return false;

        Status = NotificationStatus.Undeliverable;
        Reason = reason;
        CompletedAt = now;
        return true;
    }

    /// <summary>The intent, before the send: the version and languages rendered, and when it began.</summary>
    /// <remarks>Once only: a row claimed with the intent set is resent under it, not restamped (ADR-052).</remarks>
    public bool StartSend(int templateVersion, string languages, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(templateVersion, 1);
        Require(languages, NotificationLimits.MaxLanguagesLength, nameof(languages));

        if (Status != NotificationStatus.Pending || SendStartedAt is not null)
            return false;

        TemplateVersion = templateVersion;
        Languages = languages;
        SendStartedAt = now;
        return true;
    }

    /// <summary>The relay accepted the message; only a row whose intent was committed first can be sent.</summary>
    public bool MarkSent(DateTimeOffset now)
    {
        if (Status != NotificationStatus.Pending || SendStartedAt is null)
            return false;

        Status = NotificationStatus.Sent;
        CompletedAt = now;
        return true;
    }

    // A malformed value is the caller's defect rather than an arrival to absorb, so it throws.
    private static void Require(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, maxLength, name);
    }
}
```

Not an `AggregateRoot`: §4.1 gives the service no Domain project, and an
`IHasDomainEvents` type here would turn Task 3's architecture gate red, which
is the point of that gate. `Attempts`, `NextAttemptAt` and `LockedUntil` are
the worker's bookkeeping, mapped now so `AddNotificationLog` emits them; the
claim and the backoff that move them are PR-5's.

- [ ] **Step 5: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
```

Expected: 0 warnings; 36 green — the rendered 16 and these 20 —
`Application_references_only_what_the_dependency_table_allows` included, which
is the evidence the record reaches nothing §4.2's second row forbids.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): the Notification record and its moves, driven row by row"
```

The body argues the three decisions above, and that a superseded arrival
returns rather than throws.

---

### Task 9: The `NotificationLog` table and `AddNotificationLog`

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/NotificationConfiguration.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Persistence/NotificationsDbContext.cs`
- Create (generated): `Notifications.Infrastructure/Persistence/Migrations/<id>_AddNotificationLog.cs`
  and its designer, and the rewritten `NotificationsDbContextModelSnapshot.cs`
- Modify: `tests/Notifications.TestSupport/ServiceFixture.cs` — `ColumnsAsync`
- Test: `tests/Notifications.Worker.Tests/NotificationLogSchemaTests.cs`
- Modify: `tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs` — the applied list

**Interfaces:**
- Produces: `notifications.NotificationLog(NotificationId uniqueidentifier PK,
  EventId uniqueidentifier, TemplateKey nvarchar(64), OrderId uniqueidentifier,
  CustomerId uniqueidentifier NULL, TemplateVersion int NULL, Languages
  nvarchar(32) NULL, Parameters nvarchar(2000), Status nvarchar(16), Reason
  nvarchar(32) NULL, CreatedAt datetimeoffset(7), SendStartedAt
  datetimeoffset(7) NULL, CompletedAt datetimeoffset(7) NULL, Attempts int,
  NextAttemptAt datetimeoffset(7), LockedUntil datetimeoffset(7) NULL,
  RowVersion rowversion)`, unique on `(EventId, TemplateKey)`.
- Produces: `NotificationsDbContext.NotificationLog`;
  `ServiceFixture.ColumnsAsync(string schema, string table)`.

- [ ] **Step 1: Write the failing schema tests**

`tests/Notifications.Worker.Tests/NotificationLogSchemaTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Infrastructure.Persistence;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The record's table on the engine the migrator ran on: nothing writes it until its consumers do.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationLogSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_table_holds_every_column_the_record_names_and_no_mailbox()
    {
        string[] columns = await fixture.ColumnsAsync("notifications", "NotificationLog");

        // ADR-053 rule 4: the record holds no mailbox and no body, so the list is exact rather than a superset.
        columns.ShouldBe(
            [
                "Attempts", "CompletedAt", "CreatedAt", "CustomerId", "EventId", "Languages", "LockedUntil",
                "NextAttemptAt", "NotificationId", "OrderId", "Parameters", "Reason", "RowVersion", "SendStartedAt",
                "Status", "TemplateKey", "TemplateVersion"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task One_row_per_event_per_template_is_the_database_s_rule()
    {
        Guid eventId = Guid.CreateVersion7();

        await SaveAsync(Notification.Pending(eventId, "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now));

        // The inbox drops a redelivery first (§9.5); this is the line behind it.
        await Should.ThrowAsync<DbUpdateException>(() =>
            SaveAsync(Notification.Pending(eventId, "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now)));
    }

    [Fact]
    public async Task A_row_round_trips_with_its_status_by_name()
    {
        Notification notification =
            Notification.Pending(Guid.CreateVersion7(), "payment-declined", Guid.CreateVersion7(), """{"v":1}""", Now);
        notification.AssignCustomer(Guid.CreateVersion7());
        notification.MarkUndeliverable(NotificationReasons.NoSuchCustomer, Now.AddMinutes(1));

        await SaveAsync(notification);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        Notification read = await db.NotificationLog
            .SingleAsync(n => n.NotificationId == notification.NotificationId, TestContext.Current.CancellationToken);

        read.Status.ShouldBe(NotificationStatus.Undeliverable);
        read.Reason.ShouldBe(NotificationReasons.NoSuchCustomer);
        read.CustomerId.ShouldBe(notification.CustomerId);

        // By name, never by number (§7.2).
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM notifications.NotificationLog WHERE NotificationId = {0}",
            notification.NotificationId))
            .ShouldBe("Undeliverable");
    }

    private async Task SaveAsync(Notification notification)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        db.NotificationLog.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
```

`tests/Notifications.TestSupport/ServiceFixture.cs` gains the helper the
Shipping fixture has, after `MigrateAsync`, with
`using Microsoft.EntityFrameworkCore;` and `using Xunit;` added in sorted
position:

```csharp
    /// <summary>The column names of one table, from the engine rather than from the model.</summary>
    public async Task<string[]> ColumnsAsync(string schema, string table)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.Database
            .SqlQuery<string>(
                $"""
                SELECT COLUMN_NAME AS Value
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = {schema} AND TABLE_NAME = {table}
                """)
            .ToArrayAsync(TestContext.Current.CancellationToken);
    }
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NotificationLogSchemaTests"
```

Expected: compile failure on `db.NotificationLog`; once Step 3's `DbSet` is in
and before the migration, `Invalid object name 'notifications.NotificationLog'`.

- [ ] **Step 3: The configuration and the `DbSet`**

`Persistence/NotificationConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

/// <summary>§3.2's <c>NotificationLog</c>, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("NotificationLog", "notifications");
        builder.HasKey(n => n.NotificationId);
        builder.Property(n => n.NotificationId).ValueGeneratedNever();

        // One row per event per template, the second line behind §9.5's inbox rather than the first.
        builder.HasIndex(n => new { n.EventId, n.TemplateKey }).IsUnique();

        builder.Property(n => n.TemplateKey).HasMaxLength(NotificationLimits.MaxTemplateKeyLength);
        builder.Property(n => n.Languages).HasMaxLength(NotificationLimits.MaxLanguagesLength);
        builder.Property(n => n.Parameters).HasMaxLength(NotificationLimits.MaxParametersLength);
        builder.Property(n => n.Reason).HasMaxLength(NotificationLimits.MaxReasonLength);

        // By name, never by number (§7.2).
        builder.Property(n => n.Status).HasConversion<string>().HasMaxLength(16);

        // A shadow property, so the record names no EF type, as §8.5's marker does (§7.2).
        builder.Property<byte[]>("RowVersion").IsRowVersion();
    }
}
```

`NotificationsDbContext.cs`: `using Notifications.Application.Records;` after
`using Common.Infrastructure.Inbox;`, and before the inbox's `DbSet`:

```csharp
    /// <summary>§3.2's record of every notice owed, and the one table here that is the service's own.</summary>
    public DbSet<Notification> NotificationLog => Set<Notification>();

```

- [ ] **Step 4: Generate the migration**

```bash
dotnet tool restore
dotnet ef migrations add AddNotificationLog \
    --project src/Services/Notifications/Notifications.Infrastructure \
    --startup-project src/Services/Notifications/Notifications.Migrator \
    --output-dir Persistence/Migrations
```

Open it. It holds exactly one `CreateTable` for `notifications.NotificationLog`
with the columns in Interfaces and one unique `CreateIndex`
`IX_NotificationLog_EventId_TemplateKey` — no `OutboxMessages`, no
`InboxMessages`, no `IdempotencyMarkers`. And
`git diff -- '*NotificationsDbContextModelSnapshot.cs'` adds the
`Notifications.Application.Records.Notification` entity block and changes no
other line. **Those two observations are the proof that Task 3's snapshot is
EF's own**: a snapshot still describing the outbox would have generated a
`DropTable` here, and one missing the inbox a second `CreateTable`.

Give the hand-authored file the house dress — no byte-order mark, a
file-scoped namespace, no `#nullable disable`, no `/// <inheritdoc />`, no
`using System;`, a collection expression for the index's columns as
`AddProducts` has, and one summary — leaving the `.Designer.cs` and the
snapshot as the tool wrote them:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>§3.2's record of every notice owed, generated from <see cref="NotificationConfiguration"/>.</summary>
public partial class AddNotificationLog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "NotificationLog",
            schema: "notifications",
            columns: table => new
            {
                NotificationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TemplateKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                TemplateVersion = table.Column<int>(type: "int", nullable: true),
                Languages = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                Parameters = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                SendStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                Attempts = table.Column<int>(type: "int", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NotificationLog", x => x.NotificationId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_EventId_TemplateKey",
            schema: "notifications",
            table: "NotificationLog",
            columns: ["EventId", "TemplateKey"],
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "NotificationLog",
            schema: "notifications");
    }
}
```

If the generated id sorts before the render's `InitialCreate` — only possible
had Task 5 been given an explicit future `--migration-id` — the migration
applies before the schema exists; regenerate rather than rename by hand.

- [ ] **Step 5: The applied list**

`tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs`: `applied.Length.ShouldBe(5);`
becomes `applied.Length.ShouldBe(6);`, and after
`applied[4].ShouldEndWith("_AddIdempotencyMarkerRowVersion");`:

```csharp
        applied[5].ShouldEndWith("_AddNotificationLog");
```

- [ ] **Step 6: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests
```

Expected: 0 warnings; 65 green, the three schema tests among them. The
first migration rewritten in review is followed by `docker compose down -v`
before the migrator's answer is believed (spec, section 6).

- [ ] **Step 7: Commit**

```bash
git add src/Services/Notifications tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "feat(notifications): the NotificationLog table and AddNotificationLog"
```

The body says why the table lands before anything writes it — the scaffold's
proof is a service that migrates and starts — names the unique key as the
line behind the inbox, and records that the generated migration and snapshot
diff are what proved the pure render's snapshot.

---

### Task 10: CI's filter, outputs, matrix legs and the `images` job's `if:`

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Run the pipeline gate to see it fail**

```bash
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
```

Expected: `filters` refuses `src/Services/Notifications`; `images` refuses its
two Dockerfiles.

- [ ] **Step 2: Edit**

`outputs`, after `shipping`:

```yaml
      notifications: ${{ steps.changes.outputs.notifications }}
```

`filters`, after the `shipping` block:

```yaml
            notifications:
              - *shared
              - 'src/Services/Notifications/**'
              - 'tests/Notifications.*/**'
```

No proto path: the worker serves and calls no gRPC contract, so its images
copy nothing outside its own tree and the building blocks. The `images`
job's `if:` gains `|| needs.changes.outputs.notifications == 'true'` after the
`shipping` clause. Matrix, after the two `shipping` entries:

```yaml
          - filter: notifications
            image: notifications-worker
            dockerfile: src/Services/Notifications/Notifications.Worker/Dockerfile
          - filter: notifications
            image: notifications-migrator
            dockerfile: src/Services/Notifications/Notifications.Migrator/Dockerfile
```

`notifications-worker` because the host is a worker, and PR-6's chart and
`smoke.sh` will read the same name.

- [ ] **Step 3: Run the gate with its suite**

Step 1's three commands again. Expected: all exit 0.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: build and filter Notifications' worker and migrator images"
```

---

### Task 11: The platform up, the render verified, and everything run

- [ ] **Step 1: Both images**

```bash
docker build -f src/Services/Notifications/Notifications.Worker/Dockerfile -t notifications-worker:pr1 .
docker build -f src/Services/Notifications/Notifications.Migrator/Dockerfile -t notifications-migrator:pr1 .
```

Expected: both build. This is the check no suite makes: the restore layer
copies a csproj per project in the closure, and Task 3's Dockerfile patches
are what keep a missing Domain project from failing it.

- [ ] **Step 2: Bring the platform up**

```bash
docker compose -f deploy/compose/docker-compose.yml config --quiet
docker compose -f deploy/compose/docker-compose.yml up --build --wait
```

A RabbitMQ service on the host holding 5672 or 15672 refuses the broker's
published ports; bring the stack up with a scratchpad-only override that moves
them, never an edit to the committed files. Expected: every service healthy,
`notifications-migrator` exited 0 and `notifications-worker` running; `config`
shows the worker with `ConnectionStrings__Notifications`,
`ConnectionStrings__RabbitMq` as `notifications-svc`, `Identity__Authority`
and `OTEL_EXPORTER_OTLP_ENDPOINT`, no Redis key, no Redis dependency and **no
`ports:` mapping**. The chiselled image carries no shell or HTTP client, so the
evidence the host is listening is its log:

```bash
docker compose -f deploy/compose/docker-compose.yml logs notifications-worker | grep -i "Now listening on"
docker compose -f deploy/compose/docker-compose.yml exec sql sh -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT name FROM Notifications.sys.tables WHERE schema_name(schema_id) = '"'"'notifications'"'"'"'
```

Expected: Kestrel bound on `http://[::]:8080` inside the container; tables
`NotificationLog`, `InboxMessages` and `IdempotencyMarkers`, and no
`OutboxMessages`. Then
`docker compose -f deploy/compose/docker-compose.yml down -v`.

- [ ] **Step 3: The render commit is the scaffold's output**

```bash
py -3.12 tools/new-service/new_service.py --verify <Task 5's commit>
```

Expected: `new_service.py Notifications --pure-consumer --migration-id <id>,
rendered by the scaffold at <Task 4's commit>` and `Reproduced: every
difference is a known one.` with no `known:` line, and nothing added to
`known-differences.txt`. A difference here means Task 5's commit carried a
hand edit; it moves to its own commit and the render is recommitted, never
listed.

- [ ] **Step 4: Build and test everything, then every gate**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx
py -3.12 -m unittest discover -s tools/new-service
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 deploy/observability/check.py
py -3.12 -m unittest discover -s .github/licence-gate
py -3.12 .github/licence-gate/licence_gate.py
py -3.12 -m unittest discover -s .github/comment-gate
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: 0 warnings, every suite green, every gate exit 0. A gate with a
suite is tested and then run, and none of these is in `Platform.slnx`.

**The comment gate's run is the one to state an expectation for.** By here
the branch has created the whole of `src/Services/Notifications`,
`tests/Notifications.*` and `deploy/compose/services/notifications.yml`, so the
gate judges every block in them. It exits 0 because Task 2 cut the template
unit's three long blocks and Task 3's suite judged every line the mode writes;
a finding here is something a later task wrote.

**This plan's own text** carries test connection strings inside its code
blocks. If §15.1's scan names this file, add the entry to
`.github/secret-scan/allowed/docs.txt` with the fingerprint the scanner
computed and a reason naming the literal.

- [ ] **Step 5: Open the PR**

The body carries `| Class | A+D+E |` and the touch-set row from Global
Constraints verbatim, the mutexes named there, the dogfood evidence (the
`--verify` line, both suites' counts after Task 9, the generated migration's
one `CreateTable`), the sentence that no Helm chart is owed until PR-6 because
`smoke.sh` checks its chart list against the charts on disk both ways, and the
sentence that PR-4's live broker-binding test is what measures the narrow
grant. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 1, Redis and the outbox: Task 6 strips Redis on Shipping's terms;
  Task 3's mode renders no outbox, mapper, collector, dispatcher or gauges.
- Section 2, the mode: Task 3, with `Notifications` moved from
  `UNRENDERABLE_SERVICES` onto `PURE_CONSUMER_ONLY_SERVICES`. The gates by
  selector: Task 4 — check 8 and broker check 3 read the Domain project, the
  scaffold's suite asserts each gate's own selector over the render, and the
  rendered suites are patched by Task 3's tables. §2's sentence: Task 6.
  §4.5's sentence: Task 3. Anything hand-fixed after the render: none —
  Task 6's Redis strip is a decision about this service, the same cut Shipping
  made, and not a defect of the render.
- Section 3, PR-1's row: Tasks 1–11; CI joins (Task 10) and Helm does not.
- Section 5, the record: Task 8, every row of the table but `not_a_mailbox`'s,
  which PR-5 adds with the send that meets it, and every terminal arrival as
  a no-op.
- Section 6, persistence: Task 9, schema `notifications`, the section's
  columns, the unique key and `AddNotificationLog`.
- Section 10, the broker account: rendered in Task 5 by Task 3's grant, held
  by Task 4's check 3 and Task 7's case.
- Section 11, PR-1's keys and no published port: the render, Task 6 and Task
  11 Step 2.
- Section 13, the Application suite and the scaffold's suite: Tasks 8, 3 and 4.
- Section 14, §2 and §4.5: Tasks 6 and 3.

**Beyond the spec, each with the reason.** Task 1 (`RetentionPurgeService`
took the outbox unconditionally), §7.5's and §9.5's sentences (a service now
registers its own dispatcher, and the purge's statement set follows what is
registered), Task 2 (the template unit's blocks fail CI in every created
copy), the Dockerfile patches (a missing Domain csproj fails `docker build`),
`MetricsInitialiser` kept with one parameter fewer (it forces two other
types §13.6 wants at zero), and `deploy/observability/README.md`'s row (it
states check 8's rule).

**Type consistency.** `Notification`, `NotificationStatus`,
`NotificationReasons`, `NotificationLimits` in `Notifications.Application.Records`;
`NotificationsDbContext.NotificationLog`; the two-argument
`NotificationsWorkerFactory`; `ServiceFixture.ColumnsAsync`; the internal
`NoDomainEventDispatcher` and `NoClaimsIdempotencyStore`; the scaffold's
`Names.pure_consumer`, `PURE_CONSUMER_OMITTED`, `PURE_CONSUMER_PATCHES`,
`PURE_CONSUMER_SPANS`, `replace_span`, `pure_consumer_omits`,
`without_outbox_entity`, `NO_DOMAIN_EVENT_DISPATCHER`; the gates'
`has_domain_project` and `publishes`.

**Deliberately left.** The coverage filter: `.*\.Domain\.dll$` cannot see
the record's rules, which section 5 puts in Application, and no regex can
select "the Application of a service with no Domain" without naming it; Task
4 pins the absence so it is a fact rather than a miss, and §12.9 is not
amended here. The typed `Parameters` format with its `v` refusal: its first
writer is PR-4's consumers, and the column is an opaque bounded string until
then. The `Notifications's` possessive the rename produces: a rename rule for
names ending in s is a scaffold change of its own. `docs/repo-map.md`'s
"Planned" paragraph, already stale about three services: a restatement met in
passing, which the contract says to leave. The chart, the canary row and the
runbook: PR-6.

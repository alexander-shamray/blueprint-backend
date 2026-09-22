# Shipping PR-1 — fifth service from the scaffold's worker mode — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `tools/new-service` the Worker mode §4.5 says is owed, put
§13.6's outbox gauges into the template so every service rendered after this
one publishes them, render `src/Services/Shipping` with that mode, and land
`Shipment`, `TrackingEvent` and the state machine of the spec's section 5 with
the first migration — plus everything the platform needs to build and run the
service: the Compose pair with no published port, the broker account, and CI's
filter, outputs, matrix legs and the `images` job's own `if:`.

**Architecture:** the scaffold grows a second host shape rather than a second
template. `Names` gains a host, the rename maps `Catalog.Api` to
`<Name>.Worker`, `CatalogApi` to `<Name>Worker` and `catalog-api` to
`<name>-worker` in one pass, and a `WORKER_PATCHES` table drops the OpenAPI
document and the endpoint guidance an API host carries and a worker never
will. Kestrel stays bound because §15.3 says the worker's one listener is
§13.5's health endpoint. Nothing dials it, so the Compose unit publishes no
port and the worker mode takes no `--port`. On top of the render this PR
strips `AddRedisConnections` as Payments' PR-1 did, widens the broker grant to
`ordering-svc`'s shape under a `shipping-` prefix, and adds the aggregate: a
`Shipment` keyed by `ShipmentId` with `OrderId` unique, a `TrackingEvent`
keyed by `(ShipmentId, CarrierEventId)`, and a state machine in which every
arrival that is already superseded is a no-op rather than a throw.

**Tech Stack:** .NET at `global.json`'s pin, EF Core with SQL Server,
MassTransit (scaffolded, no consumers yet), OpenTelemetry metrics through
`IMeterFactory`, xUnit with Shouldly and Testcontainers, stdlib Python 3.12
for the scaffold and the gates.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 1 (the Redis answer), 2 (the worker mode and the outbox gauges), 3,
5 (the aggregate and its state machine), 7 (persistence and `AddShipments`),
8 (the broker account), 10 (PR-1's keys, and no port), 12 (the Domain suite
and the scaffold's suite), 13 (§4.5's sentence and §2's) and 14.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Shipping/**`, `tests/Shipping.*`,
  `src/Services/Catalog/**`, `tests/Catalog.Api.Tests/**`,
  `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`,
  `tests/Common.Web.Tests/ObservabilityTests.cs`, `Platform.slnx`,
  `tools/new-service/**`, `deploy/compose/**`,
  `deploy/observability/check.py`, `.github/secret-scan/allowed/**`,
  `.github/workflows/ci.yml`,
  `docs/backend-architecture/02-architecture-at-a-glance.md`,
  `docs/backend-architecture/04-solution-structure.md`
  — paths only, comma-separated, no prose inside the cell and no trailing
  stop: the gate splits the cell on commas, strips a token's backticks only
  when the token ends in one, and refuses a token that is not a path.
  Reasons, since the row above is paths only: A is the service's code and the
  template's, D is the scaffold, the Compose model, the observability gate and
  the two chapters, E is `Platform.slnx` and the `*.csproj` files the render
  writes, which are inside the two Shipping trees already named and so need no
  token of their own. `Common.Web` and its suite are the one `AddMeter` line a
  service's meter owes §13.2, which is why they are here and why nothing else
  in that assembly moves.
- **The locality gate admits `A+D+E` today** — `locality_gate.py` names it as
  the one three-member class and reads it as its three members — so this PR
  needs no contract change before it can merge, unlike Payments' and
  Inventory's PR-1, which are what bought that.
- Depends on nothing of Shipping's. It depends on Payments' PR-1 only for the
  port allocation it does **not** need: a worker publishes no port, so 5105 is
  not taken here.
- No new package: no `Directory.Packages.props` change, no Appendix B row.
  `Dapper` is already `Catalog.Infrastructure`'s.
  `Microsoft.Extensions.Caching.Memory` arrives there transitively through
  `Common.Infrastructure`'s `HybridCache`, and Task 1 gives it the **direct**
  reference Ordering's and Payments' copies argue for: a project that names a
  type declares the package, and both of those csprojs say so at the same
  line.
- Shipping reaches **no** Redis key and registers no `IConnectionMultiplexer`
  (spec, section 1). A rendered line that exists only to feed Redis is cut,
  not commented out.
- **The blueprint's vocabulary**: despatch and despatched in prose,
  `Dispatched` in identifiers, because the contract is `ShipmentDispatched`.
- Comments say why and cite the owner. No history, no PR names, no test named.
- Explicit local types, file-scoped namespaces, braces on two statements or
  more, one space before `=`, `=>` and `{`, 120 columns, British spelling.
- `py -3.12`, never `python`, for anything Python.
- Container tests are `[Collection(nameof(IntegrationCollection))]` and never
  skipped: without a daemon they fail.
- Every step that adds behaviour writes its test first.

---

### Task 1: The outbox gauges join the template, and Catalog's exemption goes

**Files:**
- Create: `src/Services/Catalog/Catalog.Infrastructure/Observability/IOutboxStats.cs`
- Create: `src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxStats.cs`
- Create: `src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxMetrics.cs`
- Create: `src/Services/Catalog/Catalog.Infrastructure/Observability/MetricsInitialiser.cs`
- Modify: `src/Services/Catalog/Catalog.Infrastructure/DependencyInjection.cs`
- Modify: `src/Services/Catalog/Catalog.Infrastructure/Catalog.Infrastructure.csproj`
  — the direct `Microsoft.Extensions.Caching.Memory` reference
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` — one
  `AddMeter` line
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` — one entry in
  `Required`
- Create: `tests/Catalog.Api.Tests/MetricsRegistrationTests.cs`
- Modify: `.github/secret-scan/allowed/tests.txt` — Catalog's own entry for
  the fixture password in the file above
- Modify: `tools/new-service/scaffold/render.py` — four entries in `COPIED`,
  one in `COPY`-adjacent nothing else; the new shared-file writer
- Modify: `tools/new-service/scaffold/verify.py` — one `SCAN_REASONS` row
- Modify: `tools/new-service/new_service.py` — the new shared file in `plan`
- Modify: `deploy/observability/check.py` — `OUTBOX_METRICS_EXEMPT` becomes
  empty

**Interfaces:**
- Produces: `Catalog.Infrastructure.Observability.IOutboxStats` with
  `double OldestAgeSeconds(OutboxLane)`, `int PendingCount(OutboxLane)`,
  `int AbandonedCount(OutboxLane)`; `OutboxStats` with
  `public const int ConnectTimeoutSeconds = 2`; `OutboxMetrics` with
  `public const string MeterName = "Catalog.Outbox"`; `MetricsInitialiser`,
  an `IHostedService` taking `(OutboxMetrics, MessagingMetrics,
  RequestMetrics)`. Renamed by a render, these become
  `Shipping.Infrastructure.Observability.*` and `"Shipping.Outbox"`.
- Produces: `scaffold.render.update_observability_meters(repo_root, names)`.

- [ ] **Step 1: Run the observability gate to see the exemption still standing**

```bash
py -3.12 deploy/observability/check.py
```

Expected: exit 0, with Catalog exempt. This is the baseline, not a failure;
the assertion this task inverts is the one in Step 6.

- [ ] **Step 2: Copy the four Observability files from Ordering**

`src/Services/Ordering/Ordering.Infrastructure/Observability/` holds the
version with no provider in it, which is the one every service has. Copy
`IOutboxStats.cs`, `OutboxStats.cs`, `OutboxMetrics.cs` and
`MetricsInitialiser.cs` to
`src/Services/Catalog/Catalog.Infrastructure/Observability/`, changing the
namespace to `Catalog.Infrastructure.Observability` and, in `OutboxMetrics`,
the two strings that name the service:

```csharp
    /// <summary>
    /// The contract with §13.2's <c>AddMeter</c>. An instrument on an
    /// unregistered meter is collected by nothing, so this constant and the
    /// <c>AddMeter("Catalog.Outbox")</c> line in <c>ObservabilityExtensions</c>
    /// must be the same string.
    /// </summary>
    public const string MeterName = "Catalog.Outbox";
```

Nothing else in the four files names Ordering: `OutboxStats` composes its SQL
from the registered `OutboxTable` and reads `OutboxDispatcher.MaxAttempts`, and
`MetricsInitialiser` names only `Common.Application` and
`Common.Infrastructure.Messaging` types. Verify that by grep before building:

```bash
grep -rn "Ordering" src/Services/Catalog/Catalog.Infrastructure/Observability/
```

Expected: no match.

- [ ] **Step 3: Register them in Catalog's Infrastructure**

In `AddCatalogInfrastructure`, after `services.AddSingleton<MessagingMetrics>();`:

```csharp
        // §13.6's per-lane outbox gauges, and the stats type behind them. Both
        // singletons: the gauges are callbacks the Meter holds, and a second
        // instance would mean two sets of instruments on one meter.
        //
        // OutboxStats gets its own connection factory with the bounded connect
        // timeout its own constant argues, because it runs inside gauge
        // callbacks and a command timeout bounds only the statement. The
        // runtime key, because it reads the same data plane (§7.1); only the
        // timeout differs, so no query path inherits it.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Catalog"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp => new OutboxStats(
            new SqlConnectionFactory(metricsConnectionString),
            sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // Singleton registration alone is lazy: instruments appear on first
        // resolve, which for a class nothing injects is never, and
        // ValidateOnBuild cannot check it because nothing depends on a metrics
        // class (§6.2). Registered before the bus and the dispatcher, so the
        // instruments exist before the first message is delivered against them.
        services.AddHostedService<MetricsInitialiser>();
```

with `using Catalog.Infrastructure.Observability;` and
`using Microsoft.Data.SqlClient;` added to the file's `using` block in sorted
position.

`OutboxStats` names `MemoryCache`, so `Catalog.Infrastructure.csproj` gains
the direct reference Ordering's and Payments' copies already carry, beside
`Microsoft.Extensions.Configuration.Abstractions`:

```xml
    <!-- §13.6's OutboxStats holds its snapshot in a MemoryCache.
         Common.Infrastructure's HybridCache reference carries the package
         transitively; the direct reference states the direct use, as the
         SqlClient line above does. -->
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
```

No `Version=`: the pin is `Directory.Packages.props`' and does not move, so
no Class E work beyond the reference itself, and no Appendix B row — the
package is registered there already.

- [ ] **Step 4: The meter line and the test that pins it**

`src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`, ahead of the
`Ordering.Orders` line so the block stays in §4.1's service order:

```csharp
                .AddMeter("Catalog.Outbox")                        // §13.6 per-lane
```

`tests/Common.Web.Tests/ObservabilityTests.cs`, first entry of `Required`:

```csharp
        "Catalog.Outbox",
```

That list is a deliberate second copy — its own comment says sharing a constant
with the registration would make the assertion vacuous — so both edits are
owed, and the test probes each name it holds.

- [ ] **Step 5: The registration suite, copied and re-pointed**

Copy `tests/Payments.Api.Tests/MetricsRegistrationTests.cs` to
`tests/Catalog.Api.Tests/MetricsRegistrationTests.cs` — Payments' and not
Ordering's, because it is the copy that carries the provider registration and
`TestEnvironment`, which is the shape a service with a third registration
needs and the shape this step strips — change the namespace to
`Catalog.Api.Tests`, the `using`s to `Catalog.Application`,
`Catalog.Infrastructure` and `Catalog.Infrastructure.Observability`, and the
`BuildServices()` helper to Catalog's two registration calls over
configuration that reaches nothing:

```csharp
    private static ServiceCollection BuildServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Catalog"] =
                        "Server=catalog-sql.invalid;Database=Catalog;User Id=sa;Password=not-a-real-password",
                    ["ConnectionStrings:RabbitMq"] = "amqp://guest:guest@catalog-rabbit.invalid:5672",
                    // AddRedisConnections reads both eagerly and throws naming
                    // the missing one (§8.2), so the host cannot be assembled
                    // without them. Unreachable on §12.4's .invalid convention.
                    ["ConnectionStrings:RedisCache"] = "catalog-redis.invalid:6379",
                    ["ConnectionStrings:RedisCoordination"] = "catalog-redis.invalid:6380"
                })
            .Build();

        ServiceCollection services = new();
        services.AddCatalogApplication();
        services.AddCatalogInfrastructure(configuration);

        return services;
    }
```

Drop every assertion naming `ProviderMetrics` or the provider registration, so
`The_metrics_selector_actually_selects_something` reads:

```csharp
        registered.ShouldContain(typeof(OutboxMetrics));
        registered.ShouldContain(typeof(MessagingMetrics));
        registered.ShouldContain(typeof(RequestMetrics));
```

and `TestEnvironment` leaves with the provider registration it existed for.
Everything else — the forced/registered both-directions test, the per-lane
collection over a real `IMeterFactory`, the foreign-meter isolation test and
the contained-failure test — travels unchanged: each is about the mechanism
rather than about Payments.

- [ ] **Step 6: Delete the exemption and run both gates**

`deploy/observability/check.py`:

```python
# Empty, and it stays a dictionary rather than becoming a set or going away:
# the reverse check below reads its keys, and an exemption is a sentence
# somebody has to write. §4.5's template registers the gauges now, so a
# service rendered from it publishes them from its first boot.
OUTBOX_METRICS_EXEMPT: dict[str, str] = {}
```

```bash
dotnet build Platform.slnx
dotnet test tests/Catalog.Api.Tests --filter MetricsRegistrationTests
dotnet test tests/Common.Web.Tests --filter ObservabilityTests
py -3.12 deploy/observability/check.py
```

Expected: 0 warnings; every test green; the gate exits 0 with neither the
"hosts OutboxDispatcher and registers no OutboxMetrics" line nor the stale
exemption line. Running it before Step 3 would have produced the first; that
is the inversion this task is, and it is worth seeing once.

- [ ] **Step 7: Teach the scaffold to copy them**

In `tools/new-service/scaffold/render.py`, four entries in `COPIED`, beside
the other `Catalog.Infrastructure` files:

```python
        # §13.6's per-lane gauges. They travel for the reason the outbox table
        # does: every service hosts the dispatcher, the loaded alerts group by
        # service_name, and a rendered service without them is covered by
        # alerts that can never fire for it — which reads exactly like health.
        "src/Services/Catalog/Catalog.Infrastructure/Observability/IOutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxStats.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/OutboxMetrics.cs",
        "src/Services/Catalog/Catalog.Infrastructure/Observability/MetricsInitialiser.cs",
```

and one in `COPIED` for the suite:

```python
        "tests/Catalog.Api.Tests/MetricsRegistrationTests.cs",
```

- [ ] **Step 8: The `AddMeter` line the render owes**

A gauge on a meter nobody registered is collected by nothing, so the render
writes the line too. In `render.py`:

```python
# §13.2's export names meters one by one, so a service's own meter is a line in
# a building block rather than something its own tree can declare. The line is
# written here for the reason the broker account is: without it the rendered
# service publishes §13.6's gauges and the platform collects none of them, and
# nothing else in the render would say so.
OBSERVABILITY = "src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs"

# The anchor is the shared block's first line rather than the template's own
# meter, because a service's meters are listed in §4.1's order and the
# template's sits at the top of that list. Read rather than assumed: a list
# this script cannot find is a building block that has moved.
SHARED_METERS = '                // Shared names, not service-prefixed: every service emits the\n'


def update_observability_meters(repo_root: Path, names: Names) -> str:
    """One `AddMeter` line for the rendered service's outbox meter (§13.2)."""
    text, newline = read(repo_root, OBSERVABILITY)

    line = f'                .AddMeter("{names.pascal}.Outbox")'
    if line in text:
        raise ScaffoldError(
            f"{OBSERVABILITY} already registers {names.pascal}.Outbox; this script "
            f"adds the line and never a second copy of it")

    require_once(text, SHARED_METERS, OBSERVABILITY)
    padded = line.ljust(67) + "// §13.6 per-lane\n"
    # Before the blank line, so the new meter joins the service-prefixed
    # group instead of opening the shared one (§4.1's order, §13.2's export).
    return restore(text.replace("\n" + SHARED_METERS, padded + "\n" + SHARED_METERS), newline)
```

and in `new_service.py`'s `plan`, one more entry in `updated`, with the import
beside the others:

```python
        OBSERVABILITY: update_observability_meters(repo_root, names),
```

The comment is a C# `//` and not a Python `#`: the line is written into
`ObservabilityExtensions.cs`, and a `#` there is a preprocessor directive that
does not compile. `ljust(67)` puts it in the column the block already uses —
read the column off the neighbouring `AddMeter` lines rather than off this
plan, and if they have moved, the number moves with them. IDE0055 governs C#
whitespace, and a ragged column here would be a failed build in the assembly
this writes into.

- [ ] **Step 9: The `SCAN_REASONS` row the copied suite owes**

The rendered `MetricsRegistrationTests.cs` carries `not-a-real-password`, which
§15.1's scan reports as `connection-string-password`. Without a row the render
refuses, which is the design. In `verify.py`, beside the two like it:

```python
    (
        "tests/Catalog.Api.Tests/MetricsRegistrationTests.cs",
        "connection-string-password",
        "not-a-real-password",
        "The same unusable fixture in the metrics registration suite.",
    ),
```

Catalog's own copy needs the hand-written equivalent in
`.github/secret-scan/allowed/tests.txt`, beside the other Catalog entries.
Compute the fingerprint by running the scanner rather than by hand:

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected before the entry: one finding naming the file, the rule and the
fingerprint. Copy that fingerprint into the entry; re-run; expected: exit 0.

- [ ] **Step 10: Run the scaffold's suite and the gates**

```bash
cd tools/new-service && py -3.12 -m unittest
cd ../.. && py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 deploy/observability/check.py
```

Expected: all green. `test_a_rendered_service_passes_the_secret_scan` is the
one that would fail on a missing row, and it is the reason Step 9 precedes
this.

- [ ] **Step 11: Commit**

```bash
git add src/Services/Catalog tests/Catalog.Api.Tests src/BuildingBlocks/Common.Web \
        tests/Common.Web.Tests tools/new-service deploy/observability/check.py \
        .github/secret-scan/allowed/tests.txt
git commit -m "feat(catalog): the template registers §13.6's outbox gauges, and the exemption goes"
```

The body says the exemption was a decision about the template rather than
about Catalog, names the fifth service as what forced it, and says the render
now writes the `AddMeter` line because a gauge on an unregistered meter is
collected by nothing.

---

### Task 2: The worker mode

**Files:**
- Modify: `tools/new-service/scaffold/__init__.py` — `Names` gains a host; the
  rename maps the three compound tokens
- Modify: `tools/new-service/scaffold/patch.py` — `WORKER_PATCHES`
- Modify: `tools/new-service/scaffold/render.py` — the host reaches
  `render_projects`, `update_solution`, `render_service_compose`,
  `update_compose` and `update_ports_readme`; `port` becomes optional
- Modify: `tools/new-service/new_service.py` — `--worker`, the conditional
  `--port`, the refusal split
- Modify: `tools/new-service/test_new_service.py` — the worker render's tests
- Modify: `tools/new-service/README.md`
- Modify: `docs/backend-architecture/04-solution-structure.md` — §4.5

**Interfaces:**
- Produces: `Names(pascal, host)` with `host` in `{"Api", "Worker"}`;
  `new_service.API_HOST`, `new_service.WORKER_HOST`, `new_service.HOSTS`;
  `plan(repo_root, name, port, migration_id, host=API_HOST)` with
  `port: int | None`; `project_suffixes(host)`.

- [ ] **Step 1: Write the failing tests**

In `tools/new-service/test_new_service.py`, above `RefusesToRun`:

```python
WORKER_UNIT = f"deploy/compose/services/{PROBE.lower()}.yml"


def worker(name: str = PROBE, repo_root: Path = REPO_ROOT) -> Plan:
    return plan(repo_root, name, None, MIGRATION_ID, host=new_service.WORKER_HOST)


class RendersAWorker(unittest.TestCase):
    """§4.1's second host shape: no API, and §13.5's endpoint the only listener."""

    @classmethod
    def setUpClass(cls):
        cls.rendered = worker()

    def test_the_host_project_is_a_worker_and_there_is_no_api(self):
        self.assertIn(
            f"src/Services/{PROBE}/{PROBE}.Worker/{PROBE}.Worker.csproj", self.rendered.created)
        self.assertIn(
            f"tests/{PROBE}.Worker.Tests/{PROBE}.Worker.Tests.csproj", self.rendered.created)
        for path in self.rendered.created:
            self.assertNotIn(f"{PROBE}.Api", path)

    def test_the_fixture_and_the_entry_point_follow_the_host(self):
        factory = f"tests/{PROBE}.TestSupport/{PROBE}WorkerFactory.cs"
        self.assertIn(factory, self.rendered.created)
        # `public class`, not `public sealed`: the template's CatalogApiFactory
        # is unsealed, so a render cannot produce a sealed one and an
        # assertion asking for it would fail on the template rather than on
        # the mode this suite is about.
        self.assertIn(
            f"public class {PROBE}WorkerFactory", self.rendered.created[factory])
        dockerfile = f"src/Services/{PROBE}/{PROBE}.Worker/Dockerfile"
        self.assertIn(
            f'ENTRYPOINT ["dotnet", "{PROBE}.Worker.dll"]', self.rendered.created[dockerfile])

    def test_the_host_serves_the_health_endpoint_and_nothing_else(self):
        program = self.rendered.created[f"src/Services/{PROBE}/{PROBE}.Worker/Program.cs"]
        self.assertIn("app.MapCommonHealthEndpoints();", program)
        self.assertNotIn("MapOpenApi", program)
        self.assertNotIn("AddOpenApi", program)
        # Kestrel stays bound (§15.3), so the host is still a WebApplication
        # and the middleware §11.2 requires is still on it.
        self.assertIn("WebApplication.CreateBuilder", program)
        self.assertIn("app.UseAuthentication();", program)

    def test_the_host_project_carries_no_openapi_package(self):
        csproj = self.rendered.created[
            f"src/Services/{PROBE}/{PROBE}.Worker/{PROBE}.Worker.csproj"]
        self.assertNotIn("Microsoft.AspNetCore.OpenApi", csproj)
        # And it is still a web project, for §13.5's endpoint.
        self.assertIn("Microsoft.NET.Sdk.Web", csproj)

    def test_the_compose_pair_is_migrator_and_worker_and_publishes_no_port(self):
        unit = self.rendered.created[WORKER_UNIT]
        declared = [line for line in unit.split("\n") if new_service.SERVICE_KEY.fullmatch(line)]
        self.assertEqual(declared, [f"  {PROBE.lower()}-migrator:", f"  {PROBE.lower()}-worker:"])
        self.assertNotIn("ports:", unit)

    def test_the_infra_only_override_excludes_the_worker_half(self):
        override = self.rendered.updated["deploy/compose/docker-compose.infra-only.yml"]
        self.assertIn(f"  {PROBE.lower()}-worker:\n    profiles: [ \"excluded\" ]\n", override)

    def test_the_ports_table_says_no_port_rather_than_omitting_the_service(self):
        readme = self.rendered.updated["deploy/compose/README.md"]
        self.assertIn(f"| {PROBE} worker |", readme)
        self.assertIn("no published port", readme)

    def test_the_solution_folder_holds_the_worker_and_its_suite(self):
        solution = self.rendered.updated["Platform.slnx"]
        self.assertIn(
            f'<Project Path="src/Services/{PROBE}/{PROBE}.Worker/{PROBE}.Worker.csproj" />',
            solution)
        self.assertIn(
            f'<Project Path="tests/{PROBE}.Worker.Tests/{PROBE}.Worker.Tests.csproj" />',
            solution)

    def test_the_meter_line_names_the_worker_s_outbox(self):
        extensions = self.rendered.updated[new_service.OBSERVABILITY]
        self.assertIn(f'.AddMeter("{PROBE}.Outbox")', extensions)
```

and, in `RefusesToRun`, replacing `test_a_service_section_4_1_gives_a_worker`:

```python
    def test_a_name_section_4_1_gives_a_worker_is_refused_as_an_api(self):
        # The mode exists now, so the refusal narrows rather than going: an
        # API render of either name would contradict §4.1 exactly as before.
        for name in ("Shipping", "Notifications", "SHIPPING"):
            with self.assertRaises(ScaffoldError) as raised:
                render(name=name)
            self.assertIn("--worker", str(raised.exception))

    def test_the_service_with_no_domain_project_is_refused_in_either_mode(self):
        # §4.1 gives Notifications no Domain project, which is a second mode
        # this script does not have.
        for call in (lambda: render(name="Notifications", port=5198),
                     lambda: worker(name="Notifications")):
            with self.assertRaises(ScaffoldError) as raised:
                call()
            self.assertIn("Domain", str(raised.exception))

    def test_a_worker_render_refuses_a_port(self):
        with self.assertRaises(ScaffoldError) as raised:
            plan(REPO_ROOT, PROBE, PORT, MIGRATION_ID, host=new_service.WORKER_HOST)
        self.assertIn("publishes no port", str(raised.exception))

    def test_an_api_render_still_requires_one(self):
        with self.assertRaises(ScaffoldError) as raised:
            plan(REPO_ROOT, PROBE, None, MIGRATION_ID)
        self.assertIn("--port", str(raised.exception))

    def test_a_host_this_script_does_not_render(self):
        with self.assertRaises(ScaffoldError) as raised:
            plan(REPO_ROOT, PROBE, None, MIGRATION_ID, host="Daemon")
        self.assertIn("Daemon", str(raised.exception))
```

- [ ] **Step 2: Run them to see them fail**

```bash
cd tools/new-service && py -3.12 -m unittest
```

Expected: FAIL — `TypeError: plan() got an unexpected keyword argument 'host'`
and `AttributeError: module 'new_service' has no attribute 'WORKER_HOST'`.

- [ ] **Step 3: The rename carries the host**

`tools/new-service/scaffold/__init__.py`, replacing `CASINGS` and `Names`:

```python
TEMPLATE = "Catalog"

# The host project's suffix in the template, and the one §4.1 gives Shipping
# and Notifications. A service is one or the other, and the difference reaches
# a project name, a namespace, a Compose service key, a Dockerfile entry point
# and a test fixture's type name — so it belongs to the rename rather than to a
# patch table, which can only edit a file's text and not its path.
API_HOST = "Api"
WORKER_HOST = "Worker"
HOSTS = (API_HOST, WORKER_HOST)


@dataclass(frozen=True)
class Names:
    """The three casings every rename needs, and the host the service runs as."""

    pascal: str
    host: str = API_HOST

    @property
    def lower(self) -> str:
        return self.pascal.lower()

    @property
    def upper(self) -> str:
        return self.pascal.upper()

    @property
    def replacements(self) -> dict[str, str]:
        """Every token the rename maps, longest first.

        The three compound tokens come first and that ordering is the whole
        mechanism: Python's alternation is ordered, so `Catalog.Api` is tried
        at a position before `Catalog` is, and a host rename run as a SECOND
        pass would be a pass that can see the first one's output. One
        alternation cannot re-enter its own.

        The compounds and never a bare `Api`: that token is in `AddOpenApi`,
        `MapOpenApi`, `Microsoft.AspNetCore.OpenApi` and
        `IApiDescriptionProvider`, none of which is this service's host — so
        the needles are the three places the template spells the host as part
        of a name it owns, and nowhere else.
        """
        return {
            f"{TEMPLATE}.{API_HOST}": f"{self.pascal}.{self.host}",
            f"{TEMPLATE}{API_HOST}": f"{self.pascal}{self.host}",
            f"{TEMPLATE.lower()}-{API_HOST.lower()}": f"{self.lower}-{self.host.lower()}",
            TEMPLATE: self.pascal,
            TEMPLATE.lower(): self.lower,
            TEMPLATE.upper(): self.upper,
        }

    def rename(self, text: str) -> str:
        """One pass over every token, never one pass each.

        Chained `str.replace` calls feed each replacement to the next, and a
        name that contains a later casing of the template token is rewritten
        twice: `CATALOGSearch` turned a source `Catalog` into
        `CATALOGSEARCHSearch`, because pass one produced text that pass three
        then matched.
        """
        replacements = self.replacements
        pattern = re.compile("|".join(re.escape(token) for token in replacements))
        return pattern.sub(lambda match: replacements[match.group(0)], text)
```

An API render maps `Catalog.Api` to `<Name>.Api`, which is what the plain
casing pass already did, so nothing about the four existing services' renders
moves.

- [ ] **Step 4: The host reaches the project list, the solution and Compose**

`new_service.py`: `PROJECT_SUFFIXES` becomes a function, because two of the
nine suffixes are the host's:

```python
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

with the one call site in `plan` becoming
`generated = {f"{names.pascal}.{suffix}": suffix for suffix in project_suffixes(host)}`.

`render.py`'s `update_solution` takes its two lists from the host:

```python
    folder = [
        f'  <Folder Name="/src/Services/{names.pascal}/">\n',
        *(
            f'    <Project Path="src/Services/{names.pascal}/{names.pascal}.{layer}'
            f'/{names.pascal}.{layer}.csproj" />\n'
            for layer in sorted(("Application", "Domain", "Infrastructure", "Migrator", names.host))
        ),
        "  </Folder>\n",
    ]
```

and

```python
    tests = [
        f'    <Project Path="tests/{names.pascal}.{suite}/{names.pascal}.{suite}.csproj" />\n'
        for suite in sorted(("Application.Tests", "Domain.Tests", "TestSupport", f"{names.host}.Tests"))
    ]
```

Sorted rather than written in order: `Api` sorts before `Application` and
`Worker` after `Migrator`, and the folder is alphabetical.

- [ ] **Step 5: A worker publishes no port**

`render_service_compose` takes `port: int | None`. The loopback anchor stays
required of the template — reading the prefix off Catalog would make the
scaffold agree with whatever Catalog does — and what changes is what happens
to the line it finds:

```python
    published = re.search(rf'ports: \[ "{re.escape(LOOPBACK)}:(\d+):8080" \]', block)
    if published is None:
        raise ScaffoldError(
            f"the template's api block publishes no {LOOPBACK}-bound port to substitute "
            f"(§14.1 binds every mapping to loopback)")

    if port is None:
        # §3.2 gives a worker no API and nothing dials it, so the mapping is
        # removed rather than set to something: a published port on a host
        # with one anonymous health endpoint is a door §15.3 says not to open.
        # The whole line, indent and newline included — a bare substitution
        # would leave an empty `    ` line the YAML keeps and a reader reads
        # as an omission.
        block = re.sub(rf'^ *{re.escape(published.group(0))}\n', "", block, flags=re.MULTILINE)
    else:
        block = block.replace(published.group(0), f'ports: [ "{LOOPBACK}:{port}:8080" ]')
```

`update_compose` takes `port: int | None` and skips the collision loop when it
is `None`, with the reason stated where the loop was:

```python
    # A worker publishes nothing, so there is no allocation to collide with —
    # and running the loop with `port is None` would build the pattern `:None:`
    # and find every port free, which is the fail-open shape this check exists
    # to be the opposite of.
    if port is not None:
        for _, entry in entries:
            included, _ = read(repo_root, f"{COMPOSE_DIR}/{entry}")
            if re.search(rf'"(?:{HOST_IP}:)?{port}:\d+"', included):
                raise ScaffoldError(f"port {port} is already published in {COMPOSE_DIR}/{entry}")
```

`update_ports_readme` takes `port: int | None` and writes the worker's row:

```python
    row = (
        f"| {names.pascal} worker | — (no published port) | "
        f"§3.2 gives it no API; §13.5's `/health/live` and `/health/ready` are its "
        f"only listener and answer inside the container |\n"
        if port is None
        else f"| {names.pascal} API | http://localhost:{port} | "
             f"`/health/live`, `/health/ready`, "
             f"`/openapi/v1.json` (needs a token — see below) |\n"
    )
```

The row is written rather than omitted for the reason §15.3 writes
`service.enabled: false` down: an absence is not a decision anybody can read.

- [ ] **Step 6: The worker patches**

`patch.py`, after `PATCHES`:

```python
# The edits a WORKER render makes on top of PATCHES, and the order is
# load-bearing: these are appended to a file's PATCHES tuple, so each anchor is
# matched against the text the earlier ones already produced. Two of the three
# below anchor on a PATCHES *replacement* for exactly that reason.
#
# §3.2 gives a worker no API, and §15.3 keeps Kestrel bound for §13.5's health
# endpoint — so what leaves is the OpenAPI document and the endpoint guidance,
# and what stays is the host, the middleware §11.2 requires of every service,
# and the probes.
WORKER_PATCHES: dict[str, tuple[tuple[str, str], ...]] = {
    "src/Services/Catalog/Catalog.Api/Catalog.Api.csproj": (
        (
            "    The Web SDK, because this is the one project in the service that is a\n"
            "    host. Application and Infrastructure per §4.2's fourth row — Program.cs is\n"
            "    the only composition root, and the endpoints gate in Catalog.Api.Tests\n"
            "    holds every other file to Application and Domain contracts.\n",
            "    The Web SDK, and a worker keeps it: §3.2 gives this service no API, and\n"
            "    §15.3 still requires §13.5's health endpoint, which is a listener — so\n"
            "    Kestrel stays bound and nothing routes to it. Application and\n"
            "    Infrastructure per §4.2's fourth row — Program.cs is the only composition\n"
            "    root, and the gate in Catalog.Api.Tests holds every other file to\n"
            "    Application and Domain contracts.\n",
        ),
        (
            "  <ItemGroup>\n"
            "    <!-- Appendix C's OpenAPI deliverable: document only, no UI. -->\n"
            "    <PackageReference Include=\"Microsoft.AspNetCore.OpenApi\" />\n"
            "  </ItemGroup>\n"
            "\n",
            "",
        ),
    ),
    "src/Services/Catalog/Catalog.Api/Program.cs": (
        (
            "\n"
            "// Appendix C's OpenAPI deliverable: document only, no UI.\n"
            "builder.Services.AddOpenApi();\n",
            "",
        ),
        (
            "// This service registers no permission policy, because it names no endpoint\n"
            "// that needs one. The first slice brings both together (§11.4):\n"
            "//\n"
            "//     builder.Services\n"
            "//         .AddAuthorizationBuilder()\n"
            "//         .AddPolicy(<Service>Permissions.Write, p => p.RequirePermission(…));\n"
            "//\n"
            "// A policy registered before an endpoint names it is an unused registration;\n"
            "// an endpoint naming one nobody registered throws on the first request that\n"
            "// reaches it, never at startup. Add AuthorizationPolicyTests with the slice —\n"
            "// it enumerates the endpoints and requires every policy they name to resolve.\n",
            "// This host registers no permission policy and never will: §3.2 gives it no\n"
            "// API, so there is no endpoint to name one. The middleware below stays,\n"
            "// because §11.2 makes every host validate its own token whether or not it\n"
            "// serves anything, and because ADR-030's fallback policy is what makes the\n"
            "// probes' AllowAnonymous a decision rather than an omission.\n",
        ),
        (
            "app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token\n"
            "app.MapOpenApi();\n"
            "\n"
            "// This service maps no endpoint of its own yet. The first one goes here,\n"
            "// behind RequireAuthorization at the group (§11.4) — fail closed, and let\n"
            "// any deliberately public endpoint say AllowAnonymous out loud.\n",
            "// §13.5's probes are the only thing this host serves, and that is the whole\n"
            "// difference from an API service: everything it does, it does from a hosted\n"
            "// service. The kubelet reaches this port without a Service in front of it\n"
            "// (§15.3), which is why there is a listener and no route.\n"
            "app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token\n",
        ),
    ),
}
```

`render.py` applies them in `render_projects`, after `PATCHES` and before the
migration-shape tables:

```python
        patches = PATCHES.get(relative, ())
        if names.host != API_HOST:
            patches = (*patches, *WORKER_PATCHES.get(relative, ()))
```

and `classify` gains the inert-patch check's twin, because a `WORKER_PATCHES`
key for a file nothing copies is an anchor guarding nothing exactly as a
`PATCHES` key would be:

```python
    if (inert := (set(PATCHES) | set(WORKER_PATCHES)) - set(copied)):
```

The parentheses are load-bearing: `-` binds tighter than `|` in Python, so
`set(PATCHES) | set(WORKER_PATCHES) - set(copied)` is a different set — every
`PATCHES` key, plus whatever `WORKER_PATCHES` adds — and the check would pass
on a key it exists to refuse.

- [ ] **Step 7: The command line, and the refusal split**

`new_service.py`:

```python
# The names §4.1 gives a Worker in place of an Api. The mode exists now, so
# this is no longer a refusal of the NAME: it is what makes `--worker`
# mandatory for them, because rendering either as an API service would
# contradict the chapter as quietly as it would have before.
WORKER_ONLY_SERVICES = frozenset({"Shipping", "Notifications"})

# And the one this script still cannot render at all: §4.1 gives Notifications
# no Domain project, which is a second mode. It comes off with that mode, on
# the same terms Shipping came off the list above.
UNRENDERABLE_SERVICES = frozenset({"Notifications"})
```

in `plan`, replacing the old `WORKER_SERVICES` refusal:

```python
    if host not in HOSTS:
        raise ScaffoldError(f"'{host}' is not a host this script renders; §4.1 names {HOSTS}")
    if host == WORKER_HOST and port is not None:
        raise ScaffoldError(
            f"a worker publishes no port (§3.2 gives it no API), so --port has nothing "
            f"to allocate; {port} would be a mapping nothing dials")
    if host == API_HOST and port is None:
        raise ScaffoldError(
            "--port is required for an API render: a port is an allocation recorded in "
            "§14.1 and deploy/compose/README.md")

    if host == API_HOST and name.lower() in {s.lower() for s in WORKER_ONLY_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} a Worker in place of an Api. Render it with --worker; an "
            f"API service under this name would contradict the chapter.")
    if name.lower() in {s.lower() for s in UNRENDERABLE_SERVICES}:
        raise ScaffoldError(
            f"§4.1 gives {name} no Domain project and this script renders one. That is a "
            f"second mode, and it joins with the PR that builds the first such host.")
```

**Four edits the refusals above do not make, and without any one of them
`plan` cannot render a worker at all.** They are named here rather than left
to be discovered, because each fails late and in a way that reads as
something else:

- `plan`'s own signature becomes
  `plan(repo_root, name, port, migration_id, host=API_HOST)`, with
  `port: int | None`. The default keeps every existing caller and every
  existing test passing an API render.
- The import at the top of `new_service.py` —
  `from scaffold import TEMPLATE, Names, ScaffoldError` — becomes
  `from scaffold import API_HOST, HOSTS, TEMPLATE, WORKER_HOST, Names, ScaffoldError`.
  Step 3 puts the three host names in `scaffold/__init__.py`, and this module,
  its `main` and the suite spell them bare.
- `names = Names(name)` becomes `names = Names(name, host)`. Without it the
  rename maps `Catalog.Api` onto `<Name>.Api` whatever `--worker` said, and
  the render produces an API-named host project, namespace, Compose key,
  entry point and fixture while every refusal above passes.
- The port range check, which reads `if port not in PORTS:` and raises for
  `port=None` before any host refusal is reached, becomes

```python
    if port is not None and port not in PORTS:
        raise ScaffoldError(f"port {port} is outside 1–65535 and Docker cannot publish it")
```

  and the three host refusals go **above** it, so a missing `--port` on an API
  render is answered by the message naming `--port` rather than by a range
  error about `None`.

and in `main`:

```python
    parser.add_argument(
        "--port",
        type=int,
        default=None,
        help=(
            "the host port the API publishes. Required for an API render, refused for a "
            "worker: a port is an allocation recorded in §14.1 and "
            "deploy/compose/README.md, and a script that guessed one would quietly "
            "disagree with a printed chapter"
        ),
    )
    parser.add_argument(
        "--worker",
        action="store_true",
        help=(
            "render §4.1's Worker host instead of an Api: no OpenAPI document, no route "
            "group, no published port, and §13.5's health endpoint as the one listener"
        ),
    )
```

with `host = WORKER_HOST if args.worker else API_HOST` and the `plan` call
taking it. The success line becomes

```python
    where = "publishing no port" if args.port is None else f"API on port {args.port}"
    print(f"{args.name}: {len(rendered.created)} files created, "
          f"{len(rendered.updated)} updated, {where}.")
```

- [ ] **Step 8: Run the suite**

```bash
cd tools/new-service && py -3.12 -m unittest
```

Expected: green, `RendersAWorker` included, and every existing test still
passing — the API renders must be byte-identical to what they were, which
`test_no_generated_path_or_line_still_names_the_template` and the migration
tests are the evidence for.

- [ ] **Step 9: The scaffold's own README**

`tools/new-service/README.md`: add `--worker` to the argument table, amend the
`--port` row to say it is required for an API render and refused for a worker,
and replace the closing paragraph:

> **`Shipping` and `Notifications` are refused by name** for that reason: the
> script renders the API shape, §4.1 gives those two a Worker, and
> Notifications no Domain project either. Both names are accepted again by the
> change that adds the mode.

with:

> **`--worker` renders §4.1's other host shape**: the same nine projects with
> `<Name>.Worker` in place of `<Name>.Api` and `<Name>.Worker.Tests` in place
> of `<Name>.Api.Tests`, no OpenAPI document, no route group and no published
> port. Kestrel stays bound because §15.3's worker chart says the health
> endpoint of §13.5 is the one listener a worker has, and the kubelet reaches
> it without a Service in front of it. **`Shipping` is refused without
> `--worker` and rendered with it**; `Notifications` is refused in both modes,
> because §4.1 gives it no Domain project and that is a second mode this
> script does not have.

and, in the "Not in scope" paragraph, drop "and a Worker host in place of an
API — §4.1 gives Shipping and Notifications one, and no such host exists yet
to copy", leaving the gateway route and the Helm chart.

- [ ] **Step 10: §4.5**

`docs/backend-architecture/04-solution-structure.md`:

Opening sentence — **Four** becomes **Five**, and the list says which host each
takes:

> **Five** of §4.1's six services are rendered by the command below — Catalog,
> Ordering, Inventory and Payments as API hosts, Shipping as a worker — and
> writing the fifth by hand is how it ends up subtly different from the first
> four.

Below the sample, one paragraph on the mode:

> `--worker` renders §4.1's other host shape. The nine projects are the same
> nine with `<Name>.Worker` where `<Name>.Api` would be, and what leaves is
> the OpenAPI document, the route group and the published port — a worker
> consumes from the broker and nothing dials it (§3.2). **Kestrel stays bound
> all the same**, because §15.3 gives Shipping and Notifications the same
> chart minus the Service and the Ingress and their one listener is §13.5's
> health endpoint, which the kubelet reaches on the container port without a
> Service in front of it. The mode is the rename's rather than a patch
> table's: the host's name reaches a project, a namespace, a Compose service
> key, a Dockerfile entry point and a test fixture's type, and a patch can
> edit a file's text but not its path.

The "Three things are outside it" paragraph becomes two, with the worker
clause removed:

> **Two** things are outside it, and neither is silently missing: the gateway
> route ([§10.2](10-api-gateway.md)) — the route belongs to the gateway's
> configuration, not the service's tree — and the Helm chart
> ([§15.3](15-cicd-deployment.md)).

and the closing callout becomes:

> **The scaffold refuses `Notifications` by name, and `Shipping` only without
> `--worker`.** Documenting the gap left the script willing to render either
> as an API service, which would have contradicted §4.1 quietly. A note is not
> a guard, so the guard stayed and narrowed: an API render under either name
> is still refused, and `Notifications` is refused in both modes because §4.1
> gives it no Domain project at all — which is a second mode, and it comes off
> with the PR that builds it.

Prose at 80 columns.

- [ ] **Step 11: Audit and commit**

Run `/check-links` and `/validate-blueprint`; `docs/change-locality.md`'s
procedure owes the audit after any chapter edit, and a finding is fixed here.
The test counts in §4.5 and in the scaffold README move with Task 1's added
suite and are remeasured in Task 10, not guessed here.

```bash
git add tools/new-service docs/backend-architecture/04-solution-structure.md
git commit -m "feat(scaffold): a worker mode renders §4.1's host with no API"
```

The body argues that the mode belongs to the rename, that Kestrel stays bound
for §13.5's endpoint on §15.3's terms, and that the refusal narrowed rather
than went.

---

### Task 3: Render Shipping and prove the empty worker

**Files:**
- Create (by the script): `src/Services/Shipping/**`, `tests/Shipping.*/**`,
  `deploy/compose/services/shipping.yml`
- Modify (by the script): `Platform.slnx`,
  `deploy/compose/docker-compose.yml`,
  `deploy/compose/docker-compose.infra-only.yml`,
  `deploy/compose/.env.example`, `deploy/compose/README.md`,
  `deploy/compose/rabbitmq/definitions.json`,
  `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`,
  `.github/secret-scan/allowed/*.txt`
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` — `"Shipping.Outbox"`

**Interfaces:**
- Produces: `Shipping.Worker`, `Shipping.Application`, `Shipping.Domain`,
  `Shipping.Infrastructure`, `Shipping.Migrator`; `ShippingDbContext` with
  default schema `shipping`; `AddShippingApplication()`,
  `AddShippingInfrastructure(IConfiguration)`;
  `Shipping.Infrastructure.Observability.OutboxMetrics.MeterName` =
  `"Shipping.Outbox"`; the fixtures `ServiceFixture`, `ShippingWorkerFactory`,
  `TestAuthHandler` in `tests/Shipping.TestSupport`.

- [ ] **Step 1: Confirm the tree is clean**

Run `git status --short` (expect empty) and
`grep -rn "Shipping" deploy/compose/services/ Platform.slnx` (expect no match).

- [ ] **Step 2: Run the scaffold**

```bash
py -3.12 tools/new-service/new_service.py Shipping --worker
```

Expected: the script lists what it wrote, says "publishing no port", and ends
with `Next: dotnet restore Platform.slnx && dotnet build Platform.slnx`.

- [ ] **Step 3: The one line the render cannot write**

`ObservabilityExtensions.cs` gained `.AddMeter("Shipping.Outbox")` from the
render. `tests/Common.Web.Tests/ObservabilityTests.cs` did not, and must not
have: its `Required` list is a deliberate second copy of §13.2's, and a
scaffold that edited it would make the assertion agree with whatever the
scaffold wrote. Add the entry by hand, after `"Payments.Outbox"`:

```csharp
        "Shipping.Outbox",
```

- [ ] **Step 4: Build and run the rendered suites**

```bash
dotnet restore Platform.slnx && dotnet build Platform.slnx
dotnet test tests/Shipping.Domain.Tests
dotnet test tests/Shipping.Application.Tests
dotnet test tests/Shipping.Worker.Tests
dotnet test tests/Common.Web.Tests --filter ObservabilityTests
```

One project per invocation: `dotnet test` takes a single project or solution
argument. Expected: 0 warnings, 0 errors, every test green. The container half
needs Docker; a failure on `Failed to connect to Docker endpoint` is the
daemon, not the scaffold.

- [ ] **Step 5: Confirm the secret scan accepts the rendered tree**

```bash
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: both exit 0, suite first (`docs/testing.md`). The scaffold wrote the
allow-list entries itself; a finding here is one it did not.

- [ ] **Step 6: Commit the render alone**

```bash
git add -A
git commit -m "feat(shipping): fifth service from the scaffold's worker mode"
```

The body says this is the worker mode's first dogfood, names the host as
`Shipping.Worker` with no published port, and says the next commits remove
Redis because §2 gives Shipping none and widen the broker grant because PR-5's
queue will need it.

---

### Task 4: Shipping has no Redis, and §2 says so

**Files:**
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
  — cut `services.AddRedisConnections(configuration);` and its comment block,
  cut the two Redis readiness rows, add `IIdempotencyStore`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Idempotency/NoClaimsIdempotencyStore.cs`
- Modify: `deploy/compose/services/shipping.yml` — cut both
  `ConnectionStrings__Redis*` variables, their comment, and the two `redis-*`
  entries under the worker's `depends_on`
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs` — cut the two
  Redis constructor parameters, `UnreachableRedis` and the two `UseSetting`
  calls that feed them
- Modify: `tests/Shipping.TestSupport/ServiceFixture.cs` — cut `_redisCache`,
  `_redisCoordination`, their starts, their disposal and the two factory
  arguments
- Modify: `tests/Shipping.TestSupport/Shipping.TestSupport.csproj` — cut the
  `Testcontainers.Redis` reference if nothing else in the project uses it
- Modify: `tests/Shipping.Worker.Tests/MetricsRegistrationTests.cs` — the two
  Redis keys leave `BuildServices()`
- Test: `tests/Shipping.Worker.Tests/NoRedisTests.cs`
- Modify: `docs/backend-architecture/02-architecture-at-a-glance.md`

**Interfaces:**
- Produces: `new ShippingWorkerFactory(string connectionString, string
  rabbitConnectionString)` — two parameters, used by every later plan.

- [ ] **Step 1: Inventory every rendered Redis mention**

```bash
grep -rn -i "redis\|HybridCache\|IConnectionMultiplexer" src/Services/Shipping tests/Shipping.* deploy/compose/services/shipping.yml
```

Every hit is one of: the `AddRedisConnections` call and its comment; the two
readiness rows; the Compose unit's two variables, their comment and two
`depends_on` entries; the factory's parameters, constant and settings; the
fixture's two containers; the metrics suite's two configuration keys; a
`using` for `Common.Infrastructure.Redis` or `Testcontainers.Redis`; a test
whose subject is Redis. A hit of any other kind is a stop: record it and ask,
rather than cut something the design did not name.

- [ ] **Step 2: Write the failing test**

```csharp
using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// §2: Shipping reaches neither Redis instance. Its two workers claim rows
/// under a lease in SQL (spec, section 4), it caches nothing, and §8.5's keys
/// belong to HTTP write commands, which a host with no API does not have.
/// </summary>
public sealed class NoRedisTests
{
    [Fact]
    public void The_host_starts_with_no_redis_key_and_registers_no_connection()
    {
        using ShippingWorkerFactory factory = new(
            "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://shipping-svc:x@rabbit.invalid:5672");

        IServiceProvider services = factory.Services;

        // By name, not by a package reference: a test proving the service has
        // no Redis should not be the thing that gives its project one. The
        // type is still loadable, because Common.Infrastructure carries the
        // package.
        Type multiplexer = Type.GetType("StackExchange.Redis.IConnectionMultiplexer, StackExchange.Redis")
            ?? throw new InvalidOperationException("StackExchange.Redis did not load; the assertions below would prove nothing.");
        IKeyedServiceProvider keyed = (IKeyedServiceProvider)services;

        services.GetService(multiplexer).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Cache).ShouldBeNull();
        keyed.GetKeyedService(multiplexer, RedisConnections.Coordination).ShouldBeNull();
    }
}
```

- [ ] **Step 3: Run it to see it fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter NoRedisTests`
Expected: FAIL — a compile error on the two-argument constructor, or, once the
factory is cut first, a host that throws naming a missing Redis key.

- [ ] **Step 4: Cut every hit Step 1 listed**

In `DependencyInjection.cs`, delete the `AddRedisConnections` line and the
comment above it, and the two `.AddRedis(...)` readiness rows with the part of
the readiness comment that argues them — leaving the SQL row and §13.5's
reason for it. `IIdempotencyMarkerStore` stays: it is the durable marker's EF
half, the scaffold's migrations create its table, and the purge covers it.

`RetentionPurgeService` resolves `IIdempotencyStore` unconditionally for
ADR-039's marker purge, and the shared one is Redis-backed, so Shipping
registers its own:

```csharp
using Common.Application;

namespace Shipping.Infrastructure.Idempotency;

/// <summary>
/// §2: this service opts no command into idempotency — it has no HTTP write
/// command to take §8.5's key — so nothing here ever claims one.
/// <c>RetentionPurgeService</c> (Common.Infrastructure) still resolves
/// <see cref="IIdempotencyStore"/> unconditionally to purge the marker table by
/// age (ADR-039); every other member exists only to say why a caller reached
/// it in error.
/// </summary>
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

    // No claim is ever taken, so none can still be held: every key the purge
    // asks about is unheld (ADR-039).
    public Task<IReadOnlyCollection<string>> UnheldAsync(IReadOnlyCollection<string> keys, CancellationToken ct) =>
        Task.FromResult(keys);

    private static InvalidOperationException NoIdempotentCommand() =>
        new("Shipping has no IIdempotentCommand (§8.5); giving it one means registering the " +
            "Redis-backed IIdempotencyStore, not this one.");
}
```

registered beside the marker store:

```csharp
        // §2: no Redis. The purge still needs the port (ADR-039), so this
        // service registers the one that never claims rather than the shared
        // Redis-backed one it has no connection for.
        services.AddSingleton<IIdempotencyStore, NoClaimsIdempotencyStore>();
```

In the factory, the constructor becomes
`ShippingWorkerFactory(string connectionString, string rabbitConnectionString)`.
In the fixture, `Factory = new ShippingWorkerFactory(ConnectionString,
_rabbit.GetConnectionString());`, and the `Task.WhenAll` starts two containers.

- [ ] **Step 5: §2's sentence**

Replace the last sentence of §2's "Two Redis instances, not one" bullet —

> Payments reaches neither: it caches nothing, and §8.5's keys belong to HTTP
> write commands, which it does not have — its idempotency is the payment
> provider's key and its own rows.

with:

> Payments and Shipping reach neither: both cache nothing, and §8.5's keys
> belong to HTTP write commands, which neither has — Payments' idempotency is
> the payment provider's key and its own rows, and Shipping's is the lease its
> two workers take in SQL.

Wrap at 80 columns. Run `/check-links` and `/validate-blueprint`.

- [ ] **Step 6: Run the Shipping suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Domain.Tests
dotnet test tests/Shipping.Application.Tests
dotnet test tests/Shipping.Worker.Tests
```

Expected: 0 warnings, all green, `NoRedisTests` included.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Shipping tests/Shipping.* deploy/compose/services/shipping.yml \
        docs/backend-architecture/02-architecture-at-a-glance.md
git commit -m "feat(shipping): no Redis, because two workers lease rows in SQL and nothing caches"
```

---

### Task 5: The broker account a receive endpoint will need

**Files:**
- Modify: `deploy/compose/rabbitmq/definitions.json` — `shipping-svc`'s three
  patterns

- [ ] **Step 1: Run the gate and its suite on the rendered grant**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Expected: both exit 0. The render copied Catalog's publisher-only patterns
under a `shipping-` prefix, and Shipping declares no receive endpoint yet, so
nothing is short — which is exactly why the widening has to be argued rather
than discovered.

- [ ] **Step 2: Widen it to `ordering-svc`'s shape**

Spec section 8: the account takes `ordering-svc`'s shape under a `shipping-`
prefix from PR-1, because PR-5's `shipping-events` queue needs it and
`check_permissions.py` holds the entry to the code either way. In
`deploy/compose/rabbitmq/definitions.json`, `shipping-svc`'s three patterns
become:

```json
      "configure": "^(shipping-|Common\\.Contracts|Shipping\\.Infrastructure\\.Messaging:|MassTransit:)",
      "write": "^(shipping-|Common\\.Contracts(\\.Shipping\\.V1:|:)|Shipping\\.Infrastructure\\.Messaging:|MassTransit:)",
      "read": "^(shipping-|Common\\.Contracts|Shipping\\.Infrastructure\\.Messaging:|MassTransit:)"
```

`shipping-` admits `shipping-events` when PR-5 declares it. `write` admits
Shipping's own contract exchanges — `Common.Contracts.Shipping.V1` already
holds `ShipmentDispatched` and `ShipmentDelivered` (§3.2) — and the bare
`Common.Contracts:` interface exchange, and no other context's. The render
copies `catalog-svc`'s three patterns, so what arrives names Inventory's
contracts in two of them and in two different shapes: `configure` carries the
grouped `Common\.Contracts(\.Shipping\.V1:|\.Inventory\.V1:|:)`, and `read`
carries two ungrouped alternatives,
`Common\.Contracts\.Shipping\.V1:|Common\.Contracts\.Inventory\.V1:`. Both are
Catalog's consumption of Inventory's stock levels wearing Shipping's name, and
both go with this edit; `ordering-svc`'s and `payments-svc`'s shape — a bare
`Common\.Contracts` on `configure` and `read` — is what the three above are.

- [ ] **Step 3: Run the gate and its suite again**

```bash
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Expected: both exit 0. Check 5 skips a queue whose name starts with
`shipping-`, so the wider `configure` and `read` are not a grant over anybody
else's endpoint, and `Shipping.Infrastructure.Messaging:` is this service's
own private vocabulary rather than a peer's.

- [ ] **Step 4: Commit**

```bash
git add deploy/compose/rabbitmq/definitions.json
git commit -m "feat(shipping): the broker grant a receive endpoint will need"
```

The body says the grant is `ordering-svc`'s shape under a `shipping-` prefix,
that it arrives now rather than with PR-5 because the gate reads the code and
would hold the entry either way, and that the rendered copy carried a peer's
contract namespace it has no business reading.

---

### Task 6: `Shipment`, `TrackingEvent` and the state machine

**Files:**
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/ShipmentId.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/OrderId.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/ShipmentStatus.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/TrackingStatus.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/ShipmentLimits.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/TrackingEvent.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/Shipment.cs`
- Create: `src/Services/Shipping/Shipping.Domain/Shipments/Events/ShipmentEvents.cs`
- Delete: `src/Services/Shipping/Shipping.Domain/AssemblyMarker.cs`
- Modify: `tests/Shipping.Domain.Tests/ArchitectureTests.cs`,
  `tests/Shipping.Application.Tests/ArchitectureTests.cs`,
  `tests/Shipping.Worker.Tests/ArchitectureTests.cs` — re-anchor on `Shipment`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
  — `MessageTypeSource`'s domain anchor
- Test: `tests/Shipping.Domain.Tests/ShipmentTests.cs`
- Test: `tests/Shipping.Domain.Tests/ShuffledTrackingFeedTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Shipping.Domain.Shipments;
public readonly record struct ShipmentId(Guid Value) { public static ShipmentId New(); }
public readonly record struct OrderId(Guid Value);
public enum ShipmentStatus { Pending, Booked, Dispatched, Delivered, Voided, Unfulfillable }
public enum TrackingStatus { Collected, InTransit, Delivered, Unrecognised }

public sealed class Shipment : AggregateRoot<ShipmentId>
{
    public static Shipment For(ShipmentId id, OrderId orderId, DateTimeOffset now);
    public bool Book(string carrierReference, string trackingNumber, DateTimeOffset now);
    public bool MarkUnfulfillable(string reason, DateTimeOffset now);
    public bool Cancel(DateTimeOffset now);
    public bool CarrierCancelled(DateTimeOffset now);
    public bool CarrierRefusedCancellation(DateTimeOffset now);
    public bool Record(string carrierEventId, TrackingStatus status, DateTimeOffset occurredAt, DateTimeOffset now);
}

namespace Shipping.Domain.Shipments.Events;
public sealed record ShipmentDispatchedDomainEvent(
    ShipmentId ShipmentId, OrderId OrderId, string TrackingNumber, DateTimeOffset OccurredAt) : IDomainEvent;
public sealed record ShipmentDeliveredDomainEvent(
    ShipmentId ShipmentId, OrderId OrderId, string TrackingNumber, DateTimeOffset OccurredAt) : IDomainEvent;
```

- [ ] **Step 1: Write the failing domain tests**

`tests/Shipping.Domain.Tests/ShipmentTests.cs` — one test per row of the
spec's section 5 table, and one per refused arrival:

```csharp
using Common.Domain;
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

public class ShipmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static Shipment Pending() => Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);

    private static Shipment Booked()
    {
        Shipment shipment = Pending();
        shipment.Book("car_1", "TRK1", Now).ShouldBeTrue();
        return shipment;
    }

    [Fact]
    public void A_confirmed_order_creates_a_pending_shipment_and_raises_nothing()
    {
        Shipment shipment = Pending();

        shipment.Status.ShouldBe(ShipmentStatus.Pending);
        shipment.CarrierReference.ShouldBeNull();
        shipment.TrackingNumber.ShouldBeNull();
        shipment.TerminalAt.ShouldBeNull();
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void The_carrier_booking_records_its_reference_and_tracking_number()
    {
        Shipment shipment = Booked();

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.CarrierReference.ShouldBe("car_1");
        shipment.TrackingNumber.ShouldBe("TRK1");
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void An_answer_that_it_cannot_be_done_is_terminal_and_carries_its_reason()
    {
        Shipment shipment = Pending();

        shipment.MarkUnfulfillable("address_not_serviceable", Now).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Unfulfillable);
        shipment.UnfulfillableReason.ShouldBe("address_not_serviceable");
        shipment.TerminalAt.ShouldBe(Now);
    }

    [Fact]
    public void A_cancellation_voids_a_pending_shipment_and_only_requests_it_of_a_booked_one()
    {
        Shipment pending = Pending();
        pending.Cancel(Now).ShouldBeTrue();
        pending.Status.ShouldBe(ShipmentStatus.Voided);
        pending.TerminalAt.ShouldBe(Now);

        Shipment booked = Booked();
        booked.Cancel(Now).ShouldBeTrue();
        booked.Status.ShouldBe(ShipmentStatus.Booked, "the parcel may already be moving");
        booked.CancellationRequestedAt.ShouldBe(Now);
        booked.TerminalAt.ShouldBeNull();
    }

    [Fact]
    public void The_carrier_cancelling_voids_a_requested_cancellation()
    {
        Shipment shipment = Booked();
        shipment.Cancel(Now);

        shipment.CarrierCancelled(Now.AddMinutes(1)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Voided);
        shipment.TerminalAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void The_carrier_answering_too_late_stamps_the_refusal_and_tracking_goes_on()
    {
        Shipment shipment = Booked();
        shipment.Cancel(Now);

        shipment.CarrierRefusedCancellation(Now.AddMinutes(1)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.CancellationRefusedAt.ShouldBe(Now.AddMinutes(1));

        shipment.Record("e1", TrackingStatus.Collected, Now.AddMinutes(2), Now.AddMinutes(3)).ShouldBeTrue();
        shipment.Status.ShouldBe(ShipmentStatus.Dispatched);
    }

    [Fact]
    public void A_collected_event_despatches_a_booked_shipment()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.Collected, Now.AddHours(1), Now.AddHours(2)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Dispatched);
        shipment.DomainEvents.ShouldHaveSingleItem()
            .ShouldBe(new ShipmentDispatchedDomainEvent(shipment.Id, shipment.OrderId, "TRK1", Now.AddHours(1)));
    }

    [Fact]
    public void A_delivered_event_on_a_booked_shipment_raises_the_despatch_first()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.Delivered, Now.AddHours(1), Now.AddHours(2)).ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Delivered);
        shipment.TerminalAt.ShouldBe(Now.AddHours(2));
        shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)],
            "the despatch was never raised, and a delivery that precedes it is not a timeline");
    }

    [Fact]
    public void A_delivered_event_on_a_dispatched_shipment_raises_the_delivery_alone()
    {
        Shipment shipment = Booked();
        shipment.Record("e1", TrackingStatus.Collected, Now.AddHours(1), Now.AddHours(1));

        shipment.Record("e2", TrackingStatus.Delivered, Now.AddHours(2), Now.AddHours(2)).ShouldBeTrue();

        shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)]);
    }

    [Fact]
    public void An_in_transit_or_unrecognised_event_is_recorded_and_moves_nothing()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.InTransit, Now, Now).ShouldBeFalse();
        shipment.Record("e2", TrackingStatus.Unrecognised, Now, Now).ShouldBeFalse();

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.TrackingEvents.Count.ShouldBe(2, "a carrier's fact is kept whether or not it moves the row");
        shipment.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void A_repeated_page_is_free()
    {
        Shipment shipment = Booked();

        shipment.Record("e1", TrackingStatus.Collected, Now, Now).ShouldBeTrue();
        shipment.Record("e1", TrackingStatus.Collected, Now, Now.AddHours(1)).ShouldBeFalse();

        shipment.TrackingEvents.Count.ShouldBe(1);
        shipment.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void Every_superseded_arrival_is_a_no_op_rather_than_a_throw()
    {
        Shipment delivered = Booked();
        delivered.Record("e1", TrackingStatus.Delivered, Now, Now);

        // A Collected after a Delivered, a second cancellation, a cancellation
        // of a delivered shipment, a booking of something already booked: each
        // is a fact already superseded, and a throw here is a worker row
        // retried for ever or a consumer redelivery loop into `_error`.
        delivered.Record("e2", TrackingStatus.Collected, Now, Now).ShouldBeFalse();
        delivered.Cancel(Now).ShouldBeFalse();
        delivered.Book("car_2", "TRK2", Now).ShouldBeFalse();
        delivered.MarkUnfulfillable("too_late", Now).ShouldBeFalse();
        delivered.CarrierCancelled(Now).ShouldBeFalse();
        delivered.CarrierRefusedCancellation(Now).ShouldBeFalse();

        delivered.Status.ShouldBe(ShipmentStatus.Delivered);
        delivered.TrackingNumber.ShouldBe("TRK1");
        delivered.DomainEvents.Count.ShouldBe(2);

        Shipment voided = Pending();
        voided.Cancel(Now);
        voided.Cancel(Now).ShouldBeFalse();
        voided.Book("car_2", "TRK2", Now).ShouldBeFalse();
        voided.Record("e1", TrackingStatus.Collected, Now, Now).ShouldBeFalse();
        voided.Status.ShouldBe(ShipmentStatus.Voided);

        Shipment unfulfillable = Pending();
        unfulfillable.MarkUnfulfillable("address_not_serviceable", Now);
        unfulfillable.Book("car_2", "TRK2", Now).ShouldBeFalse();
        unfulfillable.Cancel(Now).ShouldBeFalse();
        unfulfillable.Status.ShouldBe(ShipmentStatus.Unfulfillable);
    }

    [Fact]
    public void A_carrier_answer_the_columns_cannot_hold_is_a_broken_invariant()
    {
        // Malformed input is §5.7's DomainException and not a no-op: an empty
        // reference or one past the column's width is the adapter having
        // failed to bound what the carrier sent, which is a defect.
        Should.Throw<DomainException>(() => Pending().Book(" ", "TRK1", Now));
        Should.Throw<DomainException>(() => Pending().Book("car_1", "", Now));
        Should.Throw<DomainException>(() => Pending().MarkUnfulfillable("", Now));
        Should.Throw<DomainException>(() =>
            Pending().Book(new string('x', ShipmentLimits.MaxCarrierReferenceLength + 1), "TRK1", Now));
        Should.Throw<DomainException>(() =>
            Booked().Record("", TrackingStatus.Collected, Now, Now));
    }
}
```

- [ ] **Step 2: Write the failing shuffled-feed test**

```csharp
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>
/// Spec section 5: the key `(ShipmentId, CarrierEventId)` orders nothing, so
/// the state machine is monotonic by rank rather than by arrival. A carrier
/// page can hold `delivered` above `collected` — the simulator's
/// `SIM-REVERSED` is exactly that — and the timeline the platform publishes
/// must not depend on which order the feed arrived in.
/// </summary>
public class ShuffledTrackingFeedTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static readonly (string Id, TrackingStatus Status, int Minute)[] Feed =
    [
        ("e1", TrackingStatus.Collected, 10),
        ("e2", TrackingStatus.InTransit, 20),
        ("e3", TrackingStatus.Unrecognised, 30),
        ("e4", TrackingStatus.Delivered, 40)
    ];

    [Fact]
    public void Every_permutation_reaches_one_terminal_state_and_one_sequence_of_events()
    {
        foreach ((string, TrackingStatus, int)[] order in Permutations(Feed))
        {
            Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Raised);
            shipment.Book("car_1", "TRK1", Raised);

            foreach ((string id, TrackingStatus status, int minute) in order)
                shipment.Record(id, status, Raised.AddMinutes(minute), Raised.AddMinutes(minute));

            string arrival = string.Join(",", order.Select(e => e.Item1));

            shipment.Status.ShouldBe(ShipmentStatus.Delivered, arrival);
            shipment.TrackingEvents.Select(e => e.CarrierEventId).OrderBy(id => id)
                .ShouldBe(["e1", "e2", "e3", "e4"], arrival);

            // Despatch before delivery, whatever order the carrier reported
            // them in. Ordering's saga finalises on the first and Notifications
            // reads both, so a delivery ahead of a despatch is a timeline no
            // consumer can make sense of.
            shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
                [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)], arrival);
        }
    }

    /// <summary>Every ordering of the feed, by recursive selection.</summary>
    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }

        for (int index = 0; index < items.Length; index++)
        {
            T[] rest = [.. items[..index], .. items[(index + 1)..]];

            foreach (T[] tail in Permutations(rest))
                yield return [items[index], .. tail];
        }
    }
}
```

- [ ] **Step 3: Run them to see them fail**

```bash
dotnet test tests/Shipping.Domain.Tests
```

Expected: compile failure on every missing type.

- [ ] **Step 4: Write the ids, the enums and the bounds**

```csharp
namespace Shipping.Domain.Shipments;

/// <summary>§5.2's typed identifier for a shipment.</summary>
public readonly record struct ShipmentId(Guid Value)
{
    public static ShipmentId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
```

```csharp
namespace Shipping.Domain.Shipments;

/// <summary>
/// §5.2's typed identifier for the order a shipment answers for. Shipping's
/// own type rather than Ordering's: an identifier crossing a context boundary
/// arrives as a primitive (§9.1) and is given this service's meaning here.
/// </summary>
public readonly record struct OrderId(Guid Value)
{
    public override string ToString() => Value.ToString();
}
```

```csharp
namespace Shipping.Domain.Shipments;

/// <summary>
/// The states of the spec's section 5 table. <c>Voided</c>,
/// <c>Unfulfillable</c> and <c>Delivered</c> are terminal.
/// </summary>
/// <remarks>
/// The order of the first four is the rank the tracking feed promotes by
/// (spec, section 5): a carrier page orders nothing, so an arrival may only
/// move the shipment forward. The terminal-by-cancellation members sit last
/// because nothing promotes into them, and promotion compares rank alone.
/// </remarks>
public enum ShipmentStatus
{
    Pending,
    Booked,
    Dispatched,
    Delivered,
    Voided,
    Unfulfillable,
}
```

```csharp
namespace Shipping.Domain.Shipments;

/// <summary>
/// The platform's own tracking vocabulary, closed (spec, section 5). A carrier
/// word the translation does not know is stored as <see cref="Unrecognised"/>
/// and moves nothing: a carrier adds statuses on its own schedule, and a
/// conformist that faulted on a new one would stop tracking every shipment
/// until a deploy.
/// </summary>
public enum TrackingStatus
{
    Collected,
    InTransit,
    Delivered,
    Unrecognised,
}
```

```csharp
namespace Shipping.Domain.Shipments;

/// <summary>
/// The widths this service stores a carrier's strings at, named once because
/// the aggregate's guards and the entity configurations must agree: a value
/// the column refuses and the domain accepted is a commit that fails at
/// <c>SaveChanges</c>, one layer away from whatever produced it.
/// </summary>
public static class ShipmentLimits
{
    public const int MaxCarrierReferenceLength = 64;
    public const int MaxTrackingNumberLength = 64;
    public const int MaxUnfulfillableReasonLength = 100;
    public const int MaxCarrierEventIdLength = 100;
}
```

- [ ] **Step 5: Write the entity and the events**

```csharp
namespace Shipping.Domain.Shipments;

/// <summary>
/// One fact the carrier reported about a shipment (spec, section 5). An entity
/// of <see cref="Shipment"/>, reached only through it.
/// </summary>
/// <remarks>
/// Not an <c>Entity&lt;TId&gt;</c>: its identity is
/// <c>(ShipmentId, CarrierEventId)</c> and that base type keys on one struct.
/// The carrier's own id is what makes a repeated page free, and it orders
/// nothing — which is why the state machine is monotonic by rank.
/// </remarks>
public sealed class TrackingEvent
{
    public ShipmentId ShipmentId { get; private set; }

    public string CarrierEventId { get; private set; } = "";

    public TrackingStatus Status { get; private set; }

    /// <summary>The carrier's timestamp, bounded by the adapter before it arrives.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When this service first saw it, which is the clock retention ages by.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private TrackingEvent() { }

    /// <summary>
    /// <c>internal</c>, not public: an event is created by
    /// <see cref="Shipment"/> and by nothing else, so the deduplication and
    /// the promotion cannot be bypassed.
    /// </summary>
    internal TrackingEvent(
        ShipmentId shipmentId,
        string carrierEventId,
        TrackingStatus status,
        DateTimeOffset occurredAt,
        DateTimeOffset recordedAt)
    {
        ShipmentId = shipmentId;
        CarrierEventId = carrierEventId;
        Status = status;
        OccurredAt = occurredAt;
        RecordedAt = recordedAt;
    }
}
```

```csharp
using Common.Domain;

namespace Shipping.Domain.Shipments.Events;

/// <summary>
/// The shipment left the carrier's hands (spec, section 5). §9.3's mapper
/// turns it into <c>Common.Contracts.Shipping.V1.ShipmentDispatched</c>, which
/// is what finalises Ordering's saga and fulfils Inventory's reservation.
/// </summary>
public sealed record ShipmentDispatchedDomainEvent(
    ShipmentId ShipmentId,
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// The shipment reached the customer (spec, section 5). Ordering never learns
/// of it — the saga has already finalised — so its contract's consumers are
/// Notifications and ADR-051's projection.
/// </summary>
public sealed record ShipmentDeliveredDomainEvent(
    ShipmentId ShipmentId,
    OrderId OrderId,
    string TrackingNumber,
    DateTimeOffset OccurredAt) : IDomainEvent;
```

- [ ] **Step 6: Write the aggregate**

```csharp
using Common.Domain;
using Shipping.Domain.Shipments.Events;

namespace Shipping.Domain.Shipments;

/// <summary>
/// §3.2's aggregate: one shipment per confirmed order, and the spec's
/// section 5 table is its states and the only moves between them.
/// </summary>
/// <remarks>
/// Every operation returns whether it moved the shipment; a superseded
/// arrival returns <c>false</c> rather than throwing, because a throw is a
/// row retried for ever in a worker and a redelivery loop in a consumer. The
/// backoff, the lease and the poll schedule are properties and no behaviour.
/// </remarks>
public sealed class Shipment : AggregateRoot<ShipmentId>
{
    private readonly List<TrackingEvent> _trackingEvents = [];

    public OrderId OrderId { get; private set; }

    public ShipmentStatus Status { get; private set; }

    public string? CarrierReference { get; private set; }

    public string? TrackingNumber { get; private set; }

    public string? UnfulfillableReason { get; private set; }

    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    public DateTimeOffset? CancellationRefusedAt { get; private set; }

    /// <summary>
    /// When the shipment reached a terminal state, and the clock the address
    /// and tracking retention windows are measured from (ADR-053).
    /// </summary>
    public DateTimeOffset? TerminalAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? NextPollAt { get; private set; }

    public IReadOnlyList<TrackingEvent> TrackingEvents => _trackingEvents.AsReadOnly();

    // EF Core materialisation only (§5.4).
    private Shipment() { }

    private Shipment(ShipmentId id, OrderId orderId, DateTimeOffset now)
    {
        Id = id;
        OrderId = orderId;
        Status = ShipmentStatus.Pending;
        NextAttemptAt = now;
    }

    /// <summary>The first row of the spec's section 5 table: a confirmed order makes a pending shipment.</summary>
    public static Shipment For(ShipmentId id, OrderId orderId, DateTimeOffset now) => new(id, orderId, now);

    /// <summary>The carrier booked it, and answered with a reference and a tracking number.</summary>
    public bool Book(string carrierReference, string trackingNumber, DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Pending)
            return false;

        Require(carrierReference, ShipmentLimits.MaxCarrierReferenceLength, "the carrier's reference");
        Require(trackingNumber, ShipmentLimits.MaxTrackingNumberLength, "a tracking number");

        Status = ShipmentStatus.Booked;
        CarrierReference = carrierReference;
        TrackingNumber = trackingNumber;
        NextPollAt = now;
        return true;
    }

    /// <summary>The address owner or the carrier answered that it cannot be done.</summary>
    public bool MarkUnfulfillable(string reason, DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Pending)
            return false;

        Require(reason, ShipmentLimits.MaxUnfulfillableReasonLength, "a reason");

        Status = ShipmentStatus.Unfulfillable;
        UnfulfillableReason = reason;
        TerminalAt = now;
        return true;
    }

    /// <summary>
    /// <c>OrderCancelled</c> arrived. A pending shipment is voided at once and
    /// is never booked; a booked one only records the request, because the
    /// parcel may already be moving and the carrier decides (spec, section 6).
    /// </summary>
    public bool Cancel(DateTimeOffset now)
    {
        if (Status == ShipmentStatus.Pending)
        {
            Status = ShipmentStatus.Voided;
            TerminalAt = now;
            return true;
        }

        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is not null)
            return false;

        CancellationRequestedAt = now;
        return true;
    }

    /// <summary>The carrier cancelled it.</summary>
    public bool CarrierCancelled(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is null)
            return false;

        Status = ShipmentStatus.Voided;
        TerminalAt = now;
        return true;
    }

    /// <summary>
    /// The carrier answered that it has gone. Tracking goes on and the
    /// despatch is published when <c>Collected</c> arrives; the saga's
    /// <c>CancelledAfterConfirmation</c> review row is the one record of the
    /// disagreement (spec, section 6).
    /// </summary>
    public bool CarrierRefusedCancellation(DateTimeOffset now)
    {
        if (Status != ShipmentStatus.Booked || CancellationRequestedAt is null || CancellationRefusedAt is not null)
            return false;

        CancellationRefusedAt = now;
        return true;
    }

    /// <summary>
    /// One arrival from the carrier's feed. The row is kept whether or not it
    /// moves the shipment — a carrier's fact is a fact — and the return says
    /// only whether the state moved.
    /// </summary>
    public bool Record(string carrierEventId, TrackingStatus status, DateTimeOffset occurredAt, DateTimeOffset now)
    {
        Require(carrierEventId, ShipmentLimits.MaxCarrierEventIdLength, "the carrier's event id");

        // The key makes a repeated page free (spec, section 5).
        if (_trackingEvents.Any(e => e.CarrierEventId == carrierEventId))
            return false;

        _trackingEvents.Add(new TrackingEvent(Id, carrierEventId, status, occurredAt, now));

        return status switch
        {
            TrackingStatus.Collected => Dispatch(occurredAt),
            TrackingStatus.Delivered => Deliver(occurredAt, now),
            _ => false,
        };
    }

    private bool Dispatch(DateTimeOffset occurredAt)
    {
        if (Status != ShipmentStatus.Booked)
            return false;

        Status = ShipmentStatus.Dispatched;
        Raise(new ShipmentDispatchedDomainEvent(Id, OrderId, TrackingNumber!, occurredAt));
        return true;
    }

    private bool Deliver(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        if (Status is not (ShipmentStatus.Booked or ShipmentStatus.Dispatched))
            return false;

        // The despatch first when it was never raised. A delivery ahead of a
        // despatch is not a timeline, and the two consumers of the first event
        // have already acted by the time the second is read.
        if (Status == ShipmentStatus.Booked)
            Dispatch(occurredAt);

        Status = ShipmentStatus.Delivered;
        TerminalAt = now;
        Raise(new ShipmentDeliveredDomainEvent(Id, OrderId, TrackingNumber!, occurredAt));
        return true;
    }

    /// <summary>
    /// A value the columns cannot hold is §5.7's broken invariant rather than a
    /// no-op: the adapter bounds what the carrier sends (spec, section 9), so
    /// anything arriving here oversized is a defect above this line.
    /// </summary>
    private static void Require(string value, int maxLength, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException($"A shipment needs {what}.");

        if (value.Length > maxLength)
            throw new DomainException($"{what} is longer than {maxLength} characters; the adapter bounds it.");
    }
}
```

- [ ] **Step 7: Delete the marker and re-anchor the gates**

`AssemblyMarker.cs` is the one generated file written to be deleted, and the
first aggregate is when. Delete
`src/Services/Shipping/Shipping.Domain/AssemblyMarker.cs` and re-anchor:

- `tests/Shipping.Domain.Tests/ArchitectureTests.cs`:
  `typeof(AssemblyMarker).Assembly` becomes `typeof(Shipment).Assembly`, the
  `using` becomes `using Shipping.Domain.Shipments;`, and the allow-list
  becomes the four an aggregate with a child collection references, with the
  comment saying what earned each:

```csharp
        // Common.Domain and System.Runtime are what an empty domain
        // references. System.Collections is the typed ids' — a readonly record
        // struct's generated equality goes through EqualityComparer<T> — and
        // the shipment's list of tracking events. System.Linq is the
        // deduplication over that list, which is domain work over owned values
        // rather than an I/O dependency.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Shipment).Assembly
```

- `tests/Shipping.Application.Tests/ArchitectureTests.cs` and
  `tests/Shipping.Worker.Tests/ArchitectureTests.cs`: the same substitution
  wherever `AssemblyMarker` is named.
- `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`:
  `MessageTypeSource`'s domain anchor becomes `typeof(Shipment).Assembly`, the
  `using` becomes `using Shipping.Domain.Shipments;`, and the comment loses the
  half that says the service has no aggregate:

```csharp
        // The contracts half is still IIntegrationEvent, because §9.3's
        // allow-list is empty until this service publishes something; the
        // domain half is the aggregate now (§9.4).
        services.AddSingleton(
            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(Shipment).Assembly));
```

- [ ] **Step 8: Run the domain suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Domain.Tests
```

Expected: 0 warnings, every test green, including all twenty-four permutations
of the shuffled feed.

- [ ] **Step 9: Commit**

```bash
git add src/Services/Shipping tests/Shipping.Domain.Tests tests/Shipping.Application.Tests \
        tests/Shipping.Worker.Tests
git commit -m "feat(shipping): Shipment, TrackingEvent and the state machine of section 5"
```

The body argues the two decisions a reviewer would question: that a superseded
arrival returns rather than throws, and why; and that the feed is monotonic by
rank rather than by arrival, because the carrier's key orders nothing.

---

### Task 7: The `Shipments` and `TrackingEvents` tables

**Files:**
- Create: `src/Services/Shipping/Shipping.Infrastructure/Persistence/ShipmentConfiguration.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Persistence/TrackingEventConfiguration.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Persistence/ShippingDbContext.cs`
- Create (generated): `Shipping.Infrastructure/Persistence/Migrations/<ts>_AddShipments.cs`
  and its designer, and the rewritten `ShippingDbContextModelSnapshot.cs`
- Test: `tests/Shipping.Worker.Tests/ShipmentsSchemaTests.cs`

**Interfaces:**
- Produces: `shipping.Shipments(Id uniqueidentifier PK, OrderId uniqueidentifier
  UNIQUE, Status nvarchar(16), CarrierReference nvarchar(64) NULL,
  TrackingNumber nvarchar(64) NULL, UnfulfillableReason nvarchar(100) NULL,
  CancellationRequestedAt datetimeoffset(7) NULL, CancellationRefusedAt
  datetimeoffset(7) NULL, TerminalAt datetimeoffset(7) NULL, Attempts int,
  NextAttemptAt datetimeoffset(7), LockedUntil datetimeoffset(7) NULL,
  NextPollAt datetimeoffset(7) NULL, RowVersion rowversion)`.
- Produces: `shipping.TrackingEvents(ShipmentId uniqueidentifier,
  CarrierEventId nvarchar(100), Status nvarchar(16), OccurredAt
  datetimeoffset(7), RecordedAt datetimeoffset(7))`, keyed on the first two.
- Produces: `ShippingDbContext.Shipments`.

- [ ] **Step 1: Write the failing schema tests**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Persistence;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The spec's section 7, against the engine the migrator ran on. The aggregate
/// lands in this pull request and nothing drives it until PR-5, so this is
/// what makes the migration real: a table nobody has inserted into is a table
/// nobody has checked.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShipmentsSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_shipments_table_holds_every_column_section_7_names()
    {
        string[] columns = await fixture.ColumnsAsync("shipping", "Shipments");

        columns.ShouldBe(
            [
                "Attempts", "CancellationRefusedAt", "CancellationRequestedAt", "CarrierReference",
                "Id", "LockedUntil", "NextAttemptAt", "NextPollAt", "OrderId", "RowVersion",
                "Status", "TerminalAt", "TrackingNumber", "UnfulfillableReason"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task The_tracking_events_table_is_keyed_on_the_carriers_own_id()
    {
        string[] columns = await fixture.ColumnsAsync("shipping", "TrackingEvents");

        columns.ShouldBe(["CarrierEventId", "OccurredAt", "RecordedAt", "ShipmentId"], ignoreOrder: true);

        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.object_id = OBJECT_ID('shipping.TrackingEvents') AND i.is_primary_key = 1
            """))
            .ShouldBe(2, "the key is the pair, so a repeated page is free");
    }

    [Fact]
    public async Task One_shipment_per_order_is_the_database_s_rule_and_not_only_the_aggregate_s()
    {
        OrderId order = new(Guid.CreateVersion7());

        await SaveAsync(Shipment.For(ShipmentId.New(), order, Now));

        // Two services decide nothing here — this is one consumer redelivered
        // past the inbox, and the unique index is what makes the second write
        // a failure rather than a second shipment nobody reconciles.
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(Shipment.For(ShipmentId.New(), order, Now)));
    }

    [Fact]
    public async Task A_shipment_round_trips_with_its_tracking_events()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        shipment.Record("e1", TrackingStatus.Collected, Now.AddHours(1), Now.AddHours(1));
        shipment.Record("e2", TrackingStatus.Unrecognised, Now.AddHours(2), Now.AddHours(2));

        await SaveAsync(shipment);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        Shipment read = await db.Shipments
            .Include(s => s.TrackingEvents)
            .SingleAsync(s => s.Id == shipment.Id, TestContext.Current.CancellationToken);

        read.Status.ShouldBe(ShipmentStatus.Dispatched);
        read.CarrierReference.ShouldBe("car_1");
        read.Version.ShouldNotBeEmpty("the rowversion is what §6.3's concurrency check reads");
        read.TrackingEvents.Select(e => e.Status).ShouldBe(
            [TrackingStatus.Collected, TrackingStatus.Unrecognised], ignoreOrder: true);

        // By name, never by number (§7.2): an enum stored as an int makes the
        // member order a storage contract.
        (await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM shipping.Shipments WHERE Id = {0}", shipment.Id.Value))
            .ShouldBe("Dispatched");
    }

    private async Task SaveAsync(Shipment shipment)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

        db.Shipments.Add(shipment);
        // The domain events stay on the aggregate here: §7.5's dispatcher runs
        // inside the unit of work, and this test writes through the context
        // directly because PR-5 brings the first command that does not.
        shipment.ClearDomainEvents();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
```

`ServiceFixture.ColumnsAsync(schema, table)` is the one helper this needs that
the rendered fixture may not carry. If it does not, add it beside
`ScalarAsync`:

```csharp
    /// <summary>The column names of one table, from the engine rather than from the model.</summary>
    public async Task<string[]> ColumnsAsync(string schema, string table)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ShippingDbContext db = scope.ServiceProvider.GetRequiredService<ShippingDbContext>();

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

Run: `dotnet test tests/Shipping.Worker.Tests --filter ShipmentsSchemaTests`
Expected: compile failure on `db.Shipments`, then — once the `DbSet` exists and
before the migration — a SQL error naming `shipping.Shipments` as an invalid
object name.

- [ ] **Step 3: Write the configurations and the `DbSet`**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

/// <summary>
/// §7.2's pattern: configuration in a class, never in attributes on the domain
/// type — which would put EF Core in <c>Shipping.Domain</c>, past the gate.
/// Found by <c>ApplyConfigurationsFromAssembly</c>.
/// </summary>
internal sealed class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> builder)
    {
        builder.ToTable("Shipments", "shipping");
        builder.HasKey(s => s.Id);

        builder
            .Property(s => s.Id)
            .HasConversion(id => id.Value, value => new ShipmentId(value))
            .ValueGeneratedNever();

        builder
            .Property(s => s.OrderId)
            .HasConversion(id => id.Value, value => new OrderId(value));

        // One shipment per confirmed order (§3.2), and the database is where
        // that holds: a consumer redelivered past the inbox would otherwise
        // write a second shipment nobody reconciles, and the two would then
        // both be booked with the carrier.
        builder.HasIndex(s => s.OrderId).IsUnique();

        // By name, never by number (§7.2). An enum stored as an int makes the
        // member order a storage contract: inserting a status in the middle
        // silently reinterprets every existing row.
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);

        builder.Property(s => s.CarrierReference).HasMaxLength(ShipmentLimits.MaxCarrierReferenceLength);
        builder.Property(s => s.TrackingNumber).HasMaxLength(ShipmentLimits.MaxTrackingNumberLength);
        builder.Property(s => s.UnfulfillableReason).HasMaxLength(ShipmentLimits.MaxUnfulfillableReasonLength);

        // The two workers' bookkeeping, mapped here because the columns are
        // this row's (spec, section 7). The claim, the backoff and the poll
        // schedule arrive with the workers that run them.
        builder.Property(s => s.Attempts);
        builder.Property(s => s.NextAttemptAt);
        builder.Property(s => s.LockedUntil);
        builder.Property(s => s.NextPollAt);

        builder.Property(s => s.Version).HasColumnName("RowVersion").IsRowVersion();

        builder.Ignore(s => s.DomainEvents);

        // A related entity rather than an owned collection: the tracking event
        // has a key of its own that the carrier chose, and an owned collection
        // would give it a synthetic one. The aggregate boundary is kept by
        // what is absent — no DbSet<TrackingEvent> on the context, and the
        // entity's constructor is internal — so the only route to an orphan is
        // the schema permitting one, which IsRequired is what refuses.
        builder
            .HasMany(s => s.TrackingEvents)
            .WithOne()
            .HasForeignKey(e => e.ShipmentId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .Navigation(s => s.TrackingEvents)
            .HasField("_trackingEvents")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
```

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Persistence;

internal sealed class TrackingEventConfiguration : IEntityTypeConfiguration<TrackingEvent>
{
    public void Configure(EntityTypeBuilder<TrackingEvent> builder)
    {
        builder.ToTable("TrackingEvents", "shipping");

        // The carrier's own id, under the shipment's. The key is what makes a
        // repeated page free (spec, section 5), and the shipment leads it
        // because every read is per shipment.
        builder.HasKey(e => new { e.ShipmentId, e.CarrierEventId });

        builder
            .Property(e => e.ShipmentId)
            .HasConversion(id => id.Value, value => new ShipmentId(value));

        builder.Property(e => e.CarrierEventId).HasMaxLength(ShipmentLimits.MaxCarrierEventIdLength);

        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
    }
}
```

`ShippingDbContext` gains one property, beside the three technical ones:

```csharp
    /// <summary>§3.2's aggregate, and the first <c>DbSet</c> here that is one (spec, section 7).</summary>
    public DbSet<Shipment> Shipments => Set<Shipment>();
```

and no `DbSet<TrackingEvent>`, deliberately: a tracking event is reached only
through its shipment.

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddShipments \
    --project src/Services/Shipping/Shipping.Infrastructure \
    --startup-project src/Services/Shipping/Shipping.Migrator \
    --output-dir Persistence/Migrations
```

Open it: exactly `shipping.Shipments` and `shipping.TrackingEvents` with the
columns in Interfaces, the unique index on `OrderId`, the composite primary
key in the order `(ShipmentId, CarrierEventId)`, the cascade foreign key, and
nothing else — no second `CreateTable` for the outbox, the inbox or the marker
table, which the rendered snapshot already describes. Give the file the house
dress: file-scoped namespace, and a doc comment saying the configurations are
the source of truth and the `.Designer.cs` and snapshot beside it are
machine-owned and untouched.

- [ ] **Step 5: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests
```

Expected: green. `DatabaseSmokeTests` counts the applied migrations, so its
`applied.Length.ShouldBe(7)` becomes `8` with `applied[7].ShouldEndWith("_AddShipments")`
— the inverted-floor patch's twin, and the reason the scaffolded assertion is
named and ordered rather than merely counted.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Shipping tests/Shipping.Worker.Tests
git commit -m "feat(shipping): the Shipments and TrackingEvents tables, and AddShipments"
```

The body says why the two tables land before anything drives them — the
scaffold's proof is a service that migrates and starts, and a render with no
table of its own proves the template and not the service — and names the
unique index as the place §3.2's one-shipment-per-order rule is actually held.

---

### Task 8: CI's filter, outputs, matrix legs and the `images` job's `if:`

**Files:**
- Modify: `.github/workflows/ci.yml` — the `changes` job's `outputs` and
  `filters`, the `images` job's `if` and its `matrix.include`

- [ ] **Step 1: Run the pipeline gate to see it fail**

The gate's suite first, then the gate, even on a run expected to fail —
`docs/testing.md`'s order, and the only way a red gate means the tree rather
than the gate:

```bash
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
```

Expected: `filters` refuses `src/Services/Shipping`, and `images` refuses its
two Dockerfiles — the gate globs `src/**/Dockerfile`, so the worker's is
covered by the same rule the API hosts' are.

- [ ] **Step 2: Edit**

`outputs`, after `payments`:

```yaml
      shipping: ${{ steps.changes.outputs.shipping }}
```

`filters`, after the `payments` block:

```yaml
            shipping:
              - *shared
              - 'src/Services/Shipping/**'
              - 'tests/Shipping.*/**'
```

The `images` job's `if` gains `|| needs.changes.outputs.shipping == 'true'`.
Matrix, after the two `payments` entries:

```yaml
          - filter: shipping
            image: shipping-worker
            dockerfile: src/Services/Shipping/Shipping.Worker/Dockerfile
          - filter: shipping
            image: shipping-migrator
            dockerfile: src/Services/Shipping/Shipping.Migrator/Dockerfile
```

`shipping-worker` rather than `shipping-api`, because the image is the host and
the host is a worker — and because `deploy/helm/smoke.sh` and PR-7's chart will
read the same name.

- [ ] **Step 3: Run the gate with its suite**

```bash
py -3.12 -m unittest discover -s .github/pipeline-gate
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
py -3.12 .github/pipeline-gate/pipeline_gate.py images
```

Expected: all exit 0.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: build and filter Shipping's worker and migrator images"
```

---

### Task 9: The scaffold's suite asserts each gate enumerates the render

**Files:**
- Modify: `tools/new-service/test_new_service.py`

**Interfaces:**
- Consumes: `licence_gate.PROJECT_SUFFIXES`, `secret_scan.covers_path`,
  `comment_gate.READERS`, the `ModulePath` in `coverage.runsettings`, and the
  rendered `ArchitectureTests.cs` bodies.

- [ ] **Step 1: Write the failing tests**

The subject is **what each gate is looking at**, not what it found: a gate
whose selector silently stops covering the newest surface is this repository's
most-repeated failure, and the newest surface here is a project tree with a
host nobody had rendered before. In `tools/new-service/test_new_service.py`:

```python
def gate_module(name: str, filename: str):
    """A gate, imported from its own tree.

    Never a second copy of its selector here: the copy is the thing that stops
    agreeing with the gate, which is the failure these tests are about.
    """
    path = REPO_ROOT / ".github" / name / filename
    spec = importlib.util.spec_from_file_location(filename.removesuffix(".py"), path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class EveryGateSeesTheWorkerRender(unittest.TestCase):
    """The worker render's projects are inside every gate's own selector."""

    @classmethod
    def setUpClass(cls):
        cls.rendered = worker()
        cls.paths = sorted({**cls.rendered.created, **cls.rendered.updated})

    def test_the_render_is_the_nine_projects_the_suite_is_about(self):
        # The floor. A render that produced no project file would satisfy every
        # assertion below by having nothing for a gate to miss.
        projects = [p for p in self.paths if p.endswith(".csproj")]
        self.assertEqual(len(projects), 9, projects)

    def test_the_licence_gate_walks_every_rendered_project_file(self):
        gate = gate_module("licence-gate", "licence_gate.py")
        for path in (p for p in self.paths if p.endswith(".csproj")):
            self.assertTrue(
                path.endswith(gate.PROJECT_SUFFIXES), f"{path} is outside the licence gate's walk")

    def test_the_secret_scan_s_allow_list_covers_every_rendered_tree(self):
        gate = load_scan_gate(REPO_ROOT)
        covers = new_service.allow_list_trees(REPO_ROOT, gate)
        for path in self.paths:
            # A file at the repository root is outside every tree the
            # allow-list declares, and that is the design: each .txt names the
            # directory it may suppress a finding in, and Platform.slnx — the
            # one root file a render updates — holds nothing to suppress.
            if "/" not in path:
                continue
            self.assertTrue(
                any(gate.covers_path(prefix, path) for prefix in covers),
                f"no allow-list file covers {path}, so an entry for it would have nowhere to go")

        # Not vacuous: without this the loop above would pass over a render
        # that produced nothing but root files.
        self.assertTrue([p for p in self.paths if "/" in p])

    def test_the_comment_gate_reads_every_rendered_source_file(self):
        gate = gate_module("comment-gate", "comment_gate.py")
        sources = [p for p in self.rendered.created if p.endswith((".cs", ".csproj", ".yml", ".json"))]
        self.assertTrue(sources)
        for path in sources:
            self.assertIn(
                PurePosixPath(path).suffix, gate.READERS,
                f"{path} carries comments the gate has no reader for")

    def test_the_coverage_filter_matches_the_rendered_domain_assembly(self):
        module_path = re.search(
            r"<ModulePath>(.+?)</ModulePath>",
            (REPO_ROOT / "coverage.runsettings").read_text(encoding="utf-8")).group(1)
        self.assertRegex(f"{PROBE}.Domain.dll", module_path)

    def test_the_architecture_gates_name_the_rendered_assemblies(self):
        # §4.2's gates need a type per project, and the render's is the marker
        # written to be deleted. Each suite has to NAME it, or the gate is
        # judging whatever assembly happened to be loaded.
        for suite in ("Domain.Tests", "Application.Tests", f"{new_service.WORKER_HOST}.Tests"):
            body = self.rendered.created[f"tests/{PROBE}.{suite}/ArchitectureTests.cs"]
            self.assertIn("typeof(AssemblyMarker).Assembly", body, suite)
        self.assertIn(
            f"src/Services/{PROBE}/{PROBE}.Domain/AssemblyMarker.cs", self.rendered.created)

    def test_the_pipeline_gate_would_see_both_rendered_dockerfiles(self):
        gate = gate_module("pipeline-gate", "pipeline_gate.py")
        dockerfiles = [p for p in self.rendered.created if p.endswith("/Dockerfile")]
        self.assertEqual(len(dockerfiles), 2, dockerfiles)
        for path in dockerfiles:
            # The gate globs `src/**/Dockerfile` and reads a matrix entry's
            # `dockerfile:` back against it, so what a render owes is a path
            # under src/ — which the worker host is as much as an API host.
            self.assertTrue(path.startswith("src/"), path)
            self.assertTrue(gate.ROOT.joinpath(path).parent.name.endswith((PROBE + ".Worker", PROBE + ".Migrator")))
```

with `from pathlib import Path, PurePosixPath` at the top of the file and
`allow_list_trees` re-exported from `new_service` beside the other names it
hands down.

- [ ] **Step 2: Run to see them fail**

```bash
cd tools/new-service && py -3.12 -m unittest
```

Expected: FAIL — `AttributeError: module 'new_service' has no attribute
'allow_list_trees'`, and, before the re-export, nothing else. Each assertion
must then be checked by mutation rather than by a green run: temporarily
narrow `licence_gate.PROJECT_SUFFIXES` to `(".props",)` and confirm the licence
test goes red, and narrow the `ModulePath` to `.*\.Ordering\.Domain\.dll$` and
confirm the coverage test does. An exit code alone makes a vacuous test.

- [ ] **Step 3: Re-export and run**

In `new_service.py`, add `allow_list_trees` to the names imported from
`scaffold.verify`, with the others.

```bash
cd tools/new-service && py -3.12 -m unittest
```

Expected: green.

- [ ] **Step 4: Commit**

```bash
git add tools/new-service/test_new_service.py tools/new-service/new_service.py
git commit -m "test(scaffold): every gate's own selector covers the worker render"
```

The body says the subject is each gate's selector rather than its verdict, and
names the mutation each assertion was checked by.

---

### Task 10: The platform up, the counts remeasured, and whole-solution verification

- [ ] **Step 1: Bring the platform up**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build --wait
docker compose -f deploy/compose/docker-compose.yml config
```

Expected: every service healthy, `shipping-worker` and `shipping-migrator`
included; `config` shows no Redis variable and no Redis dependency for
Shipping, and **no `ports:` mapping on `shipping-worker`**. Confirm the health
endpoint answers from inside the container rather than from the host, which is
the whole of what "no port is published" means here:

```bash
docker compose -f deploy/compose/docker-compose.yml exec shipping-worker \
    dotnet --info > /dev/null && echo "worker container is alive"
docker compose -f deploy/compose/docker-compose.yml logs shipping-worker | grep -i "Now listening on"
```

Expected: the log line showing Kestrel bound on `http://[::]:8080` inside the
container, and nothing published on the host — `curl` from the host is not the
check, because there is deliberately nothing to curl. The chiselled image
ships no shell and no HTTP client, which is why the Compose unit declares no
`healthcheck:` and the log line is the evidence instead.

Then the migration, read from the engine:

```bash
docker compose -f deploy/compose/docker-compose.yml exec sql sh -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT name FROM Shipping.sys.tables WHERE schema_name(schema_id) = '"'"'shipping'"'"'"'
```

`sql` is the service `deploy/compose/infrastructure.yml` declares and
`MSSQL_SA_PASSWORD` the variable it sets inside that container. Expected:
`Shipments`, `TrackingEvents`, and the three technical tables. Tear down with
`docker compose -f deploy/compose/docker-compose.yml down -v` — from the
repository root Compose has no default file, so the `-f` is what makes the
teardown reach the stack the `up` started.

- [ ] **Step 2: Remeasure §4.5's two counts**

The template gained `MetricsRegistrationTests`, so the eighty-nine and the
forty-six are stale — and §4.5 says arithmetic is not a remeasurement. Render
the probe, run all three of its suites, then undo:

```bash
py -3.12 tools/new-service/new_service.py Yankee --port 5199
dotnet restore Platform.slnx && dotnet build Platform.slnx
dotnet test tests/Yankee.Domain.Tests
dotnet test tests/Yankee.Application.Tests
dotnet test tests/Yankee.Api.Tests
dotnet test tests/Yankee.Api.Tests --filter "Category=Integration"
```

Write the three suite totals and the integration count into §4.5 and into
`tools/new-service/README.md`, replacing both figures and adding this PR to
the list of the ones that remeasured. Then undo the probe exactly as the
scaffold README's own two commands say:

```bash
rm -rf src/Services/Yankee tests/Yankee.* deploy/compose/services/yankee.yml
git checkout -- Platform.slnx deploy/compose/ .github/secret-scan/allowed/ \
    src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs
```

`ObservabilityExtensions.cs` joins that pathspec now, because the render writes
a line into it — the README's undo block is amended in the same edit, since a
list of paths that no longer covers what a run touches is the fail-open shape
that block exists to avoid. Confirm with `git status --short`: only the two
count edits and the README's pathspec line remain.

- [ ] **Step 3: Build and test everything**

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
py -3.12 -m unittest discover -s .github/comment-gate
py -3.12 .github/comment-gate/comment_gate.py
py -3.12 -m unittest discover -s .github/licence-gate
py -3.12 .github/licence-gate/licence_gate.py
```

Expected: 0 warnings; every suite green; every gate exits 0. A gate with a
suite is tested and then run, and none of these is in `Platform.slnx`, so a
green solution says nothing about them.

**This plan's own text** carries a Compose-shaped fixture password and an
`amqp://` literal inside its code blocks, which §15.1's scan reports against
`docs/superpowers/plans/`. If the scan names this file, add the entry to
`.github/secret-scan/allowed/docs.txt` with the fingerprint the scanner
computed and a reason naming what the literal is — the Payments PR-2 plan
carries exactly such an entry.

- [ ] **Step 4: Commit and open the PR**

```bash
git add docs/backend-architecture/04-solution-structure.md tools/new-service/README.md \
        .github/secret-scan/allowed/
git commit -m "docs: §4.5's counts, remeasured against a render carrying the metrics suite"
```

The PR body carries `| Class | A+D+E |` and the touch set from the Global
Constraints, the dogfood evidence (the three probe suites' counts, and the
Shipping suites' counts after Task 7), the sentence that no Helm chart is owed
until PR-7 because `smoke.sh` checks its chart list against the charts on disk
in both directions, and then `/ship`.

## Self-review

**Spec coverage.**

- Section 1, Redis: Task 4 (the render strips `AddRedisConnections`, and §2's
  sentence gains Shipping).
- Section 2, the worker mode joins `tools/new-service` and `Shipping` comes off
  the refusal: Task 2. The outbox gauges in the template and Catalog's
  `OUTBOX_METRICS_EXEMPT` entry deleted: Task 1.
- Section 3, PR-1's row: Tasks 1–10. CI joins PR-1 (Task 8); Helm does not, on
  the Inventory spec's argument, which is why no `deploy/helm/**` path is in
  the touch set.
- Section 5, the aggregate: Task 6 — `Shipment` keyed by `ShipmentId` with
  `OrderId` unique, `TrackingEvent` keyed by `(ShipmentId, CarrierEventId)`,
  `TrackingStatus` closed over `Collected`, `InTransit`, `Delivered` and
  `Unrecognised`, every row of the state table, "every other arrival is a
  no-op that logs and returns, never a throw", and the shuffled feed.
- Section 7, persistence: Task 7 — schema `shipping`, both column lists, and
  `AddShipments` with `TrackingEvents` in it.
- Section 8, the broker account `shipping-svc` in `ordering-svc`'s shape under
  a `shipping-` prefix: Task 5.
- Section 10, PR-1's keys and no published port: Tasks 2, 3, 4 and 10. The
  render carries `ConnectionStrings__Shipping`,
  `ConnectionStrings__ShippingMigrator`, `ConnectionStrings__RabbitMq`,
  `Identity__Authority` and `OTEL_EXPORTER_OTLP_ENDPOINT`, and Task 4 removes
  the two Redis keys.
- Section 12, the Domain suite and the scaffold's suite: Tasks 6 and 9.
- Section 13, §4.5's sentence and §2's: Tasks 2 and 4.

**Type consistency.** `ShipmentId`, `OrderId`, `ShipmentStatus`,
`TrackingStatus`, `ShipmentLimits`, `TrackingEvent`, `Shipment` with
`For`/`Book`/`MarkUnfulfillable`/`Cancel`/`CarrierCancelled`/
`CarrierRefusedCancellation`/`Record`, `ShipmentDispatchedDomainEvent`,
`ShipmentDeliveredDomainEvent`, `ShippingDbContext.Shipments`, the
two-argument `ShippingWorkerFactory`, `Shipping.Infrastructure.Observability.
OutboxMetrics.MeterName` = `"Shipping.Outbox"`, and the scaffold's
`API_HOST`/`WORKER_HOST`/`HOSTS`/`project_suffixes` are the names every later
Shipping plan consumes. The state machine returns `bool` throughout, which is
what PR-5's consumers and PR-6's tracking worker branch on to decide whether
to log a superseded arrival.

**Deliberately left to a later PR.** The carrier port, its adapter,
`CarrierHop` and the simulator (PR-2). The identity move (PR-3) and Ordering's
`DeliveryAddresses.Get` (PR-4). `shipping-events`, the two consumers,
`IShipmentRepository` and the fulfilment worker (PR-5) — PR-1 maps the
aggregate so `migrations add` emits its tables and nothing more, on the
argument Payments' `PaymentOrderRow` makes one level up. The tracking worker,
the lease and backoff operations over the columns this PR creates, the two
integration events through the outbox, `ShippingJurisdictionOptions` and the
retention pass (PR-6). The chart, the canary row and §13.6's two rules
(PR-7).

**Beyond the spec's minimum, with the reason.** `ShipmentsSchemaTests` is not
named in section 12's PR-1 list. It is here because the aggregate lands with
nothing driving it, and a migration nobody has inserted through is a migration
nobody has checked — the same argument §4.5 makes for rendering `Yankee`
rather than reasoning about the render. `MetricsRegistrationTests` joins the
template for the mirror reason: the gauges are registered and forced by a
hosted service, and no gate in the repository can see the forcing.

# 4. Solution and folder structure

## 4.1 Repository layout

A single repository. Independent deployability comes from the CI pipeline
building and releasing services separately, not from separate git repositories.
A monorepo makes cross-cutting changes and contract updates atomic and reviewable.

```
/
├── src/
│   ├── BuildingBlocks/
│   │   ├── Common.Domain/              Entity, AggregateRoot, IDomainEvent (§5.5)
│   │   ├── Common.Application/         Dispatcher, pipeline behaviours, Result<T>
│   │   ├── Common.Infrastructure/      Outbox, inbox, idempotency markers,
│   │   │                               EF conventions, Redis
│   │   ├── Common.Web/                 Host defaults: OTel, health, auth, ProblemDetails.
│   │   │                               Referenced by every host. NOT resilience —
│   │   │                               the one outbound client is the BFF's (§9.7),
│   │   │                               so the policy lives with it.
│   │   │                               (Aspire's template calls this ServiceDefaults.)
│   │   └── Common.Contracts/           Integration event DTOs — the ONLY shared types
│   │
│   ├── Gateway/
│   │   └── Gateway.Api/                YARP host
│   │
│   ├── BFF/
│   │   └── Web.Bff/                    Aggregation for the web client (§10.1).
│   │                                   The ONLY host that calls a service
│   │                                   synchronously (§9.7), and therefore the
│   │                                   only one with client credentials (§11.5)
│   │
│   └── Services/
│       ├── Catalog/
│       │   ├── Catalog.Domain/
│       │   ├── Catalog.Application/
│       │   ├── Catalog.Infrastructure/
│       │   ├── Catalog.Migrator/
│       │   └── Catalog.Api/
│       ├── Ordering/
│       │   ├── Ordering.Domain/
│       │   ├── Ordering.Application/
│       │   │   └── DependencyInjection.cs   AddOrderingApplication()
│       │   ├── Ordering.Infrastructure/
│       │   │   └── DependencyInjection.cs   AddOrderingInfrastructure(config)
│       │   ├── Ordering.Migrator/           Migration job host (§7.4)
│       │   └── Ordering.Api/
│       │       └── Program.cs               The ONLY composition root (§4.2)
│       ├── Inventory/                  (same five projects)
│       ├── Payments/                   (same five projects)
│       ├── Shipping/                   (Domain, Application, Infrastructure, Migrator, Worker)
│       └── Notifications/              (Application, Infrastructure, Migrator, Worker)
│
├── tests/
│   ├── Common.Domain.Tests/            The building blocks, under the same
│   ├── Common.Application.Tests/       *.Domain.Tests / *.Application.Tests
│   ├── Common.Infrastructure.Tests/    convention the services use (§12.1).
│   ├── Common.Web.Tests/               Common.Infrastructure's suite needs
│   │                                   Docker — its Redis half runs against
│   │                                   a Testcontainers server (§8, §12.4).
│   │                                   Common.Web is a library with no entry
│   │                                   point, so its suite drives a TestServer
│   │                                   rather than a WebApplicationFactory
│   ├── Gateway.Api.Tests/              The route file of §10.2, over the real
│   │                                   host: policy resolution, prefix strips,
│   │                                   the limiter of §10.3 driven until it
│   │                                   rejects — and §10.1's two edge
│   │                                   behaviours, compression and the body
│   │                                   ceiling, the second over a real Kestrel
│   │                                   because TestServer serves no such
│   │                                   property. No TestSupport beside it —
│   │                                   that library exists where two suites
│   │                                   share a fixture, and the gateway has one
│   ├── Web.Bff.Tests/                  §9.7's hop and §11.5's credentials: the
│   │                                   resilience hierarchy read off the built
│   │                                   host, the quote endpoint over a real
│   │                                   gRPC server on loopback, and the ONE
│   │                                   suite in the solution that runs a real
│   │                                   Keycloak — the audience mapper it proves
│   │                                   is realm configuration, so nothing
│   │                                   compiles differently when it is missing
│   ├── Web.Bff.TestSupport/            The stub Catalog, the SERVER half of
│   │                                   pricing.proto with it, and PR-26's
│   │                                   PricingContract — the BFF's expectations
│   │                                   of §9.7's hop (§12.6), authored here
│   │                                   because only a consumer can write one,
│   │                                   and LINKED into Catalog.Api.Tests rather
│   │                                   than referenced, exactly as the .proto
│   │                                   is linked into Web.Bff. Not a test
│   │                                   project — and NOT here for §4.1's usual
│   │                                   reason: there is one BFF suite. Web.Bff
│   │                                   compiles the client half of the same
│   │                                   file, so generating the server half into
│   │                                   a suite that references it would put
│   │                                   every message type in a compilation
│   │                                   twice, and CS0436 is an error under
│   │                                   ADR-019
│   ├── Catalog.Domain.Tests/
│   ├── Catalog.Application.Tests/
│   ├── Catalog.Api.Tests/
│   ├── Catalog.TestSupport/            ServiceFixture, the test auth scheme and the
│   │                                   data builders (§12.4). Not a test project —
│   │                                   referenced by the two above, which each need
│   │                                   containers and cannot reference each other.
│   ├── ...
│   └── Platform.IntegrationTests/      Contract-assembly tests (§12.6) — the
│                                       only suite that references every service
│
├── deploy/
│   ├── compose/                        the index, one file per unit, overrides
│   ├── helm/                           Chart per service + umbrella chart
│   ├── observability/                  §13.8's dashboards, §13.6's alert rules
│   │                                   and §13.7's k6 SLO run, as code — plus
│   │                                   check.py, which pairs the alerts with
│   │                                   §13.9's runbooks both ways
│   ├── canary/                         §15.5's rollout as data and one decision
│   │                                   function — the ladder, the thresholds and
│   │                                   the PromQL as JSON, the weight arithmetic
│   │                                   and the promote/rollback verdict as tested
│   │                                   Python. It reaches no cluster (ADR-022)
│   └── k8s/                            Raw manifests where Helm is overkill
│
├── docs/
│   ├── backend-architecture/           This document, one file per chapter;
│   │                                   one per ADR under adr/, indexed by
│   │                                   Appendix A
│   ├── runbooks/                       One per alert (§13.9), plus a README
│   │                                   that is excluded from the pairing by
│   │                                   name
│   ├── secrets.md                      How a secret reaches a pod and how each
│   │                                   kind is rotated — the operational half
│   │                                   of §15.4, which keeps the inventory
│   └── testing.md                      How to run the suites, what needs
│                                       Docker, and the categories of §12.4 —
│                                       the operational half of §12, which
│                                       keeps the strategy
│
├── tools/
│   └── new-service/                    The scaffold of §4.5 and its tests.
│                                       Stdlib Python, no restore — it renders
│                                       a service from Catalog at run time
│
├── artifacts/                          Every build's output: bin, obj and
│                                       publish take a subdirectory per project,
│                                       package groups by configuration.
│                                       Generated and git-ignored, and drawn
│                                       here because it is the reason src/ and
│                                       tests/ above hold source alone
│
├── coverage.runsettings                What `--collect:"Code Coverage"` measures:
│                                       the report filtered to `.*\.Domain\.dll$`,
│                                       which is §12.9's "coverage of the domain
│                                       layer specifically" as an artefact
├── Directory.Build.props               Shared MSBuild settings, and the file
│                                       that puts output in artifacts/ — the
│                                       only early-enough place for it, argued
│                                       in the file's own Output comment
├── Directory.Packages.props            Central package version management
└── Platform.slnx
```

**`src/` and `tests/` hold source, and nothing a build wrote.** A project
directory carrying its own `bin/` and `obj/` buries the files a reader came for
under the ones a build wrote, and every gate that walks a source tree pays for
it again in a skip list that silently decides what the gate reads.

Moving the output retires none of those lists, and expecting it to is the way
to get this wrong: a gate that walks the repository root has to decline
`artifacts/` by name, and one that walks `src/` still meets a checkout made
before the move. What it does buy is that the tree a reader browses and the
tree a gate reads are the same tree, and that a directory left out of a skip
list is a gate reading too much rather than a gate reading a build.

**That `src/` and `tests/` hold nothing a build wrote is checked rather than
asserted**, by [`.github/output-gate/`](../../.github/output-gate/README.md),
whose README owns what it reads and which half of this sentence it leaves to
a CI step.

`.slnx` is the XML solution format, supported by the SDK from .NET 9 and by
Visual Studio 2022 17.13 onward. The `global.json` pin below already puts every
machine above that floor, which is the only reason a one-line note suffices
rather than shipping a `.sln` alongside it.

**Every service has a `*.Migrator`, because every service owns a database**
([§7.1](07-persistence.md)) and ADR-007 forbids migrating at application startup. That includes
Shipping and Notifications, which expose no public API but still own schemas —
`Shipment`/`TrackingEvent` and `NotificationLog` respectively ([§3.2](03-bounded-contexts.md)).

It is also why [§15.2](15-cicd-deployment.md) builds **two images per service** rather than one, and why
the migrator gets its own connection string, its own SQL login and its own
Kubernetes Job. A service without a migrator has no way to create its schema
that this architecture permits.

## 4.2 The dependency rule

Inside a service, dependencies point inward only:

```
Api ──────────► Application ──────────► Domain
 │                                        ▲
 └──► Infrastructure ─────────────────────┘
```

| Project | May reference | Must never reference |
|---|---|---|
| `*.Domain` | `Common.Domain` and nothing else | EF Core, ASP.NET, Redis, MassTransit, `System.Text.Json` |
| `*.Application` | its own Domain, `Common.Application`, `Common.Contracts`; `Dapper`, for §6.5's query handlers alone ([ADR-005](adr/ADR-005-ef-core-for-writes-dapper-for-reads.md)) | EF Core, ASP.NET, any other concrete infrastructure |
| `*.Infrastructure` | Domain, Application, any package | another service's projects |
| `*.Migrator` | Infrastructure, for the `DbContext` it migrates | another service's projects; anything it does not need to apply a migration |
| `*.Api` | Application, Infrastructure (**composition root only**) | another service's projects |

**The migrator's row is the narrowest, and deliberately.** It is not a second
composition root: it builds a host, resolves the `DbContext` and calls
`Database.Migrate()` ([§7.4](07-persistence.md)), and it needs no Application,
no dispatcher and no Redis to do that. The temptation is to reach for
`AddXInfrastructure(config)` and get the context for free — which also gets
the readiness checks, the bus registration and the runtime connection string,
in the one process holding the DDL identity of [§7.1](07-persistence.md). A
migration job that can open a message broker is a migration job with reasons
to fail that have nothing to do with migrations.

`*.Domain` having no third-party dependencies is what makes domain tests
instant and mock-free. It is worth defending. Enforce it with an architecture
test rather than a code review convention:

```csharp
[Fact]
public void Domain_references_only_common_domain_and_the_framework()
{
    // The table's rule is an allow-list — "Common.Domain and nothing else" —
    // so the gate is one too, and an exact one: a blacklist only bans what
    // someone thought to name, and a System.* prefix still passes
    // System.Data.SqlClient or a serialiser. Each BCL assembly Domain starts
    // using earns its line here on purpose — extending this list is the
    // decision the gate exists to force, and System.Text.Json is the
    // extension the table forbids by name.
    //
    // Two entries, because two is what an EMPTY domain references — this is
    // the form §4.5's scaffold renders. A live one grows: Catalog's and
    // Ordering's both carry System.Collections, earned by the first domain
    // event, whose generated record equality goes through
    // EqualityComparer<T>, and System.Linq, earned by the first value object
    // doing enumerable logic over owned values. Run as printed against either
    // of them, this list fails — which is the gate working, not the sample
    // being wrong.
    string[] allowed = ["Common.Domain", "System.Runtime"];

    IEnumerable<string> referenced = typeof(Order).Assembly
        .GetReferencedAssemblies()
        .Select(a => a.Name!);

    referenced.ShouldAllBe(name => allowed.Contains(name));
}
```

### The composition-root rule

`*.Api` may reference Infrastructure, but only in one place. Stated normatively:

- **Only** `Program.cs` may reference `*.Infrastructure` types.
- **Endpoints and controllers may not.** No `DbContext`, no concrete
  repository, no `IPublishEndpoint`, no `IConnectionMultiplexer` — Application
  and Domain contracts only.

> **The first bullet used to grant host-level `*ServiceCollectionExtensions`
> the same exemption, and the gate never implemented it.** No file in any of
> the four hosts is named `*ServiceCollectionExtensions` — `Catalog.Api`,
> `Ordering.Api`, `Gateway.Api` and `Web.Bff` alike — so the sentence granted
> a hole to nothing, and the test below exempts the composition root alone.
>
> Narrowing the prose to the code is the right direction rather than the
> convenient one. The exemption is the whole of this gate's trust, and the
> companion test that asserts it has not grown is only meaningful while the
> exempted set is something a reader can hold in mind. A host that genuinely
> wants a registration extension is welcome to one; what it does not get is a
> pre-granted licence written before it existed.

Without this rule the dependency table is satisfied at project level while being
violated everywhere that matters, because "Api may reference Infrastructure"
silently licenses an endpoint to inject a `DbContext`.

```csharp
[Fact]
public void Nothing_but_the_composition_root_depends_on_infrastructure()
{
    // No selector at all. The rule above is "only Program.cs may reference
    // Infrastructure", so the assembly is judged whole and the root is
    // subtracted from the FAILURES afterwards — where full names are available
    // to subtract it by, and where there is no candidate set to be narrow.
    TestResult result = Types
        .InAssembly(typeof(Program).Assembly)
        .ShouldNot().HaveDependencyOnAny(
            "Ordering.Infrastructure",
            "Microsoft.EntityFrameworkCore",
            "MassTransit",
            "StackExchange.Redis")
        .GetResult();

    string[] leaked = [.. (result.FailingTypeNames ?? []).Where(name => !IsCompositionRoot(name))];

    leaked.ShouldBeEmpty($"leaked: {string.Join(", ", leaked)}");
}

// Top-level statements put Program and its helpers in the GLOBAL namespace, so
// they carry no dot; anything an endpoint generates is nested inside the
// endpoint class and keeps its namespace.
private static bool IsCompositionRoot(string fullName) =>
    fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));
```

> **This gate was wrong four times, always by selecting less than it claimed,
> and the sequence is worth keeping because each fix looked complete.**
> `.ResideInNamespaceContaining(".Endpoints")` stopped covering the transport
> surface the moment a gRPC service arrived in `.Grpc`. A namespace *pattern*
> moved the hole one namespace further out. Excluding compiler-generated types
> by name exempted endpoint lambdas, because a closure is generated code. And
> filtering candidates through `HaveName(...)` exempted them again, because
> that predicate selects nothing for a nested async state machine — and an
> empty selection reports **success**.
>
> A companion test naming the known adapters closes none of it, and believing
> otherwise is the subtler error: the set such a test inspects is unchanged by
> a type the selector never picked up, so it passes exactly as before. It
> guards against *narrowing* the rule, never against *outgrowing* it.

> **One gap survives all four, and it belongs to NetArchTest rather than to the
> rule.** The library does not analyse compiler-generated nested types, so a
> forbidden reference used *only* inside an endpoint lambda is invisible to it.
> Measured rather than inferred: a `DbContextOptionsBuilder` in an endpoint
> method's own body fails the gate and names the endpoint class; the identical
> line inside that method's lambda leaves it green — with no selector at all,
> which is what rules out a narrowing predicate as the cause.
>
> State it rather than close it. A reference written in a method body is
> caught, which is where references are written; **a gate believed to be total
> is worse than one whose gap is written down**, because the first invites
> nobody to look.

One more, for the rule [§9.3](09-messaging.md) states in prose: application code publishes through
`IIntegrationEventPublisher` and the outbox, never through the bus directly.
The saga is the documented exception (§9.6) and it lives in Infrastructure, so
the boundary is checkable:

```csharp
[Fact]
public void Application_and_domain_do_not_reference_masstransit()
{
    // §9.3's must-not list. The saga may Send and Publish because its receive
    // endpoint carries MassTransit's transactional outbox (ADR-032), which
    // writes those sends to the same DbContext and the same transaction as the
    // saga instance — a guarantee that exists on the consume pipeline and
    // nowhere else. A handler that copies the saga's style gets a dual write
    // with no outbox behind it, and it works in every test where the broker is
    // up.
    Assembly[] assemblies = [typeof(PlaceOrderHandler).Assembly, typeof(Order).Assembly];
    foreach (Assembly assembly in assemblies)
    {
        Types
            .InAssembly(assembly)
            .ShouldNot().HaveDependencyOn("MassTransit")
            .GetResult().IsSuccessful.ShouldBeTrue(assembly.GetName().Name);
    }
}
```

If the namespace rule proves awkward to enforce, split the host into
`Ordering.Host` (composition, references Infrastructure) and `Ordering.Api`
(endpoints, does not) and let the project reference enforce it. That is the more
robust option; the single-project namespace rule is the lighter one.

**"Namespace rule" here is the rule, not the selector the callouts above
abandoned**, and the two are easy to read as one thing this close together. What
the gate stopped doing is *selecting* candidates by namespace; what it still
does is tell the composition root from everything else by exactly that —
`IsCompositionRoot` tests `!fullName.Contains('.')`, because top-level
statements put `Program` in the global namespace while an endpoint keeps
`Catalog.Api.Endpoints`. The alternative above replaces that discrimination
with a project boundary, which is why it is the heavier option and the one a
compiler enforces.

**These tests are a CI gate from the first template commit**, not a later
addition. An architecture rule introduced after the violations exist is a
backlog item; one introduced before them is a constraint.

### The rest of the table, enforced

**What the three gates above leave uncovered is a clause rather than a row**,
and saying "three rows had no test" would erase work that exists. Between them
they cover the Domain row whole, the Application row's named must-nots, and the
Api row's composition-root half. What none of them says is the clause
**Infrastructure, Migrator and Api** all carry — *must never reference another
service's projects* — and the Migrator row is the one with no gate of any kind
until PR-22.

**The table has two kinds of row, so the gates have two shapes.** A row that
says what a project *may* reference is an allow-list, and gets an allow-list
gate over `GetReferencedAssemblies`. A row that says a project may reference
any package cannot have one, and gets a named deny instead. Choosing by the
row rather than by taste is what keeps a gate from contradicting the sentence
it enforces:

| Row | Shape | Gate |
|---|---|---|
| `*.Domain` | allow-list | every referenced assembly is on a list |
| `*.Application` | allow-list | the same, one layer out |
| `*.Infrastructure` | any package | no assembly from another service, and none from the migrator |
| `*.Migrator` | allow-list, narrowest | every referenced assembly is on a list |
| `*.Api` | composition root, plus any package | the root rule above, no assembly from another service, and none from the migrator |

**The cross-service rule is one test over all five assemblies**, and it is
stated as an allow-list of *prefixes* rather than a deny-list of service
names — which is what makes it cover Inventory, Payments, Shipping and
Notifications before any of them exists:

```csharp
// Every referenced assembly that is one of this repository's own must belong
// to this service or to a building block. Common.Contracts is admitted by the
// Common prefix, which is §4.3 from the other side: it is a building block
// rather than a service, so the one assembly permitted to cross a boundary
// needs no exception of its own.
string self = typeof(Program).Assembly.GetName().Name!.Split('.')[0];

string[] foreign =
[
    .. assembly
        .GetReferencedAssemblies()
        .Where(IsFirstParty)
        .Select(reference => reference.Name!)
        .Where(name =>
            !name.StartsWith("Common.", StringComparison.Ordinal) &&
            !name.StartsWith($"{self}.", StringComparison.Ordinal))
        .Order()
];
```

> **`IsFirstParty` is a measured property rather than a list, and the reason
> is the scaffold.** §4.5's script renders this file:
> it applies its patches and *then* renames every casing of the template's
> name, so a list naming `Catalog` reaches the new service with `Catalog`
> replaced rather than joined — silently dropping the one service a scaffolded
> service is most likely to reference by accident. No spelling of the patch
> survives that, because the rename is what the patch output is fed through.
>
> So the predicate asks a question the rename cannot answer wrongly: **every
> package this platform pins is strong-named, and none of this repository's own
> projects is.** Checked across all ten service assemblies, which between them
> reference thirty-odd packages. `Dapper` is the one unsigned package in the
> graph and is named for that reason alone.
>
> ```csharp
> private static bool IsFirstParty(AssemblyName reference) =>
>     reference.GetPublicKeyToken() is null or [] && reference.Name != "Dapper";
> ```
>
> A second unsigned package would be misread as first-party and fail the gate.
> **That is the direction this has to fail in**: the failure names an assembly
> nobody expected and is closed by a line with an argument beside it, where a
> predicate erring the other way would have opened a hole and said nothing.

**The migrator's row is the one that most wanted a gate**, because its "must
never" is a sentence rather than a list — *anything it does not need to apply a
migration*. A deny-list cannot enforce that; it can only ban what somebody
thought of. So the narrowest row gets the strictest instrument, and what the
absences buy is worth reading as a list of things that cannot happen: no
Application, so the migrator cannot dispatch; no MassTransit and no Redis, so
the paragraph above about a migration job that can open a message broker is a
build failure; no ASP.NET, because it is a job host
([§7.4](07-persistence.md)) and not a second composition root; and **no
`Common.*` at all**, which is the strongest statement of the row — the migrator
resolves a `DbContext` and calls `Database.Migrate()`, and none of the building
blocks is on that path.

**Nothing references the migrator, and saying so took a third gate rather than
a wider prefix.** No row in the table names the `*.Migrator` as something a
project *may* reference: it is a leaf, a job host that resolves a `DbContext`
and calls `Database.Migrate()` ([§7.4](07-persistence.md)), so it references
and is not referenced. The cross-service gate cannot see that edge, because it
subtracts every assembly under this service's own name — which is precisely
what makes an `Api → Migrator` reference invisible to it. That edge is inside
one service and still forbidden, so it gets a rule of its own over the other
four assemblies. **The two gates ask different questions** — *whose is it* and
*which layer is it* — and one predicate answering both would answer neither
legibly. The migrator is skipped as a subject rather than exempted in the
predicate: an assembly does not reference itself, so including it would pass
vacuously and read as coverage.

**`*.Application`'s gate lists `Common.Domain` and the table above does not,
and both are right.** The table is about project references, where the line is
genuinely absent: a service's Application references its own Domain, and that
Domain cannot exist without `Common.Domain` (row one), so it arrives carrying
it. The gate is about *assembly* references, where §9.3's mapper naming
`IDomainEvent` puts `Common.Domain` in the list whether or not any csproj says
so. Adding the project reference to close the gap was considered and rejected:
it would make the second row disagree with the first about what "its own
Domain" includes, to no benefit a compiler can see.

**Each list is a subset check rather than an equality.** An entry for something
no longer referenced is a pre-authorised hole rather than a failure, which is a
real cost and the smaller one — the alternative fails a build for a legitimate
*removal*, and the decision worth forcing is the one that adds a dependency.

> **Every gate above reads *emitted* references, which is narrower than the
> word this table uses.** `GetReferencedAssemblies` reads an assembly's
> `AssemblyRef` table, and the compiler writes an entry only for an assembly
> whose types the compiled code actually names. A forbidden reference that
> nothing *uses* emits nothing — so a project may declare one and every gate
> here stays green. Each fires the moment code names a type across that edge,
> which makes these gates **late rather than absent**: the escape needs the
> reference to be both forbidden and entirely unused, and it stops being an
> escape the first time anybody relies on it.
>
> **Closing it means reading the declared graph rather than the compiled one**
> — the restore assets, or a reference list MSBuild emits into an assembly
> attribute — and that is a repo-wide build change carrying the failure this
> repository repeats most: a target that quietly stops emitting leaves every
> gate passing vacuously, so it would owe a companion test whose subject is
> what the gate is looking at. It is **owed rather than done**, and stated here
> rather than only in a test comment because it is a property of this table's
> enforcement and not of one service's file.

### What the composition root composes

Each layer exposes exactly one registration method. `Program.cs` calls them and
does nothing else with Infrastructure — which is what makes the rule above
enforceable rather than aspirational, and what lets tests exercise the real
registration path ([§6.2](06-cqrs.md)) instead of a hand-built container.

```csharp
// Ordering.Application/DependencyInjection.cs
public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
{
    services.AddPluggableFrom(typeof(PlaceOrderCommand).Assembly);       // §6.2

    // The clock. TimeProvider is an abstract BCL class, not an ambient
    // service — ASP.NET Core does not register it, so without this line every
    // handler that takes one fails to resolve. §5.4's "pass time in as a
    // parameter" discipline runs through here, and FakeTimeProvider replaces
    // exactly this registration in tests (§12.7).
    services.AddSingleton(TimeProvider.System);

    // Application-layer metrics: OrderMetrics records domain quantities, so it
    // lives and registers here rather than in Infrastructure (§13.3).
    // RequestMetrics likewise — LoggingBehavior injects it, and the pipeline
    // is Application.
    //
    // Registration is not construction, and neither of these is reachable
    // without traffic: OrderMetrics waits for the projection to run (§6.6),
    // RequestMetrics for a dispatched request — which a health probe is not.
    // MetricsInitialiser (§13.6) forces both, and a test there asserts that
    // every registered *Metrics type is on its parameter list.
    //
    // ONLY THE SECOND LINE IS BUILT TODAY, and the first is here on
    // IdempotencyBehavior's terms below — the specified shape, not the shipped
    // one. OrderMetrics arrives with §6.6's OrderSummaries projection, its only
    // call site. Copying this block as it stands FAILS THE BUILD, and by
    // design: the convention test §13.6 describes asserts every registered
    // *Metrics type is a MetricsInitialiser parameter, so registering a type
    // the initialiser does not take is exactly what it exists to catch.
    services.AddSingleton<OrderMetrics>();                               // with §6.6
    services.AddSingleton<RequestMetrics>();                             // §13.3

    // Not AddScoped<IDispatcher, Dispatcher>: Dispatcher is internal to
    // Common.Application (§6.2), so this assembly cannot name it.
    services.AddDispatcher();

    // The same reason, one section over: DomainEventDispatcher,
    // ProjectionRegistry and its cache are all internal to Common.Application
    // (§7.5), so the three registration lines they need are not lines this
    // assembly can write either. Not three AddScoped lines — the cache is a
    // singleton, for the reason §7.5 gives beside it.
    services.AddDomainEventDispatcher();                                // §7.5

    // Not an open generic, so the §6.2 scan cannot find it — one service, one
    // mapper, registered by hand. DomainEventDispatcher injects it, so a
    // missing line here fails ValidateOnBuild rather than failing silently.
    services.AddScoped<IIntegrationEventMapper, OrderingIntegrationEventMapper>();

    // Ordered, explicit, not scanned — registration order is pipeline order
    // (§6.3). Unregistered, nothing opens a transaction and no write persists.
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
    services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

    // The key one of those two builds and the other writes (§8.5, ADR-037).
    // Scoped, because a command is. Here rather than inside AddDispatcher,
    // because these lines are where a reader finds out what the pipeline is
    // made of — and because omitting it does not fail at startup:
    // ValidateOnBuild never constructs an open generic, so the miss surfaces
    // as an unresolvable TransactionBehavior on the first dispatched command.
    services.AddScoped<IdempotencyContext>();

    services.AddValidatorsFromAssemblyContaining<PlaceOrderValidator>();
    return services;
}
```

```csharp
// Ordering.Infrastructure/DependencyInjection.cs
public static IServiceCollection AddOrderingInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.AddDbContext<OrderingDbContext>(o =>
        o.UseSqlServer(
            configuration.GetConnectionString("Ordering"),   // runtime identity, §7.1
            sql => sql.EnableRetryOnFailure()));

    // Projections, cache invalidators and command mappers live here, not in
    // Application — scanning only Application would skip them all (§6.2).
    services.AddPluggableFrom(typeof(OrderRepository).Assembly);

    // §9.5's inbox filter is common code, so it names DbContext rather than
    // this service's derived type — and this alias is what makes that legal.
    // GetRequiredService, not AddScoped<DbContext, OrderingDbContext>(): the
    // second form compiles, resolves and builds a SECOND context in the same
    // scope, so the inbox row commits in its own transaction and §9.5's
    // atomic-with-the-handler row silently becomes its non-atomic one.
    services.AddScoped<DbContext>(sp => sp.GetRequiredService<OrderingDbContext>());

    services.AddScoped<IUnitOfWork, EfUnitOfWork>();                    // §6.3
    services.AddScoped<IDomainEventCollector, EfDomainEventCollector>(); // §7.5
    services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();   // §9.3
    services.AddScoped<IOrderRepository, OrderRepository>();

    // §6.5's read side. Singleton, holding the runtime connection string and
    // constructing per call — the connections it hands out are the caller's
    // to dispose, so there is no scoped state to capture. An instance, not a
    // type: SqlConnectionFactory takes the string, so a type registration has
    // no constructor the container can satisfy, and the ValidateOnBuild in
    // Program.cs below refuses the host before it serves a request.
    services.AddSingleton<IDbConnectionFactory>(
        new SqlConnectionFactory(configuration.GetConnectionString("Ordering")!));

    // The outbox's persisted type names (§9.4). Singleton and built here, so a
    // duplicate name fails this host at startup rather than one message at
    // delivery. Two assemblies: contracts for the Broker lane, this service's
    // domain events for the Local one.
    //
    // Registered as a source rather than a finished map, because a test host
    // has to add its own event types (§12.4) and a map built by `new` in this
    // line leaves no way to. Adding to the source is not the same as replacing
    // the map: the production assemblies stay in the list, so a test still
    // cannot stage something the real host would reject.
    //
    // An instance, not a factory, and that is what makes the sentence above
    // true — a test resolves the registered descriptor and calls Add on it.
    // A factory would leave a test with nothing to reach, and re-registering
    // a second source is the replacement this is written to avoid.
    services.AddSingleton(
        new MessageTypeSource(typeof(V1.OrderPlaced).Assembly, typeof(Order).Assembly));

    // All three of the source, not just its assemblies. §9.4's rename
    // procedure records an Alias and a WriteAs on this object, and a factory
    // that reads only Assemblies drops both — the host starts clean and the
    // rename abandons rows on the release the procedure exists to make safe.
    services.AddSingleton(sp =>
    {
        MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
        return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
    });

    // The map's factory is lazy, and this is what makes "a duplicate name
    // fails the host" true: ValidateOnBuild checks the call site and never
    // invokes it, so without a hosted service resolving the map the
    // constructor's throw lands on a background thread in a host that has
    // been ready for hours. Registered before the dispatcher, because hosted
    // services start in order.
    services.AddHostedService<MessageTypeMapValidator>();                 // §9.4

    // The schemas the dispatcher and the retention purge compose their
    // statements against (§9.4, §9.5, §8.5). Values, because
    // Common.Infrastructure is every service's and cannot hold a literal — and
    // every one of them from ONE local, so no two of these tables can end up
    // naming different schemas. No count in this comment: it said "both" and
    // "the two tables" while there were two, and §8.5's marker made it three.
    const string schema = "ordering";
    services.AddSingleton(new OutboxTable(schema));
    services.AddSingleton(new InboxTable(schema));
    services.AddSingleton(new IdempotencyMarkerTable(schema));

    // The retention windows of §9.4, §9.5 and §8.5, the batch size and the
    // per-pass ceiling.
    // Registered rather than const: §9.5 tells the reader to check the inbox
    // window against their broker's redelivery limits, and a number a chapter
    // says to check has to be one a service can change.
    services.AddSingleton(new RetentionPolicy());

    // The payload format, and the converters that put this service's value
    // objects in it. Money has a private constructor, so without its converter
    // it deserialises to a zero amount and a null currency and nothing says so
    // (§9.4).
    services.AddSingleton<JsonConverter, MoneyJsonConverter>();
    services.AddSingleton<OutboxJson>();

    // Plain ports — not open generics, so the §6.2 scan does not see them and
    // each needs a line here. Omitting one fails at DI resolution on the first
    // request that needs it, not at startup — unless ValidateOnBuild is on.
    services.AddScoped<IProductPriceReader, ProjectedPriceReader>();      // §6.4

    // §8.5's durable half, beside the unit of work rather than with its Redis
    // sibling below: the two ports are backed by different systems, and only
    // this one has to land on the transaction EfUnitOfWork opens. It resolves
    // the DbContext alias registered above — which is what puts the marker in
    // that transaction (ADR-037).
    services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

    // No ICurrentUser and no AddHttpContextAccessor. Both were here until
    // PR-16 and both moved to AddCommonWebDefaults (§11.4, §13.2): neither type
    // names a service, and the implementation reads IHttpContextAccessor, which
    // arrives with a FrameworkReference that only Common.Web has. Every host
    // that authenticates has a current user, which is the criterion that helper
    // exists for.

    // No IIdempotencyStore line, and its absence is a registration rather than
    // an omission: RedisIdempotencyStore is Common.Infrastructure's (§8.5) and
    // arrives with AddRedisConnections below, on RedisDistributedLockFactory's
    // terms. That method is one call by design (§8.2) — a service either has
    // Redis or does not — so a second registration here would be a second
    // place for the lifetime and the keyed connection to disagree.

    // No ITokenCache, no ClientCredentialsHandler and no ServiceIdentityOptions
    // here. Ordering makes no synchronous outbound call — the price it needs
    // comes from a local projection (§6.4) and everything else it says goes
    // over the broker. The one host in this blueprint that calls a peer is the
    // BFF (§9.7), and outbound identity belongs to it (§11.5).

    // Registered by type, not by factory: the generic overload records an
    // ImplementationType, and the integration-test fixture matches on it to
    // locate and remove this exact descriptor (§12.4). MassTransit registers
    // its bus as a hosted service too, so the fixture cannot simply call
    // RemoveAll<IHostedService>() — and a factory registration here would
    // leave ImplementationType null, so the removal it does make would match
    // nothing and the dispatcher would drain rows underneath the assertions
    // about them.
    services.AddHostedService<OutboxDispatcher>();

    // §9.4's, §9.5's and §8.5's retention, in the one hosted service §9.5
    // asks for.
    // Registered last, so it is the first stopped: hosted services stop in
    // reverse, and a deploy that interrupts a purge loses nothing an hour will
    // not redo — where the dispatcher stopping first is what keeps the
    // transport up while it drains.
    services.AddHostedService<RetentionPurgeService>();

    // Outbox metrics (§13.6) read the database, so they belong here.
    // OrderMetrics does not — it is an Application type (§13.3) and is
    // registered by AddOrderingApplication above. OutboxStats gets its OWN
    // connection factory, bounded by OutboxStats.ConnectTimeoutSeconds: the
    // shared IDbConnectionFactory would leave SqlClient's default on the open,
    // and §13.6 argues why its two bounds only work together.
    string metricsConnectionString =
        new SqlConnectionStringBuilder(configuration.GetConnectionString("Ordering"))
        {
            ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
        }.ConnectionString;

    services.AddSingleton<IOutboxStats>(sp => new OutboxStats(
        new SqlConnectionFactory(metricsConnectionString),
        sp.GetRequiredService<OutboxTable>()));
    services.AddSingleton<OutboxMetrics>();

    // The two delivery lags, the rejection counter and the inbox suppression
    // counter (§13.3). Injected by IntegrationEventConsumer<T>,
    // CommandConsumer<,> and InboxFilter<T>, resolved from the provider by
    // ProjectionInvoker — all four live here. Forced at startup for the same
    // reason as OrderMetrics: a consumer is constructed when a message
    // arrives, so on a quiet service these instruments do not exist.
    services.AddSingleton<MessagingMetrics>();

    // Forces construction of the metrics singletons in both layers. Resolving
    // them is the whole job: nothing else injects OutboxMetrics, and an
    // observable gauge that is never constructed never reports.
    services.AddHostedService<MetricsInitialiser>();

    // Keyed connections (§8.1), the HybridCache over the cache one (§8.2), the
    // lock factory and §8.5's idempotency store — all four, because this is
    // one call by design. No service name passed: the key prefix comes from
    // ApplicationName, the single source §8.5 already uses for idempotency
    // keys.
    //
    // Both connection strings are read EAGERLY here and throw naming the
    // missing one, so this line is what a half-configured deployment stops.
    services.AddRedisConnections(configuration);
    services.AddMassTransitMessaging(configuration);                     // §9

    // Readiness checks live here, not in Common.Web — they need connection
    // strings, which the shared host package does not have (§13.5).
    services
        .AddHealthChecks()
        .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
        .AddRedis(configuration.GetConnectionString("RedisCache")!, name: "redis-cache", tags: ["ready"])
        // No RabbitMQ line, deliberately: AddMassTransit above registers the
        // bus health check itself — "masstransit-bus", tagged ready (§13.5).
        // No outbox line either: the backlog is §13.6's gauges and alerts, and
        // §13.5 says why it must not be a check.
        .AddRedis(configuration.GetConnectionString("RedisCoordination")!, name: "redis-coordination", tags: ["ready"]);

    return services;
}
```

```csharp
// Ordering.Api/Program.cs — the only file that may reference Infrastructure.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start if any registered service has a dependency the container
// cannot satisfy, or if a singleton captures a scoped one. Both are otherwise
// discovered on the first request that happens to need them.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                                     // §13.2
builder.Services.AddOrderingApplication();                          // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);  // above

// Ordering's permission policies (§11.4). Deliberately not inside either
// helper: Application knows nothing about HTTP, and Common.Web must not know
// Ordering's names. A policy named by an endpoint and registered nowhere
// throws on the first request that reaches it — never at startup.
//
// RequirePermission rather than RequireClaim("permission", …), and constants
// rather than literals: the claim type belongs to Common.Web so a policy and a
// resource check cannot drift apart, and the name is written twice — here and
// at the endpoint — so the compiler should be the thing comparing them.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting. Each line below depends on
// the ones above it, and getting it wrong fails silently rather than loudly.
// §10.6's one header: nosniff on every response, including the ones
// UseExceptionHandler writes below. Above everything, so nothing can answer
// without it — and written from OnStarting, so the handler's clear does not
// take it off the 500.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs
// §10.5's promise applied to the statuses no handler produces: a challenge
// and a forbid are written by the middleware below and carry NO BODY, so the
// platform's one error shape had two holes in it until PR-17 measured a 401.
// Since .NET 8 this middleware writes them through IProblemDetailsService.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOrderEndpoints();          // §11.4

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
// One line here rather than InternalsVisibleTo: it does not have to name the
// assembly that consumes it, and the consumer is Ordering.TestSupport rather
// than either of the test projects — which is the version people get wrong.
public partial class Program;
```

Every ordering constraint below is stated rather than left to the sample,
because each one produces a defect that no test catches by accident:

| Rule | What breaks otherwise |
|---|---|
| `UseSecurityHeaders` outermost, above `UseExceptionHandler` | A response written by anything above it carries no `nosniff` ([§10.6](10-api-gateway.md), [ADR-031](adr/ADR-031-the-service-owns-nosniff-the-ingress-owns-hsts.md)). Outermost is only half the rule, though, and the other half is not an ordering at all: the extension writes from `Response.OnStarting` rather than before `next`, because `UseExceptionHandler` **clears** the response before writing §10.5's problem body. A header assigned on the way in is gone from exactly the 500 where a caller-supplied value is most likely to be reflected — so this line placed first and assigning eagerly would still lose the case it exists for |
| `UseCorrelationId` before everything that logs, `UseExceptionHandler` immediately above it | Early log lines and traces have no correlation ID, so the one request you need to follow is the one you cannot. The handler is the deliberate exception — it has to wrap the middleware below it to catch their faults, and it reaches the ID through `Request.Headers` rather than the log scope ([§10.4](10-api-gateway.md)). This row said the handler was **alone** above it until `UseSecurityHeaders` landed; that line sits above both and decides nothing about correlation, which is why "immediately" is the word doing the work |
| `UseAuthentication` before `UseAuthorization` | **Every authenticated request 401s** — in a `WebApplication` too. Omitting a call is repaired by auto-insertion; writing both in the wrong order is not, because the markers they set suppress it. See the callout below |
| `UseAuthentication` before `UseRateLimiter` (gateway only) | Same empty `User`, but this one does not 403 — §10.3's per-user partition key silently degrades to per-IP, and everyone behind one NAT shares a single bucket. **Silent is the measured half**: reversing the two leaves every test in `Gateway.Api.Tests` green, the authenticated-partition test included, so nothing in the repository is watching this line (see below) |
| `UseForwardedHeaders` above the limiter, and **below** the handler and the correlation ID (gateway only) | Two rules meeting, and this sample had them the wrong way round until PR-17: putting it first means a fault parsing a forwarded header unwinds past no exception handler, and anything the middleware logs runs outside the correlation scope. Neither of those two reads the address, so nothing is lost by letting them wrap it — while the limiter, which does read it, stays below. `ForwardedHeadersTests` covers the lower half: below `UseRateLimiter`, two forwarded addresses collapse onto the one connection the gateway can see |
| Both before endpoint mapping | `RequireAuthorization` has nothing to evaluate against |
| Health endpoints mapped **anonymous** | Probes 401, Kubernetes reads that as unhealthy, and the pod is killed in a loop |

Registration without middleware is the quiet failure mode here. `AddRateLimiter`
succeeds and does nothing if `UseRateLimiter` is absent — no error, no warning,
no failing test unless one specifically asserts on a limit.

`MapCommonHealthEndpoints()` is the same line it has always been and now means
something stronger. An empty predicate set is a passing predicate set, so a host
that registered no readiness checks answers `/health/ready` with 200 without
having verified anything — indistinguishable from a host that deliberately gates
readiness on nothing. The parameterless call is now the claim that this service
**does** gate readiness on something, and [§13.5](13-observability.md)'s helper
refuses to start it when no `ready`-tagged check is registered. A service whose
readiness set goes missing whole in a refactor therefore fails at startup rather
than taking traffic it cannot serve; the two hosts entitled to an empty set say
so at the call site instead, and the gateway below is one of them.

**Whole, and not one member of it** — the guard asks whether *any* registration
carries the tag, so a service that drops its `AddSqlServer(...)` while keeping
its Redis and broker checks starts exactly as before. §13.5 states the bound and
argues why the narrower case is not caught; it is named here because this table
is where the line gets read, and a guard read as stronger than it is buys a
confidence nobody checked.

> **`AddAuthentication` is not in that class, and saying it was cost this table
> a wrong row — twice, in opposite directions.** `WebApplication` adds the
> authentication and authorization middleware itself whenever the matching
> services are registered, so **deleting** `app.UseAuthentication()` from a
> service host changes nothing observable — verified by deleting it from
> `Catalog.Api/Program.cs`, after which every test in the repository still
> passed. That is the correction this callout was written for.
>
> **Reversing the two is a different matter, and the row above used to promise
> it was harmless.** Auto-insertion is suppressed by the markers the explicit
> calls set, and it repairs an *omission* rather than an ordering: with both
> calls present in the wrong order, authorization evaluates against a `User`
> nothing has populated and challenges. Measured through a real
> `WebApplication` over three pipelines — correct order 200, **reversed 401**,
> neither call 200 — which is the only arrangement of those three that a reader
> would not predict.
>
> So the honest summary is that the framework protects you from forgetting a
> line and not from misplacing one. Write both, in this order, and let
> `AuthenticationMiddlewareTests` hold the claim: it drives all three pipelines
> and is the regression guard if a release ever stops auto-inserting.

The **gateway** has its own pipeline and is the only place rate limiting is
applied (§10.1); a service behind it does not call `UseRateLimiter`:

```csharp
// Gateway.Api/Program.cs
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddCommonWebDefaults();                                  // §13.2

// §10.1's body ceiling, and the only one in the platform. Kestrel's 30 MB is a
// web server's default rather than a choice; GatewayLimits argues the number.
// Enforced where the body is READ, which is inside the forwarder — so a request
// that fails authorization is refused without its size being considered.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayLimits.MaxRequestBodyBytes);

// §10.1's response compression. The providers and the compressible MIME types
// are the framework's defaults, deliberately — that list omits
// application/problem+json, which is what keeps §10.5's error bodies (the one
// place a client-supplied value is reflected back) out of the compressed set.
// EnableForHttps is the whole of ADR-020, and it is what makes compression
// happen at all: the block below rewrites Request.Scheme from the ingress's
// X-Forwarded-Proto, and this middleware decides at the first WRITE, so the
// scheme it reads is https even though the hop was plain.
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// RFC 9111's no-transform, which ASP.NET Core does not implement and which an
// intermediary may not ignore (ADR-020). Replace rather than a registration
// placed above AddResponseCompression, whose TryAddSingleton would otherwise
// make ordering decide it silently.
builder.Services.Replace(
    ServiceDescriptor.Singleton<IResponseCompressionProvider, NoTransformResponseCompressionProvider>());

// YARP is registered here and configured from the "ReverseProxy" section
// shown in §10.2. Without this, MapReverseProxy() throws at startup and the
// entire routing configuration is inert.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddRateLimiter(/* §10.3 */);

// Every policy §10.2's routes name that Common.Web does not already register.
// "authenticated" comes from AddCommonWebDefaults; this one is the gateway's
// own, and it is a permission check rather than a role check for the reason
// §11.4 gives. A route naming a policy nobody registered fails CLOSED and
// loudly: the config load throws out of MapReverseProxy() below, naming the
// policy and the route, so the process does not start (§10.2).
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(GatewayPermissions.InventoryAdmin, p => p.RequirePermission(GatewayPermissions.InventoryAdmin));

// Both of the following are conditional on the deployment shape, and each is
// REQUIRED once switched on. "Off" and "on but unconfigured" are different
// states: the first is a valid topology, the second is a silent defect.
bool behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
bool corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // A load balancer or Ingress sits in front (§15.3), so
    // Connection.RemoteIpAddress is the proxy on every request. Without this
    // the rate limiter partitions all anonymous traffic into ONE bucket and
    // its per-client limit becomes a global cap — configured, running, and a
    // denial of service against legitimate users rather than a defence.
    //
    // Read HERE and not inside the callback: an options callback runs when the
    // options are first resolved, so a missing section read from inside one
    // throws on a request rather than at startup — the deferral this pair of
    // flags exists to avoid.
    string[] trusted = builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!;

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress. Left empty, ASP.NET Core trusts nothing
        // beyond loopback and silently keeps the proxy's address; opened to
        // all, any client can spoof its partition key and bypass the limit.
        //
        // KnownIPNetworks and System.Net.IPNetwork, both spelled deliberately:
        // KnownNetworks carries ASPDEPR005 at .NET 10 — an error under
        // ADR-019 — and the IPNetwork it held is the one
        // Microsoft.AspNetCore.HttpOverrides declares, which the using above
        // brings into scope in place of the framework type the new property
        // takes. This sample said both the other way and did not compile.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (string cidr in trusted)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly rather than through a CDN or
// same-origin edge (§10.2). Enabled but unset would yield WithOrigins([]),
// which rejects every browser request while starting cleanly — surfacing as a
// CORS error in a console rather than as the missing setting it is (§15.4).
// Hoisted out of the callback for the reason given above: the CORS options are
// built on the first request that needs them.
if (corsEnabled)
{
    string[] origins = builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>() ?? [];

    // GetRequiredSection proves the section EXISTS and nothing more, and four
    // review rounds each found a value the previous check admitted:
    //
    //   ""                       binds from `Cors__Origins__0=`; WithOrigins
    //                            takes it and matches no browser. "Blank
    //                            counts as missing", §11.3's rule for
    //                            Identity:Authority
    //   "*"                      invalid beside AllowCredentials below, and
    //                            ASP.NET Core only says so when the policy is
    //                            BUILT — on a preflight, not at startup
    //   "https//spa.example"     one missing colon, compared literally
    //   "https://spa.example/"   every parsed property agrees it is fine;
    //                            the browser sends no trailing slash
    //   "https://spa.example:443" a browser omits the scheme's default port
    //
    // Seven rounds of that produced six clauses and a seventh value, so the
    // check stopped enumerating prohibitions. GetLeftPart(UriPartial.Authority)
    // IS the canonical origin — scheme, host, and a port only when it is not
    // the default — so demanding the configured text equal it accepts exactly
    // what a browser sends and rejects every variant at once.
    int[] malformed =
    [
        .. origins
            .Select((origin, index) => (origin, index))
            .Where(entry =>
                !Uri.TryCreate(entry.origin, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
                parsed.UserInfo.Length > 0 ||
                !string.Equals(entry.origin, parsed.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            .Select(entry => entry.index)
    ];

    // Three guards, not one condition. They fail for different reasons and a
    // reader needs to be told which: an empty list has no index to report, and
    // the wildcard's whole diagnostic is the word AllowCredentials — collapsing
    // them into one `||` produces "unusable at index " with nothing after it,
    // and loses the only sentence that makes the wildcard case actionable.
    if (origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
        throw new InvalidOperationException("'Cors:Origins' is enabled but holds no usable origin.");

    if (origins.Any(o => o == "*"))
        throw new InvalidOperationException("'Cors:Origins' contains '*', which AllowCredentials below forbids.");

    // Indexes, never the values: credentials in the authority are one of the
    // rejected shapes, and an exception message reaches the logs — where
    // §13.4's redactor scrubs keyed attributes and cannot see a secret
    // interpolated into a string. The guard that rejects a password must not
    // be the thing that publishes one.
    if (malformed.Length > 0)
        throw new InvalidOperationException($"'Cors:Origins' is not an origin at index {string.Join(", ", malformed)}.");

    builder.Services
        .AddCors(o =>
            o.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()));
}

WebApplication app = builder.Build();

// §10.6's one header: nosniff on every response, including the ones
// UseExceptionHandler writes below. Above everything, so nothing can answer
// without it — and written from OnStarting, so the handler's clear does not
// take it off the 500.
app.UseSecurityHeaders();
app.UseExceptionHandler();
app.UseCorrelationId();           // §10.4 — assigns or replaces the client's

// High enough to wrap every writer below it, because this middleware acts by
// replacing the response body feature. Nothing here is an ordering rule a test
// can catch — moving it below the auth pair changes no observable response,
// measured — but its ABSENCE is: AddResponseCompression succeeds and compresses
// nothing without it, the same quiet shape the limiter's registration has.
app.UseResponseCompression();     // §10.1, ADR-020

app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json

// Above everything that reads the client address, and below the two that do
// not. Until this runs the address is the proxy's; skipped when the gateway IS
// the edge (Compose), where it is already the client and trusting a forwarded
// header would let any caller choose its own rate-limit bucket.
if (behindProxy)
    app.UseForwardedHeaders();

if (corsEnabled)
    app.UseCors();

// Authentication FIRST, then the limiter, then authorization. §10.3's
// "authenticated" policy partitions on the subject claim, and until this line
// runs HttpContext.User is an empty principal.
app.UseAuthentication();
app.UseRateLimiter();             // §10.3 — needs the user, precedes policy work
app.UseAuthorization();

app.MapReverseProxy();

// The edge owns no database and no broker, so its readiness set is empty and
// that is the whole of §10.1's design rather than a gap. Declared rather than
// implied: an empty predicate set passes, so the two hosts entitled to one say
// so at the call site and every other host fails to start without checks.
app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);   // §13.5 — anonymous; kubelet carries no token

app.Run();
```

> **Nothing tests this ordering, and the attempt to test it is the evidence.**
> PR-17 added a test proving two authenticated subjects hold independent
> buckets — the property the subject partition key exists for — and then ran it
> against a pipeline with `UseRateLimiter` moved above `UseAuthentication`. It
> passed, and so did every other test in that project. The limiter is still
> live under the reversal, because the anonymous window still rejects at its
> hundredth request; why the authenticated bucket does not collapse onto the
> shared fallback there is unexplained, and an unexplained pass is not a guard.
>
> So the row above is two claims of different standing. That the failure is
> **silent** is measured. That the partition **degrades to per-IP** is
> reasoned from the code and is not observed by anything. Keep the order, and
> do not believe a test is holding it — the same posture the callout below
> takes for `app.UseAuthentication()` itself.

Rate limiting sits **between** authentication and authorization, and both halves
of that are load-bearing.

It must come *after* `UseAuthentication` because §10.3's `authenticated` policy
partitions on the subject claim. Before that line the claim lookup returns null
and the key falls back to `RemoteIpAddress` — which does not fail, it just
quietly meters every signed-in user behind one corporate NAT as a single client
on a bucket sized for one person. The per-user quota would be advertised,
configured, and absent.

Placing it after authentication costs less than the older "reject floods before
doing crypto" instinct suggests. A request with no `Authorization` header does
no cryptographic work at all — `JwtBearer` finds nothing to validate and returns
immediately — so an anonymous flood is still turned away for the price of a
header lookup. Only a flood that presents tokens pays signature validation, and
that is the unavoidable cost of knowing whose quota to charge.

It must come *before* `UseAuthorization` because policy evaluation is the
expensive half: `inventory:admin` (§10.2) walks the principal's permission
claims, and a request that is over its limit should never reach that.

The gateway uses the same `MapCommonHealthEndpoints` as every service rather
than mapping a probe inline. Its readiness set is empty — the gateway owns no
database — so `/health/ready` returns healthy as soon as the process is up,
which is correct.

**What changed is that the emptiness is now declared rather than left silent.**
`ownsNoReadinessDependencies: true` is a claim made at the call site, and
without it [§13.5](13-observability.md)'s helper refuses to start the host at
all. The reason is that the probe cannot tell the two cases apart: "nothing of
mine gates readiness" and "readiness was never wired up" both answer 200,
because an empty predicate set is a passing predicate set. The parameter is the
only thing that separates them, and the **default is the failure** — so the
burden falls on the host with nothing to declare rather than on the one that
quietly forgot.
Two hosts pass it — this one and the BFF, which §13.5 names as the two whose
dependencies do not gate readiness. Neither owns *none*: the gateway proxies
four services and the BFF calls Catalog (§9.7). Every service fails to start
without its own checks.

What matters as much is that the probes stay **anonymous**: mapped inline after
`UseAuthorization`, the gateway would be the one component whose own health
check could be rejected by its own auth pipeline.

## 4.3 What may be shared between services

Exactly one thing: `Common.Contracts`, containing integration event records and
nothing else. No behaviour, no validation, no domain types.

> **One exception, and its test is narrow**
> ([ADR-045](adr/ADR-045-the-checkout-quote-takes-quantities.md)). A **bound
> both sides of a boundary are obliged by** may live here too — `OrderLimits`,
> the quantities and line count an order accepts, which the BFF's quote must
> refuse exactly as Ordering's command does. It is still not validation: the
> validators stay where they are, one per host, and what is shared is the
> integers they read. The exception admits a value only when **both** sides are
> bound by it and a second copy could therefore drift. A number one service
> alone enforces — Catalog's id ceiling is the case to compare against — fails
> that test and stays with the service that owns it, because a copy here would
> drift from the one actually enforced.

**A record arrives in the PR whose code first publishes or consumes it**, and
a new service's contracts arrive with that service rather than ahead of it. A
record's members follow the same rule from the other direction: the PR that
becomes a contract's first producer is the last one that can fix its shape
for free ([§9.2](09-messaging.md)), so read the record against the type that
will fill it before writing the mapper, not after.

> **Trap — the shared kernel that ate the platform.** A `Common.Entities`
> assembly containing `Product`, `Customer` and `Order` looks like sensible reuse
> and is the single most reliable way to destroy service independence. Two
> contexts sharing an entity class cannot evolve their models separately, so they
> must deploy together, so they are one service with extra steps. Duplicate the
> class. The duplication is the point — each context keeps only the fields it
> actually needs, and they diverge correctly over time.

`Common.Domain`, `Common.Application` and `Common.Infrastructure` are shared
*mechanism*, not shared *model*: base classes, the dispatcher, the outbox. That
is legitimate, but keep them small and treat every addition sceptically — a
shared library used by six services and the BFF is a coordination point.

## 4.4 Pinning the toolchain and packages

`global.json` pins the SDK to one exact patch, so every developer and every CI
agent compiles with the same compiler and analysers. Without it, a machine with
a newer SDK can produce different diagnostics — or different behaviour — from
the build that was reviewed. The trap below is why the pin says `disable`
rather than `latestPatch`: only one of the two makes that first sentence true.

```json
{
  "sdk": {
    "version": "10.0.302",
    "rollForward": "disable"
  }
}
```

> **Trap — `latestPatch` is not a pin, and the sentence above is only true
> without it.** `latestPatch` accepts any patch inside the feature band, so it
> resolves to whatever the machine happens to have: a developer on `10.0.305`
> compiles with those analysers while CI, which `setup-dotnet` gives exactly the
> version named here, compiles with these. Since ADR-019 makes analyser output a
> build gate, that divergence does not show up as a warning — it shows up as a
> build that is green on every machine and red in CI, reproducing nowhere. This
> repository has had one.
>
> `disable` is what closes it: the version named is the version used, or
> `dotnet` refuses to run at all rather than quietly choosing another. Feature
> bands were never the exposure — `latestPatch` already declined to cross them
> (`10.0.100` rejects `10.0.302`) — patches were, and they are the ones that
> ship analyser changes.
>
> The cost is the intended one and it is larger than it was: every machine needs
> this exact patch, not merely one in the band, so a bump is a deliberate edit
> here that everyone installs before they can build. That is the same trade as
> the exact package pins below, applied to the compiler that reads them.
>
> **One "machine" cannot install anything, and that makes the pin a two-file
> edit.** The build stage of each service image ([§15.2](15-cicd-deployment.md))
> runs whatever SDK its base tag carries, so those `FROM` lines name this exact
> patch too and a bump here is a bump there in the same change. They floated on
> `10.0-noble` once, on the argument that copying this file in would make any
> mismatch loud. It did: the tag moved a feature band, `disable` refused it, and
> every image stopped building on a restore that exited 155. Loud is the right
> behaviour for a drift nobody chose and the wrong one for the artefact that
> ships.

`Directory.Packages.props` pins every package version once for the whole
repository. This prevents the situation where two services depend on different
EF Core minor versions and behave differently under identical code.

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup Label="Runtime">
    <!-- The base package, referenced only by Common.Infrastructure: §9.5's
         InboxFilter writes through the service's DbContext because sharing
         the handler's transaction is the point of it, and common code may
         name the base type but never a provider. Every service's own
         Infrastructure takes the provider below instead. -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.0" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.0" />
    <!-- Design-time only, referenced by each *.Migrator with PrivateAssets.
         `dotnet ef migrations add` (§7.4) needs it in the startup project and
         nothing at runtime does — the version is pinned to the line above
         because the tool and the runtime move together. -->
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.0" />
    <!-- Transitive, pinned deliberately, and the same shape as Microsoft.OpenApi
         below. The package above reaches it twice — through
         Microsoft.Build.Tasks.Core and through
         Microsoft.CodeAnalysis.Workspaces.MSBuild — and both floors resolve to
         9.0.0, which carries eight advisories. NU1903 turns that into a failed
         restore, so this pin is what makes the line above restorable at all.

         PrivateAssets on the Design reference means it never ships in an image.
         The restore fails anyway, and rightly: a design-time supply chain is
         still a supply chain, and this one runs on a developer's machine with
         their credentials. -->
    <PackageVersion Include="System.Security.Cryptography.Xml" Version="10.0.10" />
    <PackageVersion Include="Microsoft.Extensions.Caching.Hybrid" Version="10.0.0" />
    <!-- HybridCache's L2 (§8.2): AddStackExchangeRedisCache and
         RedisCacheOptions. The IDistributedCache implementation over
         StackExchange.Redis — a separate package from both, and the one that
         actually stores an entry in Redis. -->
    <PackageVersion Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.0" />
    <!-- The in-process cache §13.6's OutboxStats holds its five-second
         snapshot in. HybridCache above carries it transitively; the direct pin
         states the direct use, and central package management requires one for
         a direct PackageReference either way. Not a second cache in the sense
         §8 argues about — it stores no domain data and crosses no process. -->
    <PackageVersion Include="Microsoft.Extensions.Caching.Memory" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Http.Resilience" Version="10.0.0" />
    <!-- The container and logging contracts Common.Application compiles
         against (§6.2, §13.3). ASP.NET Core's shared framework carries both,
         but Application takes no FrameworkReference — §4.2 puts the web on the
         other side of the boundary, and the building block that defines
         IPipelineBehavior sits on this one. -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
    <!-- AddOptions<RedisCacheOptions>().Configure (§8.2) — options
         configuration called directly by Common.Infrastructure, so the
         assembly is referenced directly, on the register's honesty rule. -->
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <!-- The same argument one row down: AddCatalogInfrastructure names
         IConfiguration in its signature (§4.2) and *.Infrastructure is not a
         web project, so it pays for the contract as a package. -->
    <PackageVersion Include="Microsoft.Extensions.Configuration.Abstractions" Version="10.0.0" />
    <!-- The migrator's job host (§7.4). ASP.NET Core's shared framework carries
         the generic host, and a *.Migrator is a console job with no listener —
         so it is the one project shape here that pays for hosting as a
         package. -->
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
    <!-- IHostEnvironment, for RedisKeys' key prefix (§8.3). Rides in the
         shared framework and in the Hosting meta-package above, but
         Common.Infrastructure is a library that takes neither — it pays for
         the contract as a package, like the abstractions rows above. -->
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.0" />
    <PackageVersion Include="Dapper" Version="2.1.66" />
    <!-- SqlConnection itself, for §6.5's IDbConnectionFactory. EF's SqlServer
         provider already carries it transitively at exactly this version;
         the explicit pin exists because *.Infrastructure constructs the type
         by name, and referencing what is actually used keeps the register
         honest. -->
    <PackageVersion Include="Microsoft.Data.SqlClient" Version="6.1.1" />
    <!-- Exact major. v9 is commercially licensed — see ADR-003. The core
         package is a transitive of the transport one, pinned separately
         because five projects reference it directly: Common.Infrastructure
         since PR-14, for the IPublishEndpoint the outbox dispatcher publishes
         the Broker lane through (§9.4), and four test projects that drive the
         in-memory harness — core API, which a test project using no transport
         must not claim one for. Appendix B names them; this comment does not,
         because a list in two places is the drift that made it say "two"
         while five were true. Same version as the transport: they ship as one
         release. -->
    <PackageVersion Include="MassTransit" Version="8.5.3" />
    <PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.3" />
    <!-- §9.6's saga repository — EntityFrameworkRepository and
         ConcurrencyMode.Pessimistic — and ADR-032's transactional outbox,
         which is the same package and the same DbContext. Same version and
         same release as the two rows above: a saga repository a minor behind
         the state machine it stores is not a combination anyone tests.
         Referenced by Ordering.Infrastructure, which holds the only saga;
         by Ordering.Api.Tests, for the EntityFrameworkOutboxOptions<T> its
         Serializable assertion resolves; and by Ordering.TestSupport, for the
         InboxCleanupService<T> OrderingApiFactory removes from the test host.
         Enumerated rather than counted, on Appendix B's terms. -->
    <PackageVersion Include="MassTransit.EntityFrameworkCore" Version="8.5.3" />
    <PackageVersion Include="StackExchange.Redis" Version="2.9.11" />
    <PackageVersion Include="FluentValidation" Version="12.0.0" />
    <!-- AddValidatorsFromAssemblyContaining (§4.2's registration sample) lives
         in this separate package, not in FluentValidation itself. Same
         version, same licence, same publisher — pinned together. -->
    <PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.0.0" />
    <PackageVersion Include="Scrutor" Version="6.1.0" />
    <PackageVersion Include="Yarp.ReverseProxy" Version="2.3.0" />
    <!-- The BFF's one synchronous hop (§9.7). Grpc.* majors move on their own
         schedule, independent of the .NET release — exactly the case for
         pinning rather than trusting the SDK to carry a compatible version. -->
    <PackageVersion Include="Grpc.Net.ClientFactory" Version="2.71.0" />
    <PackageVersion Include="Grpc.AspNetCore" Version="2.71.0" />
    <PackageVersion Include="Grpc.Tools" Version="2.71.0" />
    <!-- 3.30.2, not 3.29.3, and the number could only be found by compiling
         it: Grpc.AspNetCore 2.71.0 floors this package at 3.30.2, and with
         CentralPackageTransitivePinningEnabled a lower pin is a package
         DOWNGRADE rather than a floor NuGet quietly raises — NU1109 fails the
         restore and the three rows above are unbuildable. This blueprint
         carried the lower number from the outside for four PRs. -->
    <PackageVersion Include="Google.Protobuf" Version="3.30.2" />
    <!-- §11.3's JWT bearer handler, referenced by Common.Web. Not carried by
         Microsoft.AspNetCore.App — the shared framework has the authentication
         abstractions and the cookie handler, and the JWT one has been a package
         since ASP.NET Core 3.0. Same version line as the runtime: it ships with
         the framework and moves with it. -->
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.10" />
    <!-- JwtSecurityTokenHandler, which §11.5's Keycloak suite reads an `aud`
         claim with. A transitive of the row above, pinned because a project
         names the type — and pinned to 8.19.2 because that is the floor it is
         reached at, anything lower being a downgrade NU1109 refuses. -->
    <PackageVersion Include="System.IdentityModel.Tokens.Jwt" Version="8.19.2" />
    <!-- PR-07's OpenAPI deliverable (Appendix C): the framework's own document
         generator — AddOpenApi/MapOpenApi, document only, no UI. -->
    <PackageVersion Include="Microsoft.AspNetCore.OpenApi" Version="10.0.10" />
    <!-- Transitive, pinned deliberately: every patch of the package above
         floors this at 2.0.0, which carries GHSA-v5pm-xwqc-g5wc, and NuGet
         resolves a floor to its lowest. NU1903 turns that into a failed
         restore, so the pin is what makes the line above buildable at all —
         CentralPackageTransitivePinningEnabled is why it works. -->
    <PackageVersion Include="Microsoft.OpenApi" Version="2.11.0" />
  </ItemGroup>
  <ItemGroup Label="Telemetry">
    <PackageVersion Include="OpenTelemetry.Extensions.Hosting" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Http" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.Runtime" Version="1.17.0" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.17.0-beta.1" />
    <PackageVersion Include="OpenTelemetry.Instrumentation.StackExchangeRedis" Version="1.17.0-beta.1" />
    <!-- No AspNetCore.HealthChecks.Rabbitmq beside these two, and the absence
         is a decision (PR-13): its parameterless AddRabbitMQ resolves an
         IConnection nothing registers — MassTransit does not expose one — and
         the bus health check AddMassTransit registers itself answers the
         question better, endpoints included. -->
    <PackageVersion Include="AspNetCore.HealthChecks.SqlServer" Version="9.0.0" />
    <PackageVersion Include="AspNetCore.HealthChecks.Redis" Version="9.0.0" />
  </ItemGroup>
  <ItemGroup Label="Test">
    <!-- Test packages are pinned for exactly the same reason as runtime ones.
         xUnit v2 → v3 changed IAsyncLifetime from Task to ValueTask (§12.4):
         a major that drifts in silently breaks every fixture in the repo. -->
    <PackageVersion Include="xunit.v3" Version="3.1.0" />
    <!-- The fixture contract (IAsyncLifetime, TestContext) for
         Catalog.TestSupport, which is a Library — xunit.v3 itself refuses
         non-Exe output and names this package as the alternative. Pinned to
         the runner's version because they ship as one release. -->
    <PackageVersion Include="xunit.v3.extensibility.core" Version="3.1.0" />
    <!-- The VSTest adapter, and the reason `dotnet test` discovers anything.
         Microsoft.NET.Test.Sdk is the host; it finds tests through an adapter,
         and xunit.v3 does not carry one. Without this line the build succeeds,
         the run reports zero tests, and CI is green on a suite it never ran. -->
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.1.5" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <!-- TestServer without a host project. Mvc.Testing carries this
         transitively, but WebApplicationFactory<T> needs an entry point and
         Common.Web has none — it is a library (§4.1). Referencing what is
         actually used keeps the register honest. -->
    <PackageVersion Include="Microsoft.AspNetCore.TestHost" Version="10.0.0" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="NSubstitute" Version="5.3.0" />
    <PackageVersion Include="Testcontainers.MsSql" Version="4.6.0" />
    <PackageVersion Include="Testcontainers.Redis" Version="4.6.0" />
    <PackageVersion Include="Testcontainers.RabbitMq" Version="4.6.0" />
    <!-- §11.5's suite, and the ONE place in the solution that runs a real
         Keycloak. The audience mapper it proves is realm configuration, so
         nothing compiles differently when it is missing. Same version line as
         the three above: the Testcontainers modules ship as one release. -->
    <PackageVersion Include="Testcontainers.Keycloak" Version="4.6.0" />
    <!-- Transitive of the three rows above, pinned deliberately, and the third
         instance of the shape Microsoft.OpenApi and System.Security.Cryptography.Xml
         already carry. Testcontainers 4.6.0 floors this at 2024.2.0 for its SSH
         port-forwarding path, and every version through 2025.1.0 carries
         GHSA-q939-rpr3-3284 — a malicious SCP server escaping the download
         directory through traversal sequences in a filename. NU1903 turns that
         into a failed restore, so the pin is what makes the three rows above
         buildable at all. Nothing here downloads over SCP; the pin is not a
         judgement that the advisory is reachable, only that a vulnerable
         package resolving into the graph is not a thing to carry. -->
    <PackageVersion Include="SSH.NET" Version="2026.0.0" />
    <PackageVersion Include="Respawn" Version="6.2.1" />
    <PackageVersion Include="WireMock.Net" Version="1.8.11" />
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.9.0" />
    <!-- ServiceCollection itself. §6.2's registration test and §6.3's ordering
         test resolve from a real container, and the abstractions package the
         source references has none to build — it is contracts only. -->
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <!-- ConfigurationBuilder and AddInMemoryCollection. AddRedisConnections
         takes IConfiguration (§8.2), and its tests build a real one rather
         than substitute the contract — the same argument as the container
         row above, one abstraction over. -->
    <PackageVersion Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
    <!-- The architecture gates of §4.2, which PR-07 turns from a review
         comment into a build failure. A major bump can change which rules
         exist, so it fails loudly rather than quietly stopping to enforce. -->
    <PackageVersion Include="NetArchTest.Rules" Version="1.3.2" />
    <!-- The in-memory reader the observability tests read back through:
         §13.4's redaction tests, which that chapter prints one of, the
         meter-coverage tests guarding §13.2's meter list in
         Common.Web.Tests, and the Redis client-span tests in
         Common.Infrastructure.Tests proving AddRedisConnections instruments
         its own connections. Test-only: nothing in src/ exports in
         memory. -->
    <PackageVersion Include="OpenTelemetry.Exporter.InMemory" Version="1.17.0" />
  </ItemGroup>
</Project>
```

**Every package means every package**, including the test ones — those are where
the version numbers here came from, and the list is the same set [Appendix B](appendix-b-licences.md)
registers. The two files answer different questions about the same dependencies:
Appendix B says whether a licence is acceptable, this file says which version CI
will actually resolve. A package in one and not the other is how a licence
boundary gets crossed by a restore, so
[`.github/licence-gate/`](../../.github/licence-gate/README.md) fails the build
on a package nobody cleared, and its README owns what it reads to find one and
what it refuses.

Appendix B is the wider list, though, and three kinds of row in it will never
have a pin here. A check that does not know them reports false positives until
somebody stops reading its output:

- **Infrastructure products** — SQL Server, Redis, RabbitMQ, Keycloak — are
  licensable in their own right but are containers, not packages. The
  `StackExchange.Redis`, `Testcontainers.*` and `AspNetCore.HealthChecks.*` pins
  above are the *client libraries* that talk to them: a different artefact under
  a different licence. Match on package identity, never on the product a package
  is named after.
- **The Aspire packages** of [§14.2](14-local-development.md) are deliberately
  unpinned. Aspire is optional, nothing references it until the AppHost is
  adopted, and its API has moved fast enough that pinning a version this
  document cannot keep current would be worse than pinning none. Adopting
  Aspire means adding the pins here in the same change — the licence rows
  already exist, so the gap this file shows is the reminder.
- **Either/or rows** — `Shouldly` *or* `AwesomeAssertions` — pin only the chosen
  library. Clearing a licence for an alternative is not a commitment to restore
  it. Keep such rows rare and word them as alternatives, because a row that
  reads as two dependencies when it means one is how this check starts being
  ignored.

The versions are those current at the review date in the header. A blueprint
cannot keep them accurate, and the licence gate does not try. Currency and
vulnerability scanning are a separate obligation, and the tooling for them is
not yet in this repository.

> **Trap — pinning floors instead of versions.** Writing `Version="8.*"`, or
> treating the file as a set of minimums to be "reviewed quarterly", means a
> routine `dotnet restore` can resolve forward across a major boundary. Where
> that boundary is also a **licence** boundary — MassTransit v8 → v9 is the live
> example (Appendix B) — the obligation is acquired by a restore rather than by a
> decision. Pin exact versions and upgrade deliberately.

Licence drift is only caught reliably by tooling — a convention will not survive
the twentieth dependency.

> **Trap — the sample above is a copy.** The fenced block in this section is a
> second transcription of `Directory.Packages.props`, and nothing about a
> passing build proves the two agree. The licence gate compares them, and
> reports a disagreement against this chapter.

## 4.5 Adding a service

**Four** of §4.1's six services share the shape below — Catalog, Ordering,
Inventory and Payments — and writing the fourth by hand is how it ends up
subtly different from the first three. One command renders it instead:

```bash
python tools/new-service/new_service.py Yankee --port 5199
```

The name and the port are a probe rather than a real service, and that is
deliberate: this sample said `Ordering --port 5101` until PR-18 made both of
those taken, at which point the command a reader copies from the chapter
raised `ScaffoldError` — and the paragraph below, which says the run refuses a
port another service already publishes, made the chapter contradict its own
sample. A probe cannot quietly become a service later.

It writes §4.1's five service projects, its three test projects and its
`TestSupport` library — nine in all, and §4.1 is explicit that the last is not
a test project — with everything the service template has accumulated: the
`DbContext` and its conventions
([§7.2](07-persistence.md)), `EfUnitOfWork` ([§6.3](06-cqrs.md)), the
connection factory ([§6.5](06-cqrs.md)), the readiness checks
([§13.5](13-observability.md)) — SQL registered by the service, the bus's
`masstransit-bus` by MassTransit itself, and a rendered service that lost them
would now fail to start rather than report ready, because
`MapCommonHealthEndpoints` refuses an empty readiness set unless the host
declares it owns none (§4.2) — the bus registration of
[§9](09-messaging.md), whose eager read means a scaffolded host refuses to
start without `ConnectionStrings:RabbitMq`, the migration job host
([§7.4](07-persistence.md)), the `InitialCreate` migration that creates the
schema and the `AddOutbox` one beside it — §9.4's table is wiring every
service has, and a service carrying the dispatcher without it would log a
failed claim twice a second from its first boot — the outbox itself with its
empty allow-list mapper, §9.5's inbox filter and retention purge, §11.3's JWT
validation, both images ([§15.2](15-cicd-deployment.md)) and
§4.2's architecture gates. The migrations it copies are `InitialCreate`,
`AddOutbox`, `AddInbox`, `AddOutboxRetentionIndex`, `AddIdempotencyMarkers`,
`IdempotencyMarkerCommittedAtDefault` and `AddIdempotencyMarkerRowVersion` —
the messaging tables ship with the
dispatcher that reads them, because a service carrying the dispatcher without
its table logs a failed claim twice a second from its first boot, and §8.5's
marker table ships on a sharper version of the same argument: without it the
service fails a retention purge every hour and then fails the first idempotent
command it is ever given
([ADR-037](adr/ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)).
**The marker's `CommittedAt` default travels for the reason the table itself
does**: the `SYSDATETIMEOFFSET()` default and the
cutoff `RetentionPurgeService` computes in SQL are two halves of one guarantee
([ADR-038](adr/ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md)),
so a service scaffolded with the table and without the default ages its markers
on the writing pod's clock while the purge ages them on the server's — the skew
that migration exists to remove, reintroduced in every new service by omission.
**The last one adds that table's `rowversion` and travels on the sharpest
version of the argument yet**: `RetentionPurgeService` names the column in both
of its marker statements, so a service scaffolded without the migration fails
its own purge with `Invalid column name 'RowVersion'` on the first pass
([ADR-041](adr/ADR-041-the-markers-delete-identifies-a-row-by-a-rowversion-not-a-timestamp.md)).
It then edits the shared files: `Platform.slnx`, the Compose index — one
`include:` line for the unit it just created — the `infra-only` override, which
excludes both halves of the pair, `.env.example`,
the ports table in `deploy/compose/README.md`
([§14.1](14-local-development.md)), the broker definitions that grant the new
service an account of its own — without which it renders a service that starts
and cannot authenticate, since the broker has held no shared principal since
#44 — and, under `.github/secret-scan/allowed/`, the file covering each
entry's tree, one accepted-finding line per
**distinct** finding the render produces — two lines carrying one value under
one rule in one file are one finding and take one entry. **The last is the
difference between a service that renders and a service that can be
committed**:
[§15.1](15-cicd-deployment.md)'s scan reads the working tree, so it reads the
tree this leaves behind. The scaffold loads the real scanner and takes the
fingerprints from it rather than computing them, because computing them would
be a second implementation of which substring each rule matches — and a
fingerprint matching nothing is a stale entry that fails the build. Where
`.github/secret-scan/` is absent it writes nothing and says nothing, which is
the case in the scaffold suite's own synthetic root. The new service
builds and its **eighty-nine** tests pass before a line of it is written,
**forty-six** of them against real SQL Server and RabbitMQ containers —
counts measured against a rendered service, by PR-18 when they read forty-one
and sixteen three PRs after they stopped being true, again by PR-22 when they
read fifty-six, twice by PR-32, twice by PR-33, and again by PR-35. The
forty-six is the `Category=Integration` count of §12.4, which is a filter
rather than a tally.

**PR-35 found them stale by four before it had added anything**, which is the
first time the drift was somebody else's rather than the recounting PR's own.
PR-34 put four cases into `RetentionPurgeTests` for
[ADR-039](adr/ADR-039-the-markers-purge-asks-the-claim-rather-than-out-counting-it.md)
— a file the scaffold copies — and left this pair alone, so the tree read
eighty-seven while this sentence said eighty-three. PR-35 then added two and
measured eighty-nine, which is the arithmetic *failing* in the direction the
rule below predicts: two added, six apparent. **A figure nobody recounts goes
stale on the next PR's clock, not on its own**, and the only way to find that
out is the render.

**Arithmetic is not a remeasurement, and the two rules that follow from that
have both been tested by now.** PR-22 added three tests to the template and the
total moved by four, so whoever changes what the scaffold copies renders
`Yankee` and runs it rather than adding to the number here — which is what
PR-32 did when ADR-037's marker suite joined the template: rendered, built, and
run as `Yankee.Domain.Tests` (1), `Yankee.Application.Tests` (18) and
`Yankee.Api.Tests` (60) — **twice**, because the first render predated a test
that the same pull request later added to the template, and the figures were
stale between one commit and the next. PR-33 rendered again and read 1, 18 and
64, its four additions all landing in the API suite and all wanting a
container; it recounted because a reviewer noticed the figures had gone stale
inside the pull request that made them so, and then rendered a second time
because a later round of the same review added a fourth test — which is PR-32's
own two-render story arriving for the same reason one pull request on. The
second rule is that this is the only figure in
this section a reader cannot check from the tree, so it is the one to distrust
first — a number three PRs stale here looks exactly like a number taken
yesterday.

**There is no template directory, and that is the design.** The script reads
`src/Services/Catalog` at run time, so there is exactly one copy of the
wiring — the copy CI builds and `dotnet test` exercises — and an improvement to
the template reaches the next service the next time it runs. A tokenised copy
beside it would be a second `DbContext`, a second migrator host and a second
Dockerfile that nothing builds and nothing reconciles.

**It copies no domain.** Catalog's `Product`, its command, its query and its
endpoints are excluded by name; what a new service inherits is PR-07's state
with the later wiring on it, not PR-10's state with the nouns changed.
Renaming an aggregate would hand the next service a deletion job and a
vocabulary it did not choose. Three things therefore arrive with the first real
slice rather than with the scaffold — each with the part of it that needs them,
not as a set, and each noted at the line concerned in the generated code. The
first handler of either kind brings the application-test container wiring and
the test that §6.2's scan produced a registration; the first validator brings
the test for the validator scan; the first *query* brings `Dapper`, which a
command-only slice must not add. Both those scans fail silently when lost,
which is why the tests are named rather than left to be missed.

The `AssemblyMarker` runs the other way, and the distinction is worth keeping
straight. The scaffold **emits** it, because the §4.2 gates must name a type in
an assembly that has none; the first aggregate is when it is **deleted** and
the gates re-anchor on that aggregate. It is the one generated file written to
be removed, and its own doc comment says so.

> **The scaffold fails loudly or not at all.** Every piece of Catalog text it
> names must match exactly once, the whole render is built in memory and
> validated before a single file is created, and any file under
> `src/Services/Catalog` it cannot classify as template or slice stops the run.
> The price of having no second copy is that the first one moves; the price is
> paid by refusing, never by silently emitting a service that still names
> Catalog. Its own tests render this repository for the same reason — a fixture
> tree would test the script against a template that cannot drift.
>
> **That guarantee is about validation, not about the write.** A run the
> scaffold refuses writes nothing; a disk that fills up halfway through the
> write leaves a partial tree, and no transaction log is kept to undo it. The
> target is a git checkout — `git status` shows exactly what landed — and a
> second, untested rollback mechanism for something version control already
> does is not worth having.

`--port` is required and never derived. A port is an allocation recorded in
§14.1 and in `deploy/compose/README.md`; a script that guessed one would
quietly disagree with a printed chapter. The run refuses a port another service
already publishes.

**It refuses a *name* on the same terms, and the collision is one the rename
creates rather than one the operator could see.** §7.1's runtime key is
`ConnectionStrings__<Service>`, so a service named after one of §14.1's
infrastructure connections renders a key the api block already declares — and
nothing else catches it: the rename is correct, no template token is left
behind, and duplicate keys leave the YAML well formed, so whatever parses the
file keeps one of the two values and discards the other. The guard is a
predicate over the rendered `environment:` mappings and never a list of the
names it happens to catch today, because a list goes stale the moment §14.1
gives that block another `ConnectionStrings__*` key.

Three things are outside it, and none is silently missing: the gateway route
([§10.2](10-api-gateway.md)) — the route belongs to the gateway's
configuration, not the service's tree — the Helm chart
([§15.3](15-cicd-deployment.md)), and a Worker host in place of an API, which
Shipping and Notifications take (§4.1) and which joins the script with the
first one built.

**The chart's exclusion cost nothing until PR-23 and now costs something**, and
the sentence above is the same either way, which is why it is worth saying
separately. Before the charts existed, "the scaffold writes no chart" described
a file nobody had; since they exist, a scaffolded service compiles, tests,
starts under Compose — and cannot be deployed, with nothing in the render
saying so. That is a gap in the scaffold rather than a contradiction here, and
it is **owed**, and the shape of what is owed is the shape §15.3 already
describes: a `Chart.yaml`, one one-line include per template the library chart
defines, and a `values.yaml`. Only the last is real work, and it is real work —
it carries every per-service decision, which is precisely what a template
cannot guess and what §15.3 spends a section arguing.

**No file count here, deliberately.** An earlier revision said "four files,
three of which are one line", which was wrong on both numbers and wrong in a
way that would have propagated: a later PR closing this gap by emitting four
files emits a chart that does not deploy. The count moves whenever the library
gains a template — the deployables today carry six of those includes or seven,
depending on whether the service owns a database — so the thing worth writing
down is the rule, not the arithmetic.

**The scaffold refuses `Shipping` and `Notifications` by name until it can.**
Documenting the gap left the script willing to render either as an API service,
which would have contradicted §4.1 quietly — Notifications has no Domain
project at all. A note is not a guard, and the two names come off that list
with the PR that adds the mode.

---

[← §3 Bounded contexts](03-bounded-contexts.md) · [Index](README.md) · [§5 Tactical DDD →](05-tactical-ddd.md)

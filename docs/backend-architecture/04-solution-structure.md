# 4. Solution and folder structure

## 4.1 Repository layout

A single repository. Independent deployability comes from the CI pipeline
building and releasing services separately, not from separate git repositories.
A monorepo makes cross-cutting changes and contract updates atomic and reviewable.

```
/
├── src/
│   ├── BuildingBlocks/
│   │   ├── Common.Domain/              the base types of §5.5, and §4.3's one table
│   │   ├── Common.Application/         Dispatcher, pipeline behaviours, Result<T>
│   │   ├── Common.Infrastructure/      Outbox, inbox, idempotency markers,
│   │   │                               EF conventions, Redis, and §11.5's
│   │   │                               client-credentials grant (ADR-052)
│   │   ├── Common.Web/                 Host defaults: OTel, health, auth, ProblemDetails.
│   │   │                               Referenced by every host. NOT resilience —
│   │   │                               a host holding an outbound client (§9.7)
│   │   │                               keeps that client's policy beside it.
│   │   │                               (Aspire's template calls this ServiceDefaults.)
│   │   └── Common.Contracts/           Integration event DTOs — the ONLY shared types
│   │
│   ├── Gateway/
│   │   └── Gateway.Api/                YARP host
│   │
│   ├── BFF/
│   │   ├── Web.Bff/                    Aggregation for the web client (§10.1).
│   │   │                               The only host that calls a service
│   │   │                               synchronously on a request path (§9.7,
│   │   │                               ADR-052); it binds Identity:Client, and
│   │   │                               the grant's code is
│   │   │                               Common.Infrastructure's (§11.5)
│   │   ├── Web.Bff.Persistence/        ADR-051's projection schema: the
│   │   │                               context and its migrations, and nothing
│   │   │                               a web host carries
│   │   └── Web.Bff.Migrator/           Migration job host (§7.4)
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
│       ├── Notifications/              (Application, Infrastructure, Migrator, Worker)
│       └── Privacy/                    (same five projects)
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
│   ├── Common.TestSupport/             The body every service's
│   │                                   ServiceFixture derives from (ADR-056).
│   │                                   Not a test project, and not a
│   │                                   building block
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
│   │                                   gRPC server on loopback, ADR-051's
│   │                                   schema through the real migrator, and
│   │                                   the ONE suite in the solution that runs
│   │                                   a real Keycloak — the audience mapper
│   │                                   it proves is realm configuration, so
│   │                                   nothing compiles differently when it is
│   │                                   missing
│   ├── Web.Bff.TestSupport/            The stub Catalog, the SERVER half of
│   │                                   pricing.proto with it, and the
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
│   └── Platform.IntegrationTests/      Contract-assembly tests (§12.6) and the
│                                       journey (§12.1) — the only suite that
│                                       references every service
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
│   └── keycloak/                       §11's realm obligations, checked over
│                                       any realm
│
├── docs/
│   ├── backend-architecture/           This document, one file per chapter;
│   │                                   one per ADR under adr/, indexed by
│   │                                   Appendix A
│   ├── runbooks/                       The one each alert names, some shared
│   │                                   (§13.9), plus a README that is excluded
│   │                                   from the pairing by name
│   ├── secrets.md                      How a secret reaches a pod and how each
│   │                                   kind is rotated — the operational half
│   │                                   of §15.4, which keeps the inventory
│   └── testing.md                      How to run the suites, what needs
│                                       Docker, and the categories of §12.4 —
│                                       the operational half of §12, which
│                                       keeps the strategy
│
├── tools/
│   ├── bff-replay/                     ADR-051's rebuild: a console that
│   │                                   replays the publishers' outbox rows to
│   │                                   the BFF's queue. Built with the
│   │                                   solution; its suite is Web.Bff.Tests
│   ├── dead-letters/                   Lists, inspects, replays or discards
│   │                                   what §13.6's dead-letter alerts page
│   │                                   on. Stdlib Python and its tests, over
│   │                                   the Management API
│   └── new-service/                    The scaffold of §4.5 and its tests.
│                                       Stdlib Python, no restore — it renders
│                                       a service from Catalog at run time
│
├── artifacts/                          Every build's output: bin, obj and
│                                       publish take a subdirectory per project,
│                                       package groups by configuration.
│                                       Generated and git-ignored, and drawn
│                                       here because it is the reason src/,
│                                       tests/ and tools/ above hold source alone
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

**`src/`, `tests/` and `tools/` hold source, and nothing a build wrote.** A
project directory carrying its own `bin/` and `obj/` buries the files a reader
came for under the ones a build wrote, and every gate that walks a source tree
pays for it again in a skip list that silently decides what the gate reads.

Moving the output retires none of those lists, and expecting it to is the way
to get this wrong: a gate that walks the repository root has to decline
`artifacts/` by name, and one that walks `src/` still meets a checkout made
before the move. What it does buy is that the tree a reader browses and the
tree a gate reads are the same tree, and that a directory left out of a skip
list is a gate reading too much rather than a gate reading a build.

**That `src/`, `tests/` and `tools/` hold nothing a build wrote is checked
rather than asserted**, by
[`.github/output-gate/`](../../.github/output-gate/README.md), whose README
owns what it reads and which half of this sentence it leaves to a CI step.

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
| `*.Application` | its own Domain, `Common.Application`, `Common.Contracts`; `FluentValidation`, for its commands' validators; `Dapper`, for §6.5's query handlers alone ([ADR-005](adr/ADR-005-ef-core-for-writes-dapper-for-reads.md)) | EF Core, ASP.NET, any other concrete infrastructure |
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
test rather than a code review convention, as Ordering's in
`tests/Ordering.Domain.Tests/ArchitectureTests.cs` does:

```csharp
    [Fact]
    public void Domain_references_only_common_domain_and_the_framework()
    {
        // An exact allow-list, as §4.2's table is: a System.* prefix would pass System.Data.SqlClient.
        // System.Collections is the records' generated equality; System.Linq is the value objects'
        // letter scans and Order's Aggregate over its lines.
        string[] allowed = ["Common.Domain", "System.Runtime", "System.Collections", "System.Linq"];

        IEnumerable<string> referenced = typeof(Order).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!);

        referenced.ShouldAllBe(name => allowed.Contains(name));
    }
```

**The gate is an exact allow-list, because the table's row is one** —
"`Common.Domain` and nothing else". A blacklist only bans what someone thought
to name, and a `System.*` prefix still passes `System.Data.SqlClient` or a
serialiser. Each BCL assembly a domain starts using earns its line on purpose:
extending the list is the decision the gate exists to force, and
`System.Text.Json` is the extension the table forbids by name. §4.5's scaffold
renders the list with two entries, `Common.Domain` and `System.Runtime`,
because that is what an empty domain references; a live one grows, as the two
further entries above show.

### The composition-root rule

`*.Api` may reference Infrastructure, but only in one place. Stated normatively:

- **Only** `Program.cs` may reference `*.Infrastructure` types.
- **Endpoints and controllers may not.** No `DbContext`, no concrete
  repository, no `IPublishEndpoint`, no `IConnectionMultiplexer` — Application
  and Domain contracts only.

Without this rule the dependency table is satisfied at project level while being
violated everywhere that matters, because "Api may reference Infrastructure"
silently licenses an endpoint to inject a `DbContext`.

Ordering's gate, in `tests/Ordering.Api.Tests/ArchitectureTests.cs`, judges the
assembly whole and subtracts the root from the failures afterwards, where full
names are available to subtract it by and there is no candidate set to be
narrow:

```csharp
    private static readonly string[] Forbidden =
    [
        "Ordering.Infrastructure",
        "Microsoft.EntityFrameworkCore",
        "MassTransit",
        "StackExchange.Redis"
    ];

    /// <summary>Program or its global-namespace generated helpers, the one exemption §4.2 grants.</summary>
    private static bool IsCompositionRoot(string fullName) =>
        fullName == "Program" || (!fullName.Contains('.') && fullName.StartsWith('<'));

    [Fact]
    public void Nothing_but_the_composition_root_depends_on_infrastructure()
    {
        // Every banned package, not the Infrastructure namespace alone: DbContext and the like arrive transitively.
        TestResult result = Types
            .InAssembly(typeof(Program).Assembly)
            .ShouldNot().HaveDependencyOnAny(Forbidden)
            .GetResult();

        string[] leaked = [.. (result.FailingTypeNames ?? []).Where(name => !IsCompositionRoot(name))];

        leaked.ShouldBeEmpty($"leaked: {string.Join(", ", leaked)}");
    }
```

Top-level statements put `Program` and its helpers in the global namespace, so
they carry no dot; anything an endpoint generates is nested inside the
endpoint class and keeps its namespace. The exemption is the composition root
alone, and `The_composition_root_is_the_only_thing_exempted`, in the same
file, asserts that it has not grown.

> **The gate has no selector, because each selector it could use selects
> less than it claims.** A namespace selector such as
> `.ResideInNamespaceContaining(".Endpoints")` stops covering the transport
> surface the moment a service adds one in another namespace, as Ordering's
> gRPC service in `.Grpc` is. A namespace *pattern* moves the hole one
> namespace further out. Excluding compiler-generated types by name exempts
> endpoint lambdas, because a closure is generated code. And filtering
> candidates through `HaveName(...)` exempts them again, because that predicate
> selects nothing for a nested async state machine — and an empty selection
> reports **success**.
>
> A companion test naming the known adapters closes none of it: the set such a
> test inspects is unchanged by a type the selector never picked up, so it
> passes exactly as before. It guards against *narrowing* the rule, never
> against *outgrowing* it.

> **One gap belongs to NetArchTest rather than to the rule.** The library does
> not analyse compiler-generated nested types, so a forbidden reference used
> *only* inside an endpoint lambda is invisible to it.
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
the boundary is checkable, in
`tests/Ordering.Application.Tests/ArchitectureTests.cs`:

```csharp
    [Fact]
    public void Application_and_domain_do_not_reference_masstransit()
    {
        // §9.3's must-not list, whose one exemption is a saga's receive endpoint and its outbox (ADR-032).
        Assembly[] assemblies = [typeof(DependencyInjection).Assembly, typeof(Order).Assembly];
        foreach (Assembly assembly in assemblies)
        {
            Types
                .InAssembly(assembly)
                .ShouldNot().HaveDependencyOn("MassTransit")
                .GetResult().IsSuccessful
                .ShouldBeTrue(assembly.GetName().Name);
        }
    }
```

The saga may send and publish because its receive endpoint carries
MassTransit's transactional outbox (ADR-032), which writes those messages to
the same `DbContext` and the same transaction as the saga instance — a
guarantee that exists on the consume pipeline and nowhere else. A handler that
copies the saga's style gets a dual write with no outbox behind it, and it
works in every test where the broker is up.

If the namespace rule proves awkward to enforce, split the host into
`Ordering.Host` (composition, references Infrastructure) and `Ordering.Api`
(endpoints, does not) and let the project reference enforce it. That is the more
robust option; the single-project namespace rule is the lighter one.

**"Namespace rule" here is the rule, not the selectors that "The gate has no
selector" rules out**, and the two are easy to read as one thing this close
together. What the gate does not do is *select* candidates by namespace; what it
does do is exempt the composition root by name — `IsCompositionRoot` passes
`Program` and the helpers top-level statements generate beside it in the global
namespace, names opening with `<`, so an endpoint is judged wherever it lives,
`Catalog.Api.Endpoints` included. The alternative above replaces that
discrimination with a project boundary, which is why it is the heavier option
and the one a compiler enforces.

**These tests are a CI gate from the first template commit**, not a later
addition. An architecture rule introduced after the violations exist is a
backlog item; one introduced before them is a constraint.

### The rest of the table, enforced

**The three gates above leave one clause and one row uncovered.** Between
them they cover the Domain row whole, the Application row's named must-nots,
and the Api row's composition-root half. What none of them says is the clause
**Infrastructure, Migrator and Api** all carry — *must never reference another
service's projects* — and none of them gates the Migrator row.

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
stated as an allow-list rather than a deny-list of service names, which is
what makes it cover a service before it exists: this service's own
assemblies and the building blocks, both *by name*, because a test library
shares each one's prefix and is neither — `Common.TestSupport` (§4.1) is named
like a building block, and `Ordering.TestSupport` like an Ordering project.

Ordering's, in `tests/Ordering.Api.Tests/ArchitectureTests.cs`, is the list of
building blocks, the service's own projects read off the five assemblies, and
the helper its test calls once for each of them; the excerpt leaves out the
lines between the first two:

```csharp
    /// <summary>§4.1's building blocks by name, because Common.TestSupport is named like one and is not.</summary>
    private static readonly string[] BuildingBlocks =
        ["Common.Application", "Common.Contracts", "Common.Domain", "Common.Infrastructure", "Common.Web"];

    /// <summary>The service's own projects by name, never its prefix, which Ordering.TestSupport shares.</summary>
    private static readonly string[] OwnProjects = [.. ServiceAssemblies.Select(assembly => assembly.GetName().Name!)];

    private static void ShouldStayInsideThisService(string subject, AssemblyName[] references)
    {
        string[] foreign =
        [
            .. references
                .Where(IsFirstParty)
                .Select(reference => reference.Name!)
                .Where(name => !BuildingBlocks.Contains(name) && !OwnProjects.Contains(name))
                .Order()
        ];

        foreign.ShouldBeEmpty($"{subject} reaches across a service boundary: {string.Join(", ", foreign)}");
    }
```

`Common.Contracts` is on the list, which is §4.3 from the other side: it is a
building block rather than a service, so the one assembly permitted to cross a
boundary needs no exception of its own.

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
> projects is.** No project here sets `SignAssembly`, and every service's gate
> re-measures the package half for what its assemblies reference on each run,
> since an unsigned package it does not name fails it, as below. `Dapper` is
> the one unsigned package in the graph and is named for that reason alone.
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

**Nothing references the migrator, and saying so is a third gate rather than a
shorter list of this service's own projects.** No row in the table names the
`*.Migrator` as something a project *may* reference: it is a leaf, a job host
that resolves a `DbContext` and calls `Database.Migrate()`
([§7.4](07-persistence.md)), so it references and is not referenced. The
cross-service gate cannot see that edge, because it admits every one of this
service's own assemblies, the migrator among them — which is precisely what
makes an `Api → Migrator` reference invisible to it. That edge is inside one
service and still forbidden, so it gets a rule of its own over the other four
assemblies. **The two gates ask different questions** — *whose is it* and
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
so. Adding the project reference to close the gap is rejected:
it would make the second row disagree with the first about what "its own
Domain" includes, to no benefit a compiler can see.

**Each list is a subset check rather than an equality.** An entry for something
an assembly does not reference is a pre-authorised hole rather than a failure,
which is a real cost and the smaller one — the alternative fails a build for a
legitimate *removal*, and the decision worth forcing is the one that adds a
dependency.

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

Each layer exposes exactly one registration method for what it holds, and an
outbound hop registers in one of its own beside it
([ADR-055](adr/ADR-055-an-outbound-hop-registers-beside-its-layer-and-the-host-calls-it.md)).
`Program.cs` calls them, binds the host's own client credentials (§11.5), and
does nothing else with Infrastructure — which is what makes the rule above
enforceable rather than aspirational, and what lets tests exercise the real
registration path ([§6.2](06-cqrs.md)) instead of a hand-built container.

`AddOrderingApplication`, in
`src/Services/Ordering/Ordering.Application/DependencyInjection.cs`:

```csharp
    public static IServiceCollection AddOrderingApplication(this IServiceCollection services)
    {
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);   // §6.2
        services.AddDispatcher();

        // Explicit rather than scanned, beside the dispatcher it serves, as §4.2's sample has it (§7.5).
        services.AddDomainEventDispatcher();

        // §9.3's allow-list, explicit so that what this service publishes is a decision, not a scan's finding.
        services.AddScoped<IIntegrationEventMapper, OrderingIntegrationEventMapper>();

        // The clock (§5.4) and the request histogram (§13.3), which LoggingBehavior injects.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RequestMetrics>();

        // §13.3's business instruments; Infrastructure's projection is the only caller (§6.6).
        services.AddSingleton<OrderMetrics>();

        // Registration order is pipeline order; idempotency sits inside validation and outside the transaction (§6.3).
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));

        // Scoped, as a command is; only open generics inject it, so a missing one fails the first command.
        services.AddScoped<IdempotencyContext>();

        // §4.2's sample line, anchored on a validator: losing the last one fails the build, not silently.
        services.AddValidatorsFromAssemblyContaining<PlaceOrderValidator>();
        return services;
    }
```

Most comments name their line's owner, and three reasons live here alone.
`TimeProvider.System` is registered here so that Application's registration
stands on its own, rather than on whatever a host's other building blocks happen
to `TryAdd`. `AddDispatcher` and `AddDomainEventDispatcher` stand in for
`AddScoped` lines this assembly cannot write, because `Dispatcher`,
`DomainEventDispatcher` and `ProjectionRegistry` are internal to
`Common.Application` (§6.2, §7.5). And the mapper is not an open generic, so
§6.2's scan cannot find it; `DomainEventDispatcher` injects it, so a missing
line fails `ValidateOnBuild` rather than failing silently.

`AddOrderingInfrastructure`, in
`src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs`:

```csharp
    public static IServiceCollection AddOrderingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ADR-079, before any connection below is opened.
        services.AddTransportSecurity();

        // §7.1's runtime identity, no DDL; EnableRetryOnFailure makes §6.3's CreateExecutionStrategy a real retry.
        services.AddDbContext<OrderingDbContext>(o =>
            o.UseSqlServer(
                configuration.GetConnectionString("Ordering"),
                sql => sql.EnableRetryOnFailure()));

        // §9.5's inbox filter names DbContext. One instance, not AddScoped<DbContext, OrderingDbContext>(), which
        // would commit the inbox row in a second context's own transaction.
        services.AddScoped<DbContext>(sp => sp.GetRequiredService<OrderingDbContext>());

        // Each layer scans itself (§6.2); scanning only Application would skip this layer's projections and mappers.
        services.AddPluggableFrom(typeof(DependencyInjection).Assembly);

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();                     // §6.3
        services.AddScoped<IOrderRepository, OrderRepository>();             // §5.6

        // §6.4's price port, over the local projection, so the write transaction never waits on Catalog.
        services.AddScoped<IProductPriceReader, ProjectedPriceReader>();

        // §8.5's durable half, in EfUnitOfWork's transaction through the alias above. A missing line fails the
        // first command, not startup: ValidateOnBuild never constructs TransactionBehavior's open generic.
        services.AddScoped<IIdempotencyMarkerStore, EfIdempotencyMarkerStore>();

        // §7.5's two halves, scoped because the context is.
        services.AddScoped<IDomainEventCollector, EfDomainEventCollector>();
        services.AddScoped<IIntegrationEventPublisher, OutboxPublisher>();

        // Values, since Common.Infrastructure is every service's; one local, so no two tables name different schemas.
        const string schema = "ordering";
        services.AddSingleton(new OutboxTable(schema));
        services.AddSingleton(new InboxTable(schema));
        services.AddSingleton(new IdempotencyMarkerTable(schema));

        // §9.4's, §9.5's and §8.5's retention windows, registered rather than const so the service can change them.
        services.AddSingleton(new RetentionPolicy());

        // §9.4's persisted type names; the source is separate so a test host can add its own assembly.
        // The map is lazy, so MessageTypeMapValidator is what fails the host, not the first message, on a duplicate.
        services.AddSingleton(
            new MessageTypeSource(typeof(IIntegrationEvent).Assembly, typeof(Order).Assembly));
        services.AddSingleton(sp =>
        {
            MessageTypeSource source = sp.GetRequiredService<MessageTypeSource>();
            return new MessageTypeMap(source.Assemblies, source.Aliases, source.WrittenNames);
        });
        services.AddHostedService<MessageTypeMapValidator>();

        // §9.4's payload format. A value object on a domain event needs a converter here: most deserialise to
        // their default rather than failing, which §12.4's round trip catches.
        services.AddSingleton<JsonConverter, MoneyJsonConverter>();
        services.AddSingleton<JsonConverter, AddressJsonConverter>();
        services.AddSingleton<JsonConverter, PaymentReferenceJsonConverter>();
        services.AddSingleton<JsonConverter, TrackingNumberJsonConverter>();
        services.AddSingleton<OutboxJson>();

        // §13.3's messaging instruments.
        services.AddSingleton<MessagingMetrics>();

        // §13.6's outbox gauges. OutboxStats reads the runtime key's data plane (§7.1), and runs in gauge callbacks,
        // so it gets its own bounded connect timeout, which no query inherits.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Ordering"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp =>
            new OutboxStats(new SqlConnectionFactory(metricsConnectionString), sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // Constructs the metrics singletons at start, before the bus, so they exist for the first message (§13.6).
        services.AddHostedService<MetricsInitialiser>();

        // §8's two connections, read eagerly, so a missing key stops the host.
        services.AddRedisConnections(configuration);

        // The bus (§9); AddMassTransit registers its own readiness check.
        services.AddMassTransitMessaging(configuration);

        // §9.4's poll loop. The generic overload records the ImplementationType §12.4's fixture removes it by.
        // After the bus, since hosted services stop in reverse and the dispatcher drains into a live transport.
        services.AddHostedService<OutboxDispatcher>();

        // §9.4's, §9.5's and §8.5's retention. Last, so first stopped: an interrupted purge loses nothing.
        services.AddHostedService<RetentionPurgeService>();

        // §6.5's read side, singleton as §4.2's sample has it, on the runtime key rather than the migrator's (§7.1).
        services.AddSingleton<IDbConnectionFactory>(
            new SqlConnectionFactory(configuration.GetConnectionString("Ordering")!));

        // Readiness lives here, not in Common.Web, because it needs the connection strings (§13.5). Both Redis
        // instances, since AbortOnConnectFail is false and §8.1 gives them different servers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Cache)!,
                name: "redis-cache",
                tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Coordination)!,
                name: "redis-coordination",
                tags: ["ready"]);

        return services;
    }
```

Three reasons live here alone, and so do three absences.
`SqlConnectionFactory` is registered as an instance because it takes the
connection string, so a type registration leaves the container no constructor
to satisfy and `ValidateOnBuild` refuses the host; a singleton is safe because
the connections it hands out are the caller's to dispose.
`MessageTypeMapValidator` is registered before the dispatcher because hosted
services start in order. `Money` has a private constructor, so without its
converter it deserialises to a zero amount and a null currency and nothing
says so (§9.4).

The absences are registrations made elsewhere. `ICurrentUser` and
`AddHttpContextAccessor` belong to `AddCommonWebDefaults` (§11.4, §13.2):
neither names a service, and the implementation reads `IHttpContextAccessor`,
which arrives with a `FrameworkReference` that among the building blocks only
`Common.Web` has. `IIdempotencyStore` arrives with `AddRedisConnections`, which
registers `RedisIdempotencyStore` (§8.5), because a service either has Redis or
does not (§8.2). And there is no `ITokenCache`, `ClientCredentialsHandler` or
`ServiceIdentityOptions`, because Ordering makes no synchronous outbound call
and so holds no outbound identity of its own (§9.7, §11.5).

And `src/Services/Ordering/Ordering.Api/Program.cs`, from its builder on:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start on an unsatisfiable dependency or a captured scope, rather than on the first request.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddOrderingApplication();       // §6.2
builder.Services.AddOrderingInfrastructure(builder.Configuration);   // §4.2, §7.1

// Appendix C's OpenAPI deliverable: document only, no UI.
builder.Services.AddCommonOpenApi();

// ADR-052's server half; no interceptor, as the only caller-supplied value is parsed before the dispatcher.
builder.Services.AddGrpc();

// RequirePermission, so the claim type is PermissionClaim.Type's alone (§11.4). No orders:admin policy: that string
// is a claim CancelOrderHandler checks against a loaded aggregate.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(OrderingPermissions.Write, p => p.RequirePermission(OrderingPermissions.Write))
    .AddPolicy(OrderingPermissions.Cancel, p => p.RequirePermission(OrderingPermissions.Cancel))
    .AddPolicy(OrderingPermissions.DeliveryAddress, p => p.RequirePermission(OrderingPermissions.DeliveryAddress));

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's nosniff, above everything, so every response carries it, the handler's 500 included.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — above everything else that logs
app.UseRequestTimeouts();         // §9.7 — below the exception handler, which would answer 499

// §10.5's error shape for the bodiless challenge and forbid the middleware below writes.
app.UseStatusCodePages();         // §10.5 — 401 and 403 as problem+json
app.UseAuthentication();          // §11.3 — populates HttpContext.User
app.UseAuthorization();           // §11.4 — evaluates the permission policies

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();

app.MapOrderEndpoints();          // §11.4 — the group fails closed

// ADR-052, on the Http2 endpoint appsettings.json declares; [Authorize] travels on the service class.
app.MapGrpcService<DeliveryAddressService>().RetrySafe(RetrySafety.ReadOnly);   // ADR-052 — Get reads one address

app.Run();

// Top-level statements compile to an internal Program, which WebApplicationFactory cannot see (§12.4).
public partial class Program;
```

Ordering's permission policies are registered here rather than inside either
helper, because Application knows nothing about HTTP and `Common.Web` must not
know Ordering's names; a policy an endpoint names and nothing registers throws
on the first request that reaches it, never at startup. A constant names each
one, so the compiler compares the name written here with the one at the
endpoint. `public partial class Program` is one line rather than
`InternalsVisibleTo` because it does not have to name the assemblies that
consume it, `Ordering.TestSupport` and `Ordering.Api.Tests` among them, each of
which `InternalsVisibleTo` would have to list.

Every ordering constraint below is stated rather than left to the sample,
because each one produces a defect that no test catches by accident:

| Rule | What breaks otherwise |
|---|---|
| `UseSecurityHeaders` outermost, above `UseExceptionHandler` | A response written by anything above it carries no `nosniff` ([§10.6](10-api-gateway.md), [ADR-031](adr/ADR-031-the-service-owns-nosniff-the-ingress-owns-hsts.md)). Outermost is only half the rule, though, and the other half is not an ordering at all: the extension writes from `Response.OnStarting` rather than before `next`, because `UseExceptionHandler` **clears** the response before writing §10.5's problem body. A header assigned on the way in is gone from exactly the 500 where a caller-supplied value is most likely to be reflected — so this line placed first and assigning eagerly would still lose the case it exists for |
| `UseCorrelationId` before everything that logs, `UseExceptionHandler` immediately above it | Early log lines and traces have no correlation ID, so the one request you need to follow is the one you cannot. The handler is the deliberate exception — it has to wrap the middleware below it to catch their faults, and it reaches the ID through `Request.Headers` rather than the log scope ([§10.4](10-api-gateway.md)). `UseSecurityHeaders` sits above both and decides nothing about correlation, which is why the handler is *immediately* above it rather than alone |
| `UseRequestTimeouts` below `UseExceptionHandler` | Above it, the handler answers the cancelled request first, as a 499, and §10.5's 504 is never written ([ADR-066](adr/ADR-066-a-request-past-its-hosts-deadline-is-answered-504.md)) |
| `UseAuthentication` before `UseAuthorization` | **Every authenticated request 401s** — in a `WebApplication` too. Omitting a call is repaired by auto-insertion; writing both in the wrong order is not, because the markers they set suppress it. See the callout below |
| `UseAuthentication` before `UseRateLimiter` (gateway only) | Same empty `User`, but this one does not 403 — §10.3's per-user partition key silently degrades to per-IP, and everyone behind one NAT shares a single bucket. **Silent is the measured half**: reversing the two leaves every test in `Gateway.Api.Tests` green, the authenticated-partition test included, so nothing in the repository is watching this line (see below) |
| `UseForwardedHeaders` above the limiter, and **below** the handler and the correlation ID (gateway only) | Two rules meeting: putting it first means a fault parsing a forwarded header unwinds past no exception handler, and anything the middleware logs runs outside the correlation scope. Neither of those two reads the address, so nothing is lost by letting them wrap it — while the limiter, which does read it, stays below. `ForwardedHeadersTests` covers the lower half: below `UseRateLimiter`, two forwarded addresses collapse onto the one connection the gateway can see |
| Both before endpoint mapping | `RequireAuthorization` has nothing to evaluate against |
| Health endpoints mapped **anonymous** | Probes 401, Kubernetes reads that as unhealthy, and the pod is killed in a loop |

Registration without middleware is the quiet failure mode here. `AddRateLimiter`
succeeds and does nothing if `UseRateLimiter` is absent — no error, no warning,
no failing test unless one specifically asserts on a limit.
`AddResponseCompression` is the same: without `UseResponseCompression` it
succeeds and compresses nothing.

`MapCommonHealthEndpoints()` means more than it reads. An empty predicate set
is a passing predicate set, so a host that registered no readiness checks
answers `/health/ready` with 200 without having verified anything —
indistinguishable from a host that deliberately gates readiness on nothing.
So the parameterless call is the claim that this service **does** gate
readiness on something, and [§13.5](13-observability.md)'s helper refuses to
start it when no `ready`-tagged check is registered. A service whose readiness
set goes missing whole in a refactor therefore fails at startup rather than
taking traffic it cannot serve; the one host entitled to an empty set says so
at the call site instead, and it is the gateway below.

**Whole, and not one member of it** — the guard asks whether *any* registration
carries the tag, so a service that drops its `AddSqlServer(...)` while keeping
its Redis and broker checks starts exactly as before. §13.5 states the bound and
argues why the narrower case is not caught; it is named here because this table
is where the line gets read, and a guard read as stronger than it is buys a
confidence nobody checked.

> **`AddAuthentication` is not one of the registrations that does nothing
> without its middleware.** `WebApplication` adds the authentication and
> authorization middleware itself whenever the matching services are
> registered, so **deleting** `app.UseAuthentication()` from a service host
> changes nothing observable.
>
> **Reversing the two is a different matter.** Auto-insertion is suppressed by
> the markers the explicit calls set, and it repairs an *omission* rather than
> an ordering: with both calls present in the wrong order, authorization
> evaluates against a `User` nothing has populated and challenges. Through a
> real `WebApplication`, the correct order answers 200, the **reversed one
> 401** and neither call 200 — the only arrangement of those three that a
> reader would not predict.
>
> So the framework protects you from forgetting a line and not from misplacing
> one. Write both, in this order, and let `AuthenticationMiddlewareTests` hold
> the claim: it drives all three pipelines and is the regression guard if a
> release ever stops auto-inserting.

The **gateway** has its own pipeline and is the only place rate limiting is
applied (§10.1); a service behind it does not call `UseRateLimiter`.
`src/Gateway/Gateway.Api/Program.cs` follows in three excerpts. Its
registrations, up to the reverse proxy:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults(GatewayLimits.RequestTimeout);   // §13.2, §9.7

// ADR-083's root trace per request, which needs both: the hosting layer's propagator and OpenTelemetry's.
builder.Services.AddSingleton<DistributedContextPropagator, EdgeTracePropagator>();
Sdk.SetDefaultTextMapPropagator(new TraceContextPropagator());

// §10.1's request size limit; GatewayLimits argues the number.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = GatewayLimits.MaxRequestBodyBytes);

// §10.1's response compression; EnableForHttps is true against BREACH (ADR-020).
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// ADR-020's no-transform. Replace, because AddResponseCompression's TryAddSingleton would let order decide.
builder.Services.Replace(
    ServiceDescriptor.Singleton<IResponseCompressionProvider, NoTransformResponseCompressionProvider>());

// §10.2.
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
```

Then, past the rate limiter's two policies and its rejection writer, which are
§10.3's, the gateway's own authorization policies and the two blocks that are
conditional on the deployment shape:

```csharp
// §10.2's route policies beyond Common.Web's "authenticated", as permission checks for §11.4's reason.
builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy(GatewayPermissions.InventoryAdmin, p => p.RequirePermission(GatewayPermissions.InventoryAdmin))
    .AddPolicy(GatewayPermissions.PaymentsAdmin, p => p.RequirePermission(GatewayPermissions.PaymentsAdmin));

// Each is optional, and required once switched on: "on but unconfigured" is a silent defect.
bool behindProxy = builder.Configuration.GetValue<bool>("Ingress:Enabled");
bool corsEnabled = builder.Configuration.GetValue<bool>("Cors:Enabled");

if (behindProxy)
{
    // Read here, not in the Configure callback, so a missing section fails at startup rather than on a request.
    string[] trusted = builder.Configuration.GetRequiredSection("Ingress:TrustedNetworks").Get<string[]>()!;

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only the ingress: opened to all, any client could choose its own rate-limit partition.
        // KnownNetworks carries ASPDEPR005, an error under ADR-019; IPNetwork is qualified past HttpOverrides' own.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();

        foreach (string cidr in trusted)
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
    });
}

// Only when browsers call the gateway directly (§10.2); read here for the reason above.
if (corsEnabled)
{
    string[] origins = builder.Configuration.GetRequiredSection("Cors:Origins").Get<string[]>() ?? [];

    if (origins.Length == 0 || origins.Any(string.IsNullOrWhiteSpace))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' is enabled but holds no usable origin. An empty or blank entry yields a policy " +
            "matching nothing, so every browser request fails while the host reports healthy (§15.4).");
    }

    if (origins.Any(o => o == "*"))
    {
        throw new InvalidOperationException(
            "'Cors:Origins' contains '*', which cannot be combined with AllowCredentials — ASP.NET Core " +
            "throws when the policy is built, on the first preflight rather than at startup. Name the " +
            "origins, or drop credentials as a deliberate separate decision (§10.2).");
    }

    // One equality with the canonical origin rather than a list of prohibitions: the ways a string can be
    // an origin are finite and the ways it can fail are not. UserInfo is tested apart; the authority keeps it.
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

    // Indexes, never the values: a message reaches the logs, where §13.4's redactor cannot see a secret.
    if (malformed.Length > 0)
    {
        throw new InvalidOperationException(
            $"'Cors:Origins' is not a canonical origin at index {string.Join(", ", malformed)}. One is an http " +
            "or https scheme, a host and a port only when it is not the scheme's default, in lowercase: the " +
            "check is one equality with that canonical form (§4.2). The value is deliberately not echoed (§13.4).");
    }

    builder.Services
        .AddCors(o =>
            o.AddDefaultPolicy(p => p
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                // Neither header is CORS-safelisted, so a browser cannot read either without this.
                .WithExposedHeaders("Retry-After", CorrelationIdExtensions.Header)
                .AllowCredentials()));
}
```

And its pipeline:

```csharp
WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2).
// §10.6's header on every response, the exception handler's 500 included.
app.UseSecurityHeaders();
app.UseExceptionHandler();        // §10.5 — catches every fault below it
app.UseCorrelationId();           // §10.4 — adopts a plausible client ID, replaces any other
app.UseRequestTimeouts();         // §9.7 — below the exception handler, which would answer 499

// Above every writer it has to compress, because it works by replacing the response body feature.
app.UseResponseCompression();     // §10.1, ADR-020

// Above the auth pair, because it converts the bodiless challenge and forbid they write.
app.UseStatusCodePages();         // §10.5

// Above everything that reads the client address; skipped at the edge (Compose), where a forwarded header
// would let a caller choose its own rate-limit bucket.
if (behindProxy)
    app.UseForwardedHeaders();

if (corsEnabled)
    app.UseCors();

// Authentication before the limiter, because §10.3's "authenticated" policy partitions on the subject claim.
app.UseAuthentication();          // §11.3
app.UseRateLimiter();             // §10.3 — needs the user, precedes policy work
app.UseAuthorization();           // §11.4

// MapReverseProxy()'s own three steps, beneath the one that keeps the edge's deadline a 504 (§9.7).
app.MapReverseProxy(proxy =>
{
    proxy.Use(ProxyDeadline.RethrowAsync);
    proxy.UseSessionAffinity();
    proxy.UseLoadBalancing();
    proxy.UsePassiveHealthChecks();
});

// The edge owns no database and no broker, so its readiness set is empty (§10.1).
app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);   // §13.5 — anonymous; kubelet carries no token

app.Run();
```

**The origin check is one equality with the canonical origin**, not a list of
prohibitions, because `GetRequiredSection` proves only that the section
exists. `GetLeftPart(UriPartial.Authority)` is the canonical origin — scheme,
host, and a port only when it is not the scheme's default — so the parse and
that equality between them reject a missing colon, a trailing slash and a
default port written out, none of which a browser's `Origin` header ever
matches. A blank entry, which binds from `Cors__Origins__0=`, counts as
missing, which is §11.3's rule for `Identity:Authority`; `*` is refused
because it is invalid beside `AllowCredentials`, and ASP.NET Core says so only
when the policy is built, on a preflight rather than at startup. The three
refusals stay three guards rather than one condition because they fail for
different reasons and each message says which.

> **Nothing tests the limiter's place below authentication.**
> `RateLimitedRouteTests`'
> `The_authenticated_policy_gives_each_subject_its_own_bucket` proves two
> authenticated subjects hold independent buckets — the property the subject
> partition key exists for — and it stays green, as does every other test in
> that project, with `UseRateLimiter` moved above `UseAuthentication`. The
> limiter is still live under the reversal, because the anonymous window still
> rejects at its hundredth request; why the authenticated bucket does not
> collapse onto the shared fallback there is unexplained, and an unexplained
> pass is not a guard.
>
> So the row above is two claims of different standing. That the failure is
> **silent** is measured. That the partition **degrades to per-IP** is
> reasoned from the code and is not observed by anything. Keep the order, and
> do not believe a test is holding it.

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

**The emptiness is declared rather than left silent.**
`ownsNoReadinessDependencies: true` is a claim made at the call site, and
without it [§13.5](13-observability.md)'s helper refuses to start the host at
all. The reason is that the probe cannot tell the two cases apart: "nothing of
mine gates readiness" and "readiness was never wired up" both answer 200,
because an empty predicate set is a passing predicate set. The parameter is the
only thing that separates them, and the **default is the failure** — so the
burden falls on the host with nothing to declare rather than on the one that
quietly forgot.
One host passes it — this one, which §13.5 names as the host whose
dependencies do not gate readiness. It does not own *none*: it proxies the
services it routes to. The BFF does not pass it, because its projection is a
schema of its own
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md))
and so a readiness check of its own, which
Catalog's hop is deliberately not part of (§13.5). Every service fails to start
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
shared library used by seven services and the BFF is a coordination point.

`CurrencyMinorUnits` in `Common.Domain` is the one reference table among them
([ADR-067](adr/ADR-067-a-currencys-minor-unit-is-iso-4217s-held-once.md)):
ISO 4217's minor units, which every context that counts money must read alike
and no domain project can reach in `Common.Contracts`. It passes the bound's
test above and is admitted on it; no other model data is.

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
> version named here, compiles with these. Because ADR-019 makes analyser
> output a build gate, that divergence does not show up as a warning — it shows
> up as a build that is green on every machine and red in CI, reproducing
> nowhere.
>
> `disable` is what closes it: the version named is the version used, or
> `dotnet` refuses to run at all rather than quietly choosing another. Feature
> bands are not the exposure — `latestPatch` declines to cross them
> (`10.0.100` rejects `10.0.302`) — patches are, and they are the ones that
> ship analyser changes.
>
> The cost is the intended one: every machine needs this exact patch, not
> merely one in the band, so a bump is a deliberate edit here that everyone
> installs before they can build. That is the same trade as the exact package
> pins below, applied to the compiler that reads them.
>
> **One "machine" cannot install anything, and that makes the pin a two-file
> edit.** The build stage of each service image ([§15.2](15-cicd-deployment.md))
> runs whatever SDK its base tag carries, so those `FROM` lines name this exact
> patch too and a bump here is a bump there in the same change. A floating tag
> such as `10.0-noble` is no substitute: when it moves, `disable` refuses the
> SDK it carries and every image stops building. Loud is the right behaviour
> for a drift nobody chose and the wrong one for the artefact that ships.

`Directory.Packages.props` pins every package version once for the whole
repository. This prevents the situation where two services depend on different
EF Core minor versions and behave differently under identical code. The file
opens with the two properties below, and one `PackageVersion` per package
follows, exact and grouped by `Label`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
```

**Every package means every package**, including the test ones, and the set is
the one [Appendix B](appendix-b-licences.md) registers. The two files answer
different questions about the same dependencies: Appendix B says whether a
licence is acceptable, `Directory.Packages.props` says which version CI will
actually resolve. A package in one and not the other is how a licence
boundary gets crossed by a restore, so
[`.github/licence-gate/`](../../.github/licence-gate/README.md) fails the build
on a pin nobody cleared, and its README owns what it reads to find one, what
it refuses and where its reach stops.

Appendix B is the wider list, though, and three kinds of row in it will never
have a pin here. A check that does not know them reports false positives until
somebody stops reading its output:

- **Infrastructure products** — SQL Server, Redis, RabbitMQ, Keycloak — are
  licensable in their own right but are containers, not packages. The
  `StackExchange.Redis`, `Testcontainers.*` and `AspNetCore.HealthChecks.*` pins
  are the *client libraries* that talk to them: a different artefact under
  a different licence. Match on package identity, never on the product a package
  is named after.
- **The Aspire packages** of [§14.2](14-local-development.md) are deliberately
  unpinned. Aspire is optional, nothing references it until the AppHost is
  adopted, and its API has moved fast enough that pinning a version this
  document cannot keep current would be worse than pinning none. Adopting
  Aspire means adding the pins in the same change — the licence rows already
  exist, so the gap between the two files is the reminder.
- **Either/or rows** — `Shouldly` *or* `AwesomeAssertions` — pin only the chosen
  library. Clearing a licence for an alternative is not a commitment to restore
  it. Keep such rows rare and word them as alternatives, because a row that
  reads as two dependencies when it means one is how this check starts being
  ignored.

Which versions those are is `Directory.Packages.props`'s answer and never this
chapter's. A version with a known vulnerability fails the restore: NuGet's
audit raises NU1903 and NU1904, and
[ADR-019](adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md)
makes a warning a failed build, which is how several of that file's pins were
chosen. Currency, whether a newer version exists, is a separate obligation, and
the tooling for it is not in this repository.

> **Trap — pinning floors instead of versions.** Writing `Version="8.*"`, or
> treating the file as a set of minimums to be "reviewed quarterly", means a
> routine `dotnet restore` can resolve forward across a major boundary. Where
> that boundary is also a **licence** boundary — MassTransit v8 → v9 is the live
> example (Appendix B) — the obligation is acquired by a restore rather than by a
> decision. Pin exact versions and upgrade deliberately.

Licence drift is only caught reliably by tooling — a convention will not survive
the twentieth dependency.

> **Trap — transcribing the pins into this chapter.** A fenced copy of
> `Directory.Packages.props` reads as documentation and behaves as a second
> owner: every pin raise then has to edit a chapter, which
> [`docs/change-locality.md`](../change-locality.md) §2 forbids and Class E's
> row forbids again. The file owns the versions; this chapter owns the rule.

## 4.5 Adding a service

**All seven** of §4.1's services share the shape below — Catalog, Ordering,
Inventory, Payments and Privacy as API hosts, Shipping as a worker and
Notifications as a pure consumer — and writing one by hand is how it ends up
subtly different from the rest. One command renders it instead:

```bash
python tools/new-service/new_service.py Yankee --port 5199
```

The name and the port are a probe rather than a real service, deliberately:
the run refuses a port another service already publishes (below), so a
command naming a real service's port would raise `ScaffoldError` as printed,
and a probe cannot quietly become a service later.

`--worker` renders §4.1's other host shape. The nine projects are the same
nine with `<Name>.Worker` where `<Name>.Api` would be, and what leaves is
the OpenAPI document, the route group and the published port — a worker
consumes from the broker and nothing dials it (§3.2). **Kestrel stays bound
all the same**, because §15.3 separates a worker's chart from Ordering's
by `service.enabled` alone and its probes still address the container port:
§13.5's health endpoint is the one listener a worker has, and the kubelet
reaches it without a Service in front of it. The mode is the rename's rather
than a patch table's: the host's name reaches a project, a namespace, a
Compose service key, a Dockerfile entry point and a test fixture's type, and a
patch can edit a file's text but not its path.

`--pure-consumer` renders §4.1's third shape and implies `--worker`. §4.1
gives such a service no Domain project and §3.2 nothing to publish, so the
render is seven projects with none of §9.4's outbox or §9.3's mapper: no
outbox table or its three migrations, no dispatcher, publisher or gauges and no
`AddMeter` line, no collector and no mapper. §9.5's inbox and the purge over
it, §8.5's marker table, the migrator, the probes and the bus stay. Its
broker account writes its own endpoints and the fault exchanges and no
contract exchange
([ADR-036](adr/ADR-036-the-broker-has-a-per-service-identity.md)). §6.3's
`TransactionBehavior` still calls a domain-event dispatcher, so the render
writes one that stages nothing ([§7.5](07-persistence.md)), and the
service's own architecture gate holds its premise. The mode is a set of
omissions from the one template rather than a second template, so
`--verify` reproduces its commit like any other.

It writes §4.1's five service projects, its three test projects and its
`TestSupport` library — nine in all, seven for a pure consumer, and §4.1 is
explicit that the last is not a test project — with everything the service
template has accumulated: the
`DbContext` and its conventions
([§7.2](07-persistence.md)), `EfUnitOfWork` ([§6.3](06-cqrs.md)), the
connection factory ([§6.5](06-cqrs.md)), the readiness checks
([§13.5](13-observability.md)) — SQL registered by the service, the bus's
`masstransit-bus` by MassTransit itself, and a rendered service that lost them
would fail to start rather than report ready, because
`MapCommonHealthEndpoints` refuses an empty readiness set unless the host
declares it owns none (§4.2) — the bus registration of
[§9](09-messaging.md), whose eager read means a scaffolded host refuses to
start without `ConnectionStrings:RabbitMq`, the migration job host
([§7.4](07-persistence.md)), the `InitialCreate` migration that creates the
schema and, for a publishing service, the `AddOutbox` one beside it —
§9.4's table is wiring every publishing service has, and a service carrying
the dispatcher without it would log a failed claim twice a second from its
first boot — the outbox itself with its empty allow-list mapper (a pure
consumer has none of the three, above), §9.5's inbox filter and retention
purge, §11.3's JWT validation, both images ([§15.2](15-cicd-deployment.md)) and
§4.2's architecture gates. The migrations it copies are `InitialCreate`,
`AddOutbox`, `AddInbox`, `AddOutboxRetentionIndex`, `AddIdempotencyMarkers`,
`IdempotencyMarkerCommittedAtDefault`, `AddIdempotencyMarkerRowVersion` and
`AddOutboxTraceContext`, bar the three outbox ones for a pure consumer —
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
**`AddIdempotencyMarkerRowVersion` adds that table's `rowversion` and travels
on the sharpest version of the argument**: `RetentionPurgeService` names
the column in both of its marker statements, so a service scaffolded without
the migration fails its own purge with `Invalid column name 'RowVersion'` on
the first pass
([ADR-041](adr/ADR-041-the-markers-delete-identifies-a-row-by-a-rowversion-not-a-timestamp.md)).
`AddOutboxTraceContext` travels on the same argument one table over: the
outbox dispatcher's claim reads both trace columns ([§9.4](09-messaging.md)),
so a publishing service without them fails its first claim.
It then edits the shared files: `Platform.slnx`, the Compose index — one
`include:` line for the unit it just created — the `infra-only` override, which
excludes both halves of the pair, `.env.example`
([§14.1](14-local-development.md)), the broker definitions that grant the new
service an account of its own — without which it renders a service that starts
and cannot authenticate, since the broker holds no shared principal
([ADR-036](adr/ADR-036-the-broker-has-a-per-service-identity.md)) —
the `AddMeter` line in `Common.Web` for a publishing service's outbox
meter, without which §13.6's gauges are published and collected by nothing,
since §13.2's export names meters one by one — and, under
`.github/secret-scan/allowed/`, the file covering each
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
builds and its **122** tests pass before a line of it is written, **47** of
them against real SQL Server and RabbitMQ containers — counts measured on
2026-10-09 against a rendered service, whose `Yankee.Domain.Tests`,
`Yankee.Application.Tests` and `Yankee.Api.Tests` hold 1, 18 and 103 of them.
The 47 is the `Category=Integration` count of §12.4, which is a filter rather
than a tally.

**Arithmetic is not a remeasurement.** The total need not move by the number of
tests a change adds to the template, so whoever changes what the scaffold copies
renders `Yankee` and runs it rather than adding to the number here, and renders
again after the last commit that changes the template, because a figure taken
before it is stale by the next one. **A figure nobody recounts goes stale on the
next PR's clock, not on its own**, and this is the only figure in this section a
reader cannot check from the tree, so it is the one to distrust first — a number
three PRs stale looks exactly like a number taken yesterday.

**There is no template directory, and that is the design.** The script reads
`src/Services/Catalog` at run time, so there is exactly one copy of the
wiring — the copy CI builds and `dotnet test` exercises — and an improvement to
the template reaches the next service the next time it runs. A tokenised copy
beside it would be a second `DbContext`, a second migrator host and a second
Dockerfile that nothing builds and nothing reconciles.

**It copies no domain.** Catalog's `Product`, its command, its query and its
endpoints are excluded by name; what a new service inherits is Catalog's
wiring, not Catalog's slice with the nouns changed.
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

`--port` is required for an API render, refused for a worker, and never
derived. A port is an allocation recorded in the service's own Compose unit;
a script that guessed one would quietly disagree with a printed chapter. The
run refuses a port another service already publishes.

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

**Two** things are outside it, and neither is silently missing: the gateway
route ([§10.2](10-api-gateway.md)) — the route belongs to the gateway's
configuration, not the service's tree — and the Helm chart
([§15.3](15-cicd-deployment.md)).

**The chart's exclusion has a cost**: a scaffolded service compiles, tests,
starts under Compose — and cannot be deployed, with nothing in the render
saying so. That is a gap in the scaffold rather than a contradiction here, and
it is **owed**, and the shape of what is owed is the shape §15.3 already
describes: a `Chart.yaml`, one one-line include per template the library chart
defines, and a `values.yaml`. Only the last is real work, and it is real work —
it carries every per-service decision, which is precisely what a template
cannot guess and what §15.3 spends a section arguing.

**No file count here, deliberately.** The count moves whenever the library
gains a template, so a chart emitted to a remembered count would not deploy;
the thing worth writing down is the rule, not the arithmetic.

**The scaffold refuses `Shipping` without `--worker` and `Notifications`
without `--pure-consumer`.** A note is not a guard: without the refusal the
script would render either in a shape §4.1 does not give it, and contradict
the chapter quietly.

---

[← §3 Bounded contexts](03-bounded-contexts.md) · [Index](README.md) · [§5 Tactical DDD →](05-tactical-ddd.md)

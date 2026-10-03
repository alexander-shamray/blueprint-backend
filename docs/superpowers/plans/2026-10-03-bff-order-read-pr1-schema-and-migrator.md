# BFF order read PR-1 — the order projection's schema and migrator — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Web.Bff` the schema ADR-051's projection lives in, and nothing
that writes it yet. `src/BFF/Web.Bff.Persistence` holds `BffDbContext`, the
three tables of the spec's section 5 and the inbox, and the first migration
`AddOrderProjection`; `src/BFF/Web.Bff.Migrator` is the §7.4 job host every
service ships. The host registers the context, the inbox purge and a SQL
readiness check, refuses to start without `ConnectionStrings:Bff`, and drops
`ownsNoReadinessDependencies`. `RetentionPurgeService` takes its idempotency
half as optional, both or neither. The suite gains a `ServiceFixture` under
the name `Bff`; Compose gains the migrator pair; CI builds the
`web-bff-migrator` image; the chart turns on its database and its migration
Job, because a chart that deploys this host without a database deploys a pod
that does not start; and the chapters ADR-051 lists as describing a BFF with
no schema are amended where this PR makes them false.

**Architecture:** the persistence project is a class library holding the
schema and nothing a web host carries — the migrator references it, the host
references it, and neither references the other. Writes and reads arrive in
PR-2 and PR-3 as Dapper over `IDbConnectionFactory`, so EF here defines the
schema and the inbox row the filter stages, and nothing else; the row types
have no behaviour. Every `bff.Orders` column but the key and the two BFF
instants is nullable, because any of the eight events can create a row.
Four check constraints hold the pairs one handler writes together — a total
and its currency, a cancellation and its member, an authorisation and its
amount, a refund and its amount; a fifth holds `CancelOutcome` to §10.7's three members, which
`CancelOutcomes` names once; and a sixth holds `PaymentCurrency` present
exactly when either amount is, so no amount is stored without the currency
that labels it and no currency without an amount to label. The host's
registration is one extension, `AddBffPersistence`, in `Web.Bff`, which is
the composition root's only caller of it.

**Tech Stack:** .NET at `global.json`'s pin, EF Core with SQL Server (already
pinned), `AspNetCore.HealthChecks.SqlServer` (already pinned), xUnit v3 with
Shouldly and Testcontainers through `Common.TestSupport`, Helm 3 at
`helm.yml`'s pin, stdlib Python 3.12 for the gates.

**Spec:** `docs/superpowers/specs/2026-10-03-bff-order-read-design.md`,
sections 1 (where the code lives, the names, the rows, readiness, retention),
3 (`RetentionPurgeService`'s optional half, `BffFactory`'s placeholder keys
and the suite's `ServiceFixture`), 4 (PR-1's row, and why CI joins it), 5
(the three tables, the nullable columns, the read's index, the constraints,
the widths, `AddOrderProjection`), 9 (PR-1's keys, the Compose pair), 11 (the
SQL Server half of the testing list that PR-1 can hold) and 12 (the rows
PR-1 takes).

**The chart's database half rides this PR**, as the spec's section 4 argues:
`deploy/helm/smoke.sh` reads `src/BFF/Web.Bff` and fails a chart that names
no connection the code resolves, and a release built from `main` would
otherwise start a pod that refuses to start. So do §14.2's database half,
§15.3's sentence and `docs/repo-map.md`'s BFF entry (spec, section 12); the
broker half and the `consume` signal are PR-2's.

**Not run before it was written.** Every anchor quoted below was read from
the tree at `cc453404`. The code blocks are written against that tree and
have not been compiled; each task's run steps are the first proof, and a
divergence they find is fixed in the task, not carried.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set:

  `src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs`, `tests/Common.Infrastructure.Tests/**`, `tests/Common.TestSupport/ServiceFixture.cs`, `src/BFF/**`, `tests/Web.Bff.Tests/**`, `Platform.slnx`, `.github/workflows/ci.yml`, `.github/secret-scan/allowed/deploy.txt`, `.github/secret-scan/allowed/docs.txt`, `deploy/compose/services/web-bff.yml`, `deploy/compose/docker-compose.infra-only.yml`, `deploy/compose/.env.example`, `deploy/compose/README.md`, `deploy/helm/web-bff/**`, `deploy/helm/smoke.sh`, `deploy/canary/deployables/web-bff.json`, `deploy/canary/canary.py`, `deploy/canary/test_canary.py`, `docs/repo-map.md`, `docs/backend-architecture/04-solution-structure.md`, `docs/backend-architecture/09-messaging.md`, `docs/backend-architecture/13-observability.md`, `docs/backend-architecture/14-local-development.md`, `docs/backend-architecture/15-cicd-deployment.md`

  Why each, since the row is paths only. **A**: one building block,
  `Common.Infrastructure`'s purge, with its suite and the shared fixture's
  two direct constructions of it; the BFF's own tree and its one suite. **D**:
  the workflow's image leg; the two allow-list files the secret scan reads,
  because every connection default this PR prints is a finding with a
  fingerprint; the Compose unit, the override that excludes the new migrator,
  `.env.example`'s commented keys and the README's host-run recipe; the chart,
  `smoke.sh`'s one comment that names the BFF as databaseless, the
  descriptor, `canary.py`'s docstring and the one test that asserts the BFF
  renders no Job; the repo map's BFF entry; and the chapters the spec's
  section 12 gives this PR — the sentences it makes false, and §15.3's one
  new paragraph. **E**: `Platform.slnx` and the `*.csproj`
  files, which sit under `src/BFF/**` and `tests/Web.Bff.Tests/**` already.
- **Mutexes held**: `Platform.slnx`, `deploy/compose/docker-compose.infra-only.yml`
  and `tests/Common.TestSupport/ServiceFixture.cs` from the repo-wide list;
  the BFF's `Program.cs` and `Web.Bff.csproj`, its composition root and its
  project file. `Directory.Packages.props` is **not** touched: every package
  named below is pinned today, so no Appendix B row moves.
- **Depends on nothing earlier.** PR-2 to PR-5 consume the names *Interfaces
  for PR-2* lists at the end of this plan.
- Comments say why and cite the owner; no history, no PR, no test named. The
  comment gate's `BLOCK_LIMIT` is 5, a touched block counts whole, and a file
  the branch creates is all added lines. A summary is one sentence, a
  `<remarks>` cited and four lines. Prose at 80 columns, code at 120, British
  spelling, explicit local types, file-scoped namespaces, braces on two
  statements or more, one space before `=`, `=>` and `{`.
- **No literal credential in this plan.** The secret scan reads
  `docs/superpowers/`, so where a step needs Compose's local SQL default it
  names the line to copy rather than printing it, and Task 5 and Task 8 add
  the allow entries the copies produce.
- `py -3.12`, never `python`. Container tests are
  `[Collection(nameof(BffIntegrationCollection))]` and never skipped.
- Every step that adds behaviour writes its test first, and a test whose
  subject is a refusal asserts the refusal's own message, so a different
  failure cannot pass it.
- Branch: `feat/bff-order-projection-schema`.

---

### Task 1: The purge takes its idempotency half as optional

**Files:**
- Modify: `src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs`
- Modify: `tests/Common.Infrastructure.Tests/RetentionPurgeServiceTests.cs`
- Modify: `tests/Common.TestSupport/ServiceFixture.cs` — the two direct
  constructions
- Modify: `docs/backend-architecture/09-messaging.md` — §9.5, one sentence

**Interfaces:**
- Changes: `RetentionPurgeService(IServiceScopeFactory scopes, InboxTable
  inbox, RetentionPolicy policy, ILogger<RetentionPurgeService> log,
  OutboxTable? outbox = null, IdempotencyMarkerTable? markers = null,
  IIdempotencyStore? claims = null)`. The marker table and the claim store
  move behind the outbox because only trailing parameters can be optional,
  and the container resolves an unregistered optional parameter to its
  default — the mechanism the outbox half already relies on. One supplied
  without the other throws `InvalidOperationException` naming both halves.
  `PurgeAsync` keeps `(int Outbox, int Inbox, int Idempotency)` and reports
  `Idempotency: 0` when there is no marker half.

The constructor today takes `IdempotencyMarkerTable` and `IIdempotencyStore`
unconditionally, so a host with no command pipeline would have to register a
marker table nothing writes, and a claim store nothing claims in, to satisfy
`ValidateOnBuild`.

- [ ] **Step 1: Write the failing tests**

In `tests/Common.Infrastructure.Tests/RetentionPurgeServiceTests.cs`, widen
the `Registrations` helper and add three tests. The helper:

```csharp
    private static ServiceCollection Registrations(
        List<string> statements,
        bool withOutbox,
        bool withMarkers = true,
        bool withClaims = true)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new InboxTable("probe"));
        services.AddSingleton(new RetentionPolicy());
        services.AddSingleton<RetentionPurgeService>();
        services.AddSingleton(RecordingFactory(statements));

        if (withOutbox)
            services.AddSingleton(new OutboxTable("probe"));

        if (withMarkers)
            services.AddSingleton(new IdempotencyMarkerTable("probe"));

        if (withClaims)
            services.AddSingleton(Substitute.For<IIdempotencyStore>());

        return services;
    }
```

The tests, after `It_still_purges_the_outbox_when_the_service_registers_the_table`:

```csharp
    [Fact]
    public async Task It_composes_no_marker_statement_for_a_host_that_registers_neither_half()
    {
        // ValidateOnBuild first, as every host builds: a host with no command pipeline writes no marker (§9.5).
        List<string> statements = [];
        using ServiceProvider provider = Registrations(statements, withOutbox: false, withMarkers: false,
            withClaims: false).BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        (int outbox, int inbox, int idempotency) =
            await provider.GetRequiredService<RetentionPurgeService>().PurgeAsync(CancellationToken.None);

        outbox.ShouldBe(0);
        inbox.ShouldBe(0);
        idempotency.ShouldBe(0);
        statements.ShouldNotBeEmpty();
        statements.ShouldNotContain(sql => sql.Contains("CommittedAt", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void It_refuses_one_idempotency_half_without_the_other(bool withMarkers, bool withClaims)
    {
        using ServiceProvider provider =
            Registrations([], withOutbox: false, withMarkers, withClaims).BuildServiceProvider();

        // The message, not the type: the container's own missing-service error is an InvalidOperationException too.
        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredService<RetentionPurgeService>());

        thrown.Message.ShouldContain("both halves");
    }
```

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet test tests/Common.Infrastructure.Tests --filter "FullyQualifiedName~RetentionPurgeServiceTests"
```

Expected: the neither-half test fails building the provider with
`Unable to resolve service for type 'Common.Infrastructure.Idempotency.IdempotencyMarkerTable'`;
the theory's `withMarkers: false` row fails the same way and the
`withClaims: false` row fails on `IIdempotencyStore`, so neither message
contains "both halves". The three existing tests pass.

- [ ] **Step 3: Make the marker half optional**

In `RetentionPurgeService.cs`, replace the field block from `private readonly
IIdempotencyStore _claims;` through `private readonly string _markerTable;`
and the constructor with:

```csharp
    private readonly IServiceScopeFactory _scopes;
    private readonly RetentionPolicy _policy;
    private readonly ILogger<RetentionPurgeService> _log;

    // Null when no OutboxTable is registered, as for §4.1's pure consumer (§9.5).
    private readonly string? _outboxSql;
    private readonly string _inboxSql;

    // Null when neither marker half is registered, as for a host with no command pipeline (§9.5).
    private readonly MarkerHalf? _markers;

    public RetentionPurgeService(
        IServiceScopeFactory scopes,
        InboxTable inbox,
        RetentionPolicy policy,
        ILogger<RetentionPurgeService> log,
        OutboxTable? outbox = null,
        IdempotencyMarkerTable? markers = null,
        IIdempotencyStore? claims = null)
    {
        // A marker table with no store to ask could only be purged by guessing whether its claim is gone (ADR-039).
        if ((markers is null) != (claims is null))
        {
            string given = markers is null ? nameof(IIdempotencyStore) : nameof(IdempotencyMarkerTable);
            string missing = markers is null ? nameof(IdempotencyMarkerTable) : nameof(IIdempotencyStore);

            throw new InvalidOperationException(
                $"{nameof(RetentionPurgeService)} was given an {given} and no {missing}. The marker purge needs " +
                "both halves (§9.5, ADR-039): register both for a host that runs commands, or neither for one that " +
                "runs none.");
        }

        _scopes = scopes;
        _policy = policy;
        _log = log;

        // ProcessedAt IS NOT NULL keeps the abandoned rows §13.6's alert surfaces.
        _outboxSql = outbox is null
            ? null
            : $"""
              DELETE TOP (@BatchSize) FROM {outbox.QualifiedName}
              WHERE ProcessedAt IS NOT NULL
                  AND ProcessedAt < @Before;
              """;

        // Age alone: an inbox row has no unfinished state, and the window outlasts redelivery (§9.5).
        _inboxSql =
            $"""
            DELETE TOP (@BatchSize) FROM {inbox.QualifiedName}
            WHERE HandledAt < @Before;
            """;

        // Candidates only: the store decides (ADR-039), and the cutoff is on the database's clock (ADR-038).
        // Oldest first, so the rows likeliest still claimed sit at the tail where a pass stops.
        _markers = markers is null
            ? null
            : new MarkerHalf(
                $"""
                SELECT TOP (@BatchSize) [Key], {IdempotencyMarker.RowVersionColumn}
                FROM {markers.QualifiedName}
                WHERE CommittedAt < DATEADD(second, -@WindowSeconds, SYSDATETIMEOFFSET())
                ORDER BY CommittedAt;
                """,
                markers.QualifiedName,
                claims!);
    }
```

The `claims!` is the one null-forgiveness, and the guard above it is what
makes it true; `markers is null` and `claims is null` agree past it.

In `PurgeAsync`, replace the three lines from `// No \`now\`: the markers' cutoff`
through `Purged(_log, idempotency, "idempotency", null);` with:

```csharp
        // No `now`: the markers' cutoff is the database's clock (ADR-038).
        int idempotency = 0;
        if (_markers is not null)
        {
            idempotency = await PurgeMarkersAsync(_markers, connection, ct);
            Purged(_log, idempotency, "idempotency", null);
        }
```

Give `PurgeMarkersAsync`, `DeleteRowsAsync` and `DeleteSql` the half they
read rather than the fields, so nothing reaches a marker field that may be
null:

```csharp
    private async Task<int> PurgeMarkersAsync(MarkerHalf markers, IDbConnection connection, CancellationToken ct)
```

with `_idempotencyCandidateSql` read as `markers.CandidateSql` and
`_claims.UnheldAsync` as `markers.Claims.UnheldAsync`; `DeleteRowsAsync(markers.Table,
connection, [...], ct)` passing the table through to `DeleteSql(table,
chunk.Length)`, which becomes `private static string DeleteSql(string table,
int rows)` with `{_markerTable}` read as `{table}`. Then, beside
`MarkerCandidate` at the foot of the class:

```csharp
    /// <summary>The marker purge's two statements' table and the store that decides between them (ADR-039).</summary>
    private sealed record MarkerHalf(string CandidateSql, string Table, IIdempotencyStore Claims);
```

- [ ] **Step 4: Move the shared fixture's two constructions to named arguments**

In `tests/Common.TestSupport/ServiceFixture.cs`, `PurgeWithAsync(RetentionPolicy
policy, IIdempotencyStore claims)` constructs:

```csharp
        RetentionPurgeService purge = new(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Factory.Services.GetRequiredService<InboxTable>(),
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>(),
            outbox: Factory.Services.GetService<OutboxTable>(),
            markers: Factory.Services.GetRequiredService<IdempotencyMarkerTable>(),
            claims: claims);
```

— `GetRequiredService` for the marker table, because the overload exists to
substitute the claim store between select and delete and is meaningless for
a host with no markers. `PurgeWithSkewedClockAsync` constructs:

```csharp
        RetentionPurgeService purge = new(
            new SkewedScopeFactory(
                Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                new SkewedClock(skew)),
            Factory.Services.GetRequiredService<InboxTable>(),
            policy,
            Factory.Services.GetRequiredService<ILogger<RetentionPurgeService>>(),
            outbox: Factory.Services.GetService<OutboxTable>(),
            markers: Factory.Services.GetService<IdempotencyMarkerTable>(),
            claims: Factory.Services.GetService<IIdempotencyStore>());
```

- [ ] **Step 5: Run them green, and the services that construct through the fixture**

```bash
dotnet test tests/Common.Infrastructure.Tests --filter "FullyQualifiedName~RetentionPurgeServiceTests"
dotnet build Platform.slnx
dotnet test tests/Catalog.Api.Tests --filter "FullyQualifiedName~RetentionPurgeTests|FullyQualifiedName~IdempotencyMarkerTests"
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~RetentionPurgeTests|FullyQualifiedName~IdempotencyMarkerTests"
```

Expected: the five unit tests pass; the solution builds with 0 warnings; the
two services' purge and marker suites, which construct through the fixture's
two methods and resolve the hosted purge from DI, pass unchanged. The last
two need Docker.

- [ ] **Step 6: Amend §9.5**

In `docs/backend-architecture/09-messaging.md`, the *It purges a third table*
paragraph ends:

```
What the floor bounds is how long the guarantee lasts rather than whether it
holds: keeping the marker alive while the claim is, is the purge's job rather
than the window's, and §8.5 owns that argument.
```

Append to it:

```
What the floor bounds is how long the guarantee lasts rather than whether it
holds: keeping the marker alive while the claim is, is the purge's job rather
than the window's, and §8.5 owns that argument. A host that runs no command
pipeline writes no marker — the BFF, whose projection only consumers write
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md))
— so it registers neither the marker table nor the claim store, and is given
no marker statement. One registered without the other is refused when the
service is built, because a marker the pass cannot ask about is one it could
delete only by guessing.
```

- [ ] **Step 7: Commit**

```bash
git add src/BuildingBlocks/Common.Infrastructure/Messaging/RetentionPurgeService.cs \
        tests/Common.Infrastructure.Tests/RetentionPurgeServiceTests.cs \
        tests/Common.TestSupport/ServiceFixture.cs \
        docs/backend-architecture/09-messaging.md
git commit -m "feat(infra): RetentionPurgeService takes its idempotency half as optional, both or neither"
```

The body argues: a host with no command pipeline would otherwise register a
marker table nothing writes; both-or-neither because the pass needs the
store to decide (ADR-039); the parameters move last because only trailing
ones can be optional.

---

### Task 2: `Web.Bff.Persistence` — the context, the rows and their configuration

**Files:**
- Create: `src/BFF/Web.Bff.Persistence/Web.Bff.Persistence.csproj`
- Create: `src/BFF/Web.Bff.Persistence/BffSchema.cs`
- Create: `src/BFF/Web.Bff.Persistence/ProjectionLimits.cs`
- Create: `src/BFF/Web.Bff.Persistence/CancelOutcomes.cs`
- Create: `src/BFF/Web.Bff.Persistence/OrderRow.cs`
- Create: `src/BFF/Web.Bff.Persistence/OrderLineRow.cs`
- Create: `src/BFF/Web.Bff.Persistence/ProductRow.cs`
- Create: `src/BFF/Web.Bff.Persistence/BffDbContext.cs`
- Create: `src/BFF/Web.Bff.Persistence/Configurations/OrderRowConfiguration.cs`
- Create: `src/BFF/Web.Bff.Persistence/Configurations/OrderLineRowConfiguration.cs`
- Create: `src/BFF/Web.Bff.Persistence/Configurations/ProductRowConfiguration.cs`
- Create: `src/BFF/Web.Bff.Persistence/Configurations/InboxMessageConfiguration.cs`
- Create: `tests/Web.Bff.Tests/ProjectionModelTests.cs`
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`
- Modify: `Platform.slnx`

**Interfaces:**
- Produces: `Web.Bff.Persistence.BffDbContext` with `Orders`, `OrderLines`,
  `Products` and `InboxMessages`; `BffSchema.Name` (`"bff"`);
  `ProjectionLimits.CurrencyLength` (3), `ProductNameMaxLength` (200),
  `TrackingNumberMaxLength` (64), `CancelOutcomeMaxLength` (16);
  `CancelOutcomes.Cancelled`, `OutOfStock`, `Declined`.

The widths cite their owners rather than referencing them: §4.2 lets the BFF
reach no service project, so Catalog's name width and Shipping's tracking
width are copied as constants whose summaries say whose they are. A wider
wire value is PR-2's refusal to make, against these constants.

- [ ] **Step 1: Write the failing model test**

`tests/Web.Bff.Tests/ProjectionModelTests.cs`:

```csharp
using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Web.Bff.Persistence;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's schema as the design-time model holds it, which builds with no server to reach.</summary>
public sealed class ProjectionModelTests
{
    [Fact]
    public void Every_table_is_in_the_bff_schema()
    {
        using BffDbContext db = Context();
        IModel model = db.GetService<IDesignTimeModel>().Model;

        string[] tables = [.. model.GetEntityTypes().Select(e => $"{e.GetSchema()}.{e.GetTableName()}")];

        tables.ShouldBe(["bff.InboxMessages", "bff.OrderLines", "bff.Orders", "bff.Products"], ignoreOrder: true);
    }

    [Fact]
    public void Any_event_can_create_an_order_row_so_only_the_key_and_the_two_instants_are_required()
    {
        using BffDbContext db = Context();
        IEntityType orders = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!;

        string[] required = [.. orders.GetProperties().Where(p => !p.IsNullable).Select(p => p.Name)];

        required.ShouldBe([nameof(OrderRow.OrderId), nameof(OrderRow.FirstSeenAt), nameof(OrderRow.AsOf)],
            ignoreOrder: true);
    }

    [Fact]
    public void The_reads_index_seeks_owned_rows_newest_first()
    {
        using BffDbContext db = Context();
        IIndex owned = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_Orders_Owned");

        owned.Properties.Select(p => p.Name).ShouldBe(
            [nameof(OrderRow.CustomerId), nameof(OrderRow.FirstSeenAt), nameof(OrderRow.OrderId)]);
        owned.IsDescending.ShouldBe([false, true, true]);
        owned.GetFilter().ShouldBe("[CustomerId] IS NOT NULL");
    }

    [Fact]
    public void The_pairs_one_handler_writes_together_the_cancel_member_and_the_payment_currency_are_constrained()
    {
        using BffDbContext db = Context();
        IEntityType orders = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!;

        orders.GetCheckConstraints().Select(c => c.ModelName).ShouldBe(
            [
                "CK_Orders_Authorisation", "CK_Orders_CancelOutcome", "CK_Orders_Cancellation",
                "CK_Orders_PaymentCurrency", "CK_Orders_Refund", "CK_Orders_Total"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Both_currencies_are_bounded_to_an_iso_code()
    {
        using BffDbContext db = Context();
        IEntityType orders = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!;

        orders.FindProperty(nameof(OrderRow.Currency))!.GetMaxLength().ShouldBe(ProjectionLimits.CurrencyLength);
        orders.FindProperty(nameof(OrderRow.PaymentCurrency))!.GetMaxLength()
            .ShouldBe(ProjectionLimits.CurrencyLength);
    }

    [Fact]
    public void A_line_is_keyed_by_its_position_and_goes_with_its_order()
    {
        using BffDbContext db = Context();
        IEntityType lines = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderLineRow))!;

        lines.FindPrimaryKey()!.Properties.Select(p => p.Name).ShouldBe(
            [nameof(OrderLineRow.OrderId), nameof(OrderLineRow.LineNumber)]);

        IForeignKey order = lines.GetForeignKeys().Single();
        order.PrincipalEntityType.ClrType.ShouldBe(typeof(OrderRow));
        order.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
    }

    [Fact]
    public void The_inbox_is_keyed_on_message_and_endpoint()
    {
        using BffDbContext db = Context();
        IEntityType inbox = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(InboxMessage))!;

        inbox.FindPrimaryKey()!.Properties.Select(p => p.Name).ShouldBe(
            [nameof(InboxMessage.MessageId), nameof(InboxMessage.Endpoint)]);
    }

    // The provider is named so the model is SQL Server's; nothing connects to build it.
    private static BffDbContext Context() =>
        new(new DbContextOptionsBuilder<BffDbContext>().UseSqlServer("Server=model-only.invalid").Options);
}
```

Add to `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, in the package group:

```xml
    <!-- UseSqlServer, named by the model and schema tests though Web.Bff.Persistence carries it. -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
```

and in the project group:

```xml
    <!-- ADR-051's schema, which the model and schema tests read and the fixture migrates. -->
    <ProjectReference Include="..\..\src\BFF\Web.Bff.Persistence\Web.Bff.Persistence.csproj" />
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet build tests/Web.Bff.Tests
```

Expected: the build fails — `Web.Bff.Persistence.csproj` does not exist, and
`BffDbContext`, `OrderRow` and `OrderLineRow` do not resolve.

- [ ] **Step 3: The project**

`src/BFF/Web.Bff.Persistence/Web.Bff.Persistence.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- ADR-051's schema and its migrations, and nothing a web host carries, so the migrator need not reference one. -->

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
  </ItemGroup>

  <ItemGroup>
    <!-- §9.5's InboxMessage, the one row here the BFF does not define. -->
    <ProjectReference Include="..\..\BuildingBlocks\Common.Infrastructure\Common.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

`src/BFF/Web.Bff.Persistence/BffSchema.cs`:

```csharp
namespace Web.Bff.Persistence;

/// <summary>The BFF's schema, named once for the context, the inbox table and every statement against it.</summary>
public static class BffSchema
{
    public const string Name = "bff";
}
```

`src/BFF/Web.Bff.Persistence/ProjectionLimits.cs`:

```csharp
namespace Web.Bff.Persistence;

/// <summary>The projection's column widths, read by the configurations and by the handlers' refusals.</summary>
public static class ProjectionLimits
{
    /// <summary>ISO 4217's code length, as every service's money column holds it.</summary>
    public const int CurrencyLength = 3;

    /// <summary>Catalog's product-name column width, copied because §4.2 lets the BFF reach no Catalog type.</summary>
    public const int ProductNameMaxLength = 200;

    /// <summary><c>ShipmentLimits.MaxTrackingNumberLength</c>'s value, copied for the same reason.</summary>
    public const int TrackingNumberMaxLength = 64;

    /// <summary>Room for the longest of <see cref="CancelOutcomes"/>' members.</summary>
    public const int CancelOutcomeMaxLength = 16;
}
```

`src/BFF/Web.Bff.Persistence/CancelOutcomes.cs`:

```csharp
namespace Web.Bff.Persistence;

/// <summary>§10.7's three members an <c>OrderCancelled</c> decides between, held by a check constraint.</summary>
public static class CancelOutcomes
{
    public const string Cancelled = "cancelled";

    public const string OutOfStock = "out_of_stock";

    public const string Declined = "declined";
}
```

`src/BFF/Web.Bff.Persistence/OrderRow.cs`:

```csharp
namespace Web.Bff.Persistence;

/// <summary>One order as the projection knows it: a column per fact §10.7 returns, each set once (ADR-051).</summary>
/// <remarks>
/// Written and read through SQL, never through this type, which exists to define the table (§7.2). Every column
/// but the key and the two BFF instants is nullable, because any of the eight events can create the row.
/// </remarks>
public sealed class OrderRow
{
    public Guid OrderId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public string? Currency { get; private set; }

    public decimal? TotalAmount { get; private set; }

    public DateTimeOffset? PlacedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelOutcome { get; private set; }

    public DateTimeOffset? AuthorisedAt { get; private set; }

    public decimal? AuthorisedAmount { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }

    public decimal? RefundedAmount { get; private set; }

    /// <summary>The payment events' currency, labelling both amounts; one may precede <c>Currency</c>.</summary>
    public string? PaymentCurrency { get; private set; }

    public string? TrackingNumber { get; private set; }

    /// <summary>The BFF's clock at insert, which orders the list and never changes.</summary>
    public DateTimeOffset FirstSeenAt { get; private set; }

    /// <summary>The BFF's clock at the row's last write, which both routes return.</summary>
    public DateTimeOffset AsOf { get; private set; }
}
```

`src/BFF/Web.Bff.Persistence/OrderLineRow.cs`:

```csharp
namespace Web.Bff.Persistence;

/// <summary>One line an order was placed with, keyed by its position in the event's list (ADR-051).</summary>
public sealed class OrderLineRow
{
    public Guid OrderId { get; private set; }

    public int LineNumber { get; private set; }

    public Guid ProductId { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }
}
```

`src/BFF/Web.Bff.Persistence/ProductRow.cs`:

```csharp
namespace Web.Bff.Persistence;

/// <summary>A product's name as Catalog published it, joined on read so a line never snapshots one (§10.7).</summary>
public sealed class ProductRow
{
    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset PublishedAt { get; private set; }
}
```

`src/BFF/Web.Bff.Persistence/BffDbContext.cs`:

```csharp
using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;

namespace Web.Bff.Persistence;

/// <summary>ADR-051's projection schema, which the migrator applies and the host's SQL reads and writes.</summary>
public sealed class BffDbContext(DbContextOptions<BffDbContext> options) : DbContext(options)
{
    /// <summary>One row per order the projection has heard of.</summary>
    public DbSet<OrderRow> Orders => Set<OrderRow>();

    /// <summary>The lines each order was placed with.</summary>
    public DbSet<OrderLineRow> OrderLines => Set<OrderLineRow>();

    /// <summary>The product names Catalog published.</summary>
    public DbSet<ProductRow> Products => Set<ProductRow>();

    /// <summary>§9.5's inbox.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(BffSchema.Name);

        // §7.2 puts mapping in these classes, never in attributes.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BffDbContext).Assembly);
    }

    /// <summary>§7.2's global conventions, which govern every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}
```

`src/BFF/Web.Bff.Persistence/Configurations/OrderRowConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>Orders</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class OrderRowConfiguration : IEntityTypeConfiguration<OrderRow>
{
    public void Configure(EntityTypeBuilder<OrderRow> builder)
    {
        builder.ToTable("Orders", BffSchema.Name, table =>
        {
            // Each pair is written by one handler in one statement, so half of one is a defect to refuse.
            table.HasCheckConstraint("CK_Orders_Total", Together("Currency", "TotalAmount"));
            table.HasCheckConstraint("CK_Orders_Cancellation", Together("CancelledAt", "CancelOutcome"));
            table.HasCheckConstraint("CK_Orders_Authorisation", Together("AuthorisedAt", "AuthorisedAmount"));
            table.HasCheckConstraint("CK_Orders_Refund", Together("RefundedAt", "RefundedAmount"));

            // An amount is a number the client cannot render without its currency, and the currency is written by
            // the same statement as the first amount, so it is present exactly when either amount is (§10.7).
            table.HasCheckConstraint(
                "CK_Orders_PaymentCurrency",
                "([PaymentCurrency] IS NULL AND [AuthorisedAmount] IS NULL AND [RefundedAmount] IS NULL) OR " +
                "([PaymentCurrency] IS NOT NULL AND ([AuthorisedAmount] IS NOT NULL OR [RefundedAmount] IS NOT NULL))");

            // §10.7's closed vocabulary; a NULL passes, as a CHECK on an unknown does.
            table.HasCheckConstraint(
                "CK_Orders_CancelOutcome",
                $"[CancelOutcome] IN (N'{CancelOutcomes.Cancelled}', N'{CancelOutcomes.OutOfStock}', " +
                $"N'{CancelOutcomes.Declined}')");
        });

        // The order's own id, where every handler's MERGE meets: two first arrivals cannot both insert.
        builder.HasKey(o => o.OrderId);
        builder.Property(o => o.OrderId).ValueGeneratedNever();

        builder.Property(o => o.Currency).HasMaxLength(ProjectionLimits.CurrencyLength);
        builder.Property(o => o.PaymentCurrency).HasMaxLength(ProjectionLimits.CurrencyLength);
        builder.Property(o => o.CancelOutcome).HasMaxLength(ProjectionLimits.CancelOutcomeMaxLength);
        builder.Property(o => o.TrackingNumber).HasMaxLength(ProjectionLimits.TrackingNumberMaxLength);

        // The list's keyset seek (§10.7), filtered so it never reads a row with no owner.
        builder
            .HasIndex(o => new { o.CustomerId, o.FirstSeenAt, o.OrderId })
            .HasDatabaseName("IX_Orders_Owned")
            .IsDescending(false, true, true)
            .HasFilter("[CustomerId] IS NOT NULL");
    }

    private static string Together(string first, string second) =>
        $"([{first}] IS NULL AND [{second}] IS NULL) OR ([{first}] IS NOT NULL AND [{second}] IS NOT NULL)";
}
```

`Currency` is Ordering's and `PaymentCurrency` the payment events' (spec,
section 5): a payment event never writes `Currency`, so the two columns are
independent and no constraint compares them — a disagreement between them
is a publisher defect the read can show, not one the schema can repair.
`Currency` pairs with `TotalAmount` as the other pairs do, because
`OrderPlaced` and `OrderConfirmed` write both in one statement.
**`CK_Orders_PaymentCurrency` is the choice that answers the spec's
labelling rule**: present if and only if either amount is, which refuses an
amount stored unlabelled and a currency stored with nothing to label, and
admits the refund-before-authorisation row §10.7's detail route shows with
only its refund half.

`src/BFF/Web.Bff.Persistence/Configurations/OrderLineRowConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>OrderLines</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class OrderLineRowConfiguration : IEntityTypeConfiguration<OrderLineRow>
{
    public void Configure(EntityTypeBuilder<OrderLineRow> builder)
    {
        builder.ToTable("OrderLines", BffSchema.Name);

        // By position, since nothing in the contracts' line types promises a product appears once (ADR-051).
        builder.HasKey(l => new { l.OrderId, l.LineNumber });
        builder.Property(l => l.LineNumber).ValueGeneratedNever();

        // Cascade, so erasing a buyer's orders takes their lines in the same statement.
        builder
            .HasOne<OrderRow>()
            .WithMany()
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
```

`src/BFF/Web.Bff.Persistence/Configurations/ProductRowConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>Products</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class ProductRowConfiguration : IEntityTypeConfiguration<ProductRow>
{
    public void Configure(EntityTypeBuilder<ProductRow> builder)
    {
        builder.ToTable("Products", BffSchema.Name);

        builder.HasKey(p => p.ProductId);
        builder.Property(p => p.ProductId).ValueGeneratedNever();

        builder.Property(p => p.Name).HasMaxLength(ProjectionLimits.ProductNameMaxLength);
    }
}
```

`src/BFF/Web.Bff.Persistence/Configurations/InboxMessageConfiguration.cs` is
Notifications' file with the schema named once:

```csharp
using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>§9.5's table, mapped here because the schema is the BFF's and the scan looks here.</summary>
internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("InboxMessages", BffSchema.Name);

        // §9.5's composite key: one host may bind a type on two endpoints, and each must process it.
        builder.HasKey(m => new { m.MessageId, m.Endpoint });

        // nvarchar, because narrowing half a key lets an encoding decide whether a message is delivered.
        builder
            .Property(m => m.Endpoint)
            .HasMaxLength(InboxMessage.EndpointMaxLength)

            // BIN2, because queue names are case-sensitive and matched exactly, and this column is half a key.
            .UseCollation("Latin1_General_BIN2");

        // The purge's predicate (§9.5), unfiltered because every row is handled by construction.
        builder
            .HasIndex(m => m.HandledAt)
            .HasDatabaseName("IX_Inbox_HandledAt");
    }
}
```

Add the project to `Platform.slnx`'s `/src/BFF/` folder, below `Web.Bff`:

```xml
    <Project Path="src/BFF/Web.Bff.Persistence/Web.Bff.Persistence.csproj" />
```

- [ ] **Step 4: Run it green**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~ProjectionModelTests"
```

Expected: seven passing tests, and the build with 0 warnings. If
`GetCheckConstraints` returns nothing, the model read is the runtime model
rather than the design-time one — the test reads `IDesignTimeModel` for that
reason; do not move the constraints into a migration by hand.

- [ ] **Step 5: Commit**

```bash
git add src/BFF/Web.Bff.Persistence tests/Web.Bff.Tests/ProjectionModelTests.cs \
        tests/Web.Bff.Tests/Web.Bff.Tests.csproj Platform.slnx
git commit -m "feat(bff): Web.Bff.Persistence holds ADR-051's three tables and the inbox"
```

---

### Task 3: `Web.Bff.Migrator`, `AddOrderProjection`, and the suite's fixture

**Files:**
- Create: `src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj`
- Create: `src/BFF/Web.Bff.Migrator/Program.cs`
- Create: `src/BFF/Web.Bff.Migrator/MigratorHost.cs`
- Create: `src/BFF/Web.Bff.Migrator/MigrationRunner.cs`
- Create: `src/BFF/Web.Bff.Migrator/Dockerfile`
- Create: `src/BFF/Web.Bff.Persistence/Migrations/<timestamp>_AddOrderProjection.cs`,
  its `.Designer.cs` and `BffDbContextModelSnapshot.cs` — emitted, never hand-written
- Create: `tests/Web.Bff.Tests/BffServiceFixture.cs`
- Create: `tests/Web.Bff.Tests/BffIntegrationCollection.cs`
- Create: `tests/Web.Bff.Tests/DatabaseSmokeTests.cs`
- Create: `tests/Web.Bff.Tests/ProjectionSchemaTests.cs`
- Modify: `tests/Web.Bff.Tests/BffFactory.cs`
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`
- Modify: `Platform.slnx`

**Interfaces:**
- Produces: `Web.Bff.Migrator.MigratorHost.Build(string[])`, reading
  `ConnectionStrings:BffMigrator`; `MigrationRunner.RunAsync`;
  `BffServiceFixture` with `RunMigratorAsync`; `BffIntegrationCollection`;
  `BffFactory.DatabaseConnectionString` and `BffFactory.UnreachableDatabase`.
- Consumes: Task 2's context.

**The fixture lives in the suite, not in `Web.Bff.TestSupport`.** That project
compiles pricing.proto's server half, and a project referencing `Web.Bff`
there would put every generated message in one compilation twice — §4.1's
CS0436 argument for why the support project exists at all. The suite already
references both and names no generated type.

**The fixture's broker is started and unused here.** `ServiceFixture<,,>`
starts one per collection under the account `bff-svc`, which
`definitions.json` does not grant until PR-2; `HarnessWrite` returns null, so
nothing reads the grant, and no code in this PR connects. If the container's
own wait fails, that is the measurement saying the account is needed now, and
PR-2's `definitions.json` entry moves here rather than the fixture growing a
switch.

- [ ] **Step 1: The factory's database key**

In `tests/Web.Bff.Tests/BffFactory.cs`, below `Scope`:

```csharp
    /// <summary>A server that does not resolve, so a route that queries it fails and no other route notices.</summary>
    public const string UnreachableDatabase =
        "Server=tcp:sql.invalid,1433;Database=Bff;Encrypt=False;Connect Timeout=1";

    /// <summary>The runtime key (§7.1), which <c>BffServiceFixture</c> points at its container.</summary>
    public string DatabaseConnectionString { get; set; } = UnreachableDatabase;
```

`Settings` gains the key:

```csharp
    protected virtual IEnumerable<KeyValuePair<string, string?>> Settings =>
    [
        new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
        new($"{ServiceIdentityOptions.SectionName}:ClientId", "web-bff-test"),
        new($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret"),
        new($"{ServiceIdentityOptions.SectionName}:Scope", Scope),
        new("ConnectionStrings:Bff", DatabaseConnectionString)
    ];
```

and `ConfigureServices` swaps the hosted purge for a singleton, as
Notifications' factory does, so `ServiceFixture.PurgeRetentionAsync` can drive
a pass:

```csharp
            // §9.5's purge, matched by the ImplementationType AddHostedService<T> sets, so a test drives each pass.
            ServiceDescriptor? purge = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(RetentionPurgeService));

            if (purge is not null)
            {
                services.Remove(purge);
                services.AddSingleton<RetentionPurgeService>();
            }
```

`SingleOrDefault` rather than `Single`, because Task 4 is what registers it
and this step lands first; Task 4 Step 3 turns it into `Single` once the host
registers the purge unconditionally. Add `using Common.Infrastructure.Messaging;`
and `using Microsoft.Extensions.Hosting;`.

In `tests/Web.Bff.Tests/OptionsValidationTests.cs`, `MissingSettingFactory`
replaces `Settings` whole, so it must carry the database key too, or every
row of `The_host_refuses_to_start_without_each_credential` passes on the
missing database instead of the missing credential once Task 4 lands:

```csharp
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
            new("ConnectionStrings:Bff", UnreachableDatabase),
            .. Members.Select(name => new KeyValuePair<string, string?>(
                $"{ServiceIdentityOptions.SectionName}:{name}",
                string.Equals(name, member, StringComparison.Ordinal) ? "" : "supplied"))
        ];
```

- [ ] **Step 2: Write the failing container tests**

`tests/Web.Bff.Tests/BffIntegrationCollection.cs`:

```csharp
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§12.4's collection over SQL Server and the broker; xUnit v3 gives its trait to every test in it.</summary>
[CollectionDefinition(nameof(BffIntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class BffIntegrationCollection : ICollectionFixture<BffServiceFixture>;
```

`tests/Web.Bff.Tests/BffServiceFixture.cs`:

```csharp
using Common.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Web.Bff.Migrator;
using Web.Bff.Persistence;

namespace Web.Bff.Tests;

/// <summary>The BFF's names, migrator and factory over the shared body (ADR-056).</summary>
public sealed class BffServiceFixture()
    : ServiceFixture<BffFactory, Program, BffDbContext>("Bff")
{
    /// <summary>Runs the real §7.4 job host; a null argument leaves that key unset.</summary>
    public static Task<int> RunMigratorAsync(
        string? migratorConnectionString,
        string? runtimeConnectionString = null) =>
        MigratorRun.RunAsync(
            "Bff",
            MigratorHost.Build,
            (services, ct) => services.GetRequiredService<MigrationRunner>().RunAsync(ct),
            migratorConnectionString,
            runtimeConnectionString);

    protected override Task<int> MigrateAsync(string connectionString) => RunMigratorAsync(connectionString);

    protected override BffFactory CreateFactory() => new() { DatabaseConnectionString = ConnectionString };
}
```

`tests/Web.Bff.Tests/DatabaseSmokeTests.cs`:

```csharp
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The migrator against a real engine (ADR-010), and the schema it leaves (ADR-051).</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class DatabaseSmokeTests(BffServiceFixture fixture)
{
    [Fact]
    public async Task Migrator_exits_zero_and_creates_the_schema()
    {
        // The fixture ran the real §7.4 job against an empty server, so this is its own outcome.
        fixture.FirstRunExitCode.ShouldBe(0);

        int schema = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM sys.schemas WHERE name = 'bff'");
        schema.ShouldBe(1);

        // Named, since a count passes on a different migration of the same length.
        string[] applied = await fixture.AppliedMigrationsAsync();
        applied.Length.ShouldBe(1);
        applied[0].ShouldEndWith("_AddOrderProjection");
    }

    [Fact]
    public async Task Migrating_twice_applies_nothing_and_still_exits_zero()
    {
        // §7.4 reruns this on every deploy, so applying nothing has to succeed.
        int exitCode = await BffServiceFixture.RunMigratorAsync(fixture.ConnectionString);

        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Migrator_fails_when_only_the_runtime_connection_string_is_set()
    {
        // §7.1's split is a boundary only while the migrator reads its own key.
        int exitCode = await BffServiceFixture.RunMigratorAsync(
            migratorConnectionString: null,
            runtimeConnectionString: fixture.ConnectionString);

        exitCode.ShouldBe(1);
    }
}
```

`tests/Web.Bff.Tests/ProjectionSchemaTests.cs`:

```csharp
using Microsoft.Data.SqlClient;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>What the database itself refuses and keeps, beneath the handlers that write it (ADR-051).</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class ProjectionSchemaTests(BffServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>SQL Server's constraint-violation error.</summary>
    private const int ConstraintViolation = 547;

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_row_needs_only_its_key_and_the_two_instants()
    {
        // Any of the eight events can create the row, so every fact column has to start empty.
        Guid orderId = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            orderId);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.Orders WHERE OrderId = {0}", orderId))
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_cancellation_without_its_member_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, CancelledAt, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_Cancellation");
    }

    [Fact]
    public async Task A_member_outside_the_three_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, CancelledAt, CancelOutcome, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), N'refunded', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_CancelOutcome");
    }

    [Fact]
    public async Task An_amount_without_its_payment_currency_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, AuthorisedAt, AuthorisedAmount, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), 59.97, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_PaymentCurrency");
    }

    [Fact]
    public async Task A_payment_currency_with_no_amount_to_label_is_refused()
    {
        SqlException refused = await Should.ThrowAsync<SqlException>(() => fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, PaymentCurrency, FirstSeenAt, AsOf)
            VALUES ({0}, N'GBP', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            Guid.CreateVersion7()));

        refused.Number.ShouldBe(ConstraintViolation);
        refused.Message.ShouldContain("CK_Orders_PaymentCurrency");
    }

    [Fact]
    public async Task A_refund_recorded_before_any_authorisation_is_kept()
    {
        // §9.4 orders nothing, so the refund can arrive first; §10.7's detail route shows its half alone.
        Guid orderId = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, RefundedAt, RefundedAmount, PaymentCurrency, FirstSeenAt, AsOf)
            VALUES ({0}, SYSDATETIMEOFFSET(), 59.97, N'GBP', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            orderId);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.Orders WHERE OrderId = {0}", orderId))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Deleting_an_order_deletes_its_lines()
    {
        Guid orderId = Guid.CreateVersion7();

        await fixture.ExecuteAsync(
            """
            INSERT INTO bff.Orders (OrderId, FirstSeenAt, AsOf) VALUES ({0}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            INSERT INTO bff.OrderLines (OrderId, LineNumber, ProductId, Quantity, UnitPrice)
            VALUES ({0}, 0, NEWID(), 1, 9.99), ({0}, 1, NEWID(), 2, 4.50);
            DELETE FROM bff.Orders WHERE OrderId = {0};
            """,
            orderId);

        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM bff.OrderLines WHERE OrderId = {0}", orderId))
            .ShouldBe(0, "erasure deletes a buyer's orders and has to take their lines with them");
    }

    [Fact]
    public async Task The_owned_index_is_filtered_to_rows_with_an_owner()
    {
        string filter = await fixture.ScalarAsync<string>(
            """
            SELECT Value = filter_definition
            FROM sys.indexes
            WHERE name = 'IX_Orders_Owned' AND object_id = OBJECT_ID('bff.Orders')
            """);

        filter.ShouldBe("([CustomerId] IS NOT NULL)");
    }
}
```

`ScalarAsync<string>` needs a non-null `Value`; if the index is missing the
query returns no row and `SingleAsync` throws, which is the failure wanted.

Add to `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`'s package group:

```xml
    <!-- SqlException, named by the schema tests to read a refusal's number. -->
    <PackageReference Include="Microsoft.Data.SqlClient" />
```

and to its project group, updating the `Common.TestSupport` comment:

```xml
    <!-- §7.4's job host, which the fixture runs for real against its container. -->
    <ProjectReference Include="..\..\src\BFF\Web.Bff.Migrator\Web.Bff.Migrator.csproj" />
    <!-- WriteEndpointRule (ADR-058) and the shared ServiceFixture body (ADR-056). -->
    <ProjectReference Include="..\Common.TestSupport\Common.TestSupport.csproj" />
```

- [ ] **Step 3: Run them and watch them fail**

```bash
dotnet build tests/Web.Bff.Tests
```

Expected: the build fails — `Web.Bff.Migrator` does not exist, so
`MigratorHost` and `MigrationRunner` do not resolve.

- [ ] **Step 4: The migrator**

`src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    §4.1's migrator and §7.4's job host for ADR-051's schema: ADR-007 forbids
    migrating at startup, so the schema is applied by a process with no
    listener, holding the one identity that has DDL (§7.1).
  -->

  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" />
    <!-- Design-time only: `dotnet ef migrations add` needs it here, and the running job does not. -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" PrivateAssets="all" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Web.Bff.Persistence\Web.Bff.Persistence.csproj" />
  </ItemGroup>

</Project>
```

`src/BFF/Web.Bff.Migrator/Program.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Web.Bff.Migrator;

// §7.4's migration job, which ADR-007 keeps out of every host's startup.
// The host is not started: a Job whose pod never completes never finishes its pre-upgrade hook.
using IHost host = MigratorHost.Build(args);
using IServiceScope scope = host.Services.CreateScope();

return await scope.ServiceProvider
    .GetRequiredService<MigrationRunner>()
    .RunAsync(CancellationToken.None);
```

`src/BFF/Web.Bff.Migrator/MigratorHost.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Web.Bff.Persistence;

namespace Web.Bff.Migrator;

/// <summary>The §7.4 job host, outside <c>Program.cs</c> so a test can drive the wiring the Job runs.</summary>
public static class MigratorHost
{
    public static IHost Build(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // §7.1's migrator identity, the only one with DDL. Reading the runtime key here would reduce the two
        // principals to a naming convention.
        builder.Services.AddDbContext<BffDbContext>(o =>
            o.UseSqlServer(
                builder.Configuration.GetConnectionString("BffMigrator"),
                sql => sql.EnableRetryOnFailure()));

        builder.Services.AddScoped<MigrationRunner>();

        return builder.Build();
    }
}
```

`src/BFF/Web.Bff.Migrator/MigrationRunner.cs` is Notifications' runner with
`NotificationsDbContext` read as `BffDbContext`, the namespace
`Web.Bff.Migrator`, `using Web.Bff.Persistence;` for the context, and the
three log texts naming the schema:

```csharp
            "BFF schema is already current; nothing to apply.");
...
            "BFF schema migrated.");
...
            "BFF migration failed; the schema may be partially applied. The job exits non-zero.");
```

Everything else — the four `LoggerMessage.Define` fields, `RunAsync`, its
pending-list read and the broad catch with its comment — is copied verbatim.

`src/BFF/Web.Bff.Migrator/Dockerfile`:

```dockerfile
# src/BFF/Web.Bff.Migrator/Dockerfile

# Pinned to the exact patch global.json names, same as the host image (§4.4);
# a bump there is a bump in both files.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble AS build
WORKDIR /src

# Project files first, so restore sits in a layer that survives source-only
# changes. No Common.Web and no Web.Bff — the reference chain stops at
# Web.Bff.Persistence. Every project in it takes a line: a csproj absent at
# restore is not restored, and the --no-restore publish fails with NETSDK1004.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/Common.Domain/Common.Domain.csproj src/BuildingBlocks/Common.Domain/
COPY src/BuildingBlocks/Common.Application/Common.Application.csproj src/BuildingBlocks/Common.Application/
COPY src/BuildingBlocks/Common.Contracts/Common.Contracts.csproj src/BuildingBlocks/Common.Contracts/
COPY src/BuildingBlocks/Common.Infrastructure/Common.Infrastructure.csproj src/BuildingBlocks/Common.Infrastructure/
COPY src/BFF/Web.Bff.Persistence/Web.Bff.Persistence.csproj src/BFF/Web.Bff.Persistence/
COPY src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj src/BFF/Web.Bff.Migrator/
RUN dotnet restore src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj

# .editorconfig is a build input under ADR-019 — without it this publish
# enforces a weaker style policy than every other build.
COPY .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/BFF/Web.Bff.Persistence/ src/BFF/Web.Bff.Persistence/
COPY src/BFF/Web.Bff.Migrator/ src/BFF/Web.Bff.Migrator/
RUN dotnet publish src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj \
    -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Runtime, not aspnet — the migrator has no listener. -extra, because plain
# chiselled runs globalization-invariant and Microsoft.Data.SqlClient refuses
# it; ICU and tzdata are the whole difference.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra AS final
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Web.Bff.Migrator.dll"]
```

Add the project to `Platform.slnx`'s `/src/BFF/` folder, below
`Web.Bff.Persistence`:

```xml
    <Project Path="src/BFF/Web.Bff.Migrator/Web.Bff.Migrator.csproj" />
```

- [ ] **Step 5: Emit the migration**

```bash
dotnet tool restore
dotnet ef migrations add AddOrderProjection \
    --project src/BFF/Web.Bff.Persistence \
    --startup-project src/BFF/Web.Bff.Migrator \
    --output-dir Migrations
```

Expected: three files under `src/BFF/Web.Bff.Persistence/Migrations/`. Read
the `Up` before believing it, and check it holds exactly: `EnsureSchema("bff")`;
four `CreateTable` calls — `InboxMessages`, `Orders`, `OrderLines`,
`Products` — every one in schema `bff`; `Orders` with the six check
constraints of Task 2, `Currency` and `PaymentCurrency` both `nvarchar(3)`,
and every column but `OrderId`, `FirstSeenAt` and `AsOf` nullable;
`OrderLines` with the foreign key `FK_OrderLines_Orders_OrderId`,
`onDelete: ReferentialAction.Cascade`; the index `IX_Orders_Owned` with
`descending: new[] { false, true, true }` and `filter: "[CustomerId] IS NOT NULL"`;
and `IX_Inbox_HandledAt`. A difference is a configuration to fix and a
migration to remove and re-emit (`dotnet ef migrations remove` with the same
two project arguments), never an edit to the emitted file.

- [ ] **Step 6: Run the container tests green**

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~DatabaseSmokeTests|FullyQualifiedName~ProjectionSchemaTests"
```

Expected: eleven passing tests, against a SQL Server and a RabbitMQ container
the collection starts. Docker must be running; without it they fail on
`Failed to connect to Docker endpoint`, which is the intended direction.

- [ ] **Step 7: The image builds**

```bash
docker build -f src/BFF/Web.Bff.Migrator/Dockerfile -t web-bff-migrator:local .
```

Expected: a successful build. A NETSDK1004 names a project the restore block
is missing a line for.

- [ ] **Step 8: Commit**

```bash
git add src/BFF/Web.Bff.Migrator src/BFF/Web.Bff.Persistence/Migrations Platform.slnx \
        tests/Web.Bff.Tests/BffServiceFixture.cs tests/Web.Bff.Tests/BffIntegrationCollection.cs \
        tests/Web.Bff.Tests/DatabaseSmokeTests.cs tests/Web.Bff.Tests/ProjectionSchemaTests.cs \
        tests/Web.Bff.Tests/BffFactory.cs tests/Web.Bff.Tests/OptionsValidationTests.cs \
        tests/Web.Bff.Tests/Web.Bff.Tests.csproj
git commit -m "feat(bff): Web.Bff.Migrator applies AddOrderProjection, proved over a real SQL Server"
```

---

### Task 4: The host registers the schema, the purge and its SQL readiness

**Files:**
- Create: `src/BFF/Web.Bff/BffPersistence.cs`
- Create: `src/BFF/Web.Bff/SqlConnectionFactory.cs`
- Modify: `src/BFF/Web.Bff/Program.cs`
- Modify: `src/BFF/Web.Bff/Web.Bff.csproj`
- Modify: `src/BFF/Web.Bff/Dockerfile`
- Create: `tests/Web.Bff.Tests/PersistenceRegistrationTests.cs`
- Create: `tests/Web.Bff.Tests/RetentionPurgeTests.cs`
- Modify: `tests/Web.Bff.Tests/HostPipelineTests.cs`
- Modify: `tests/Web.Bff.Tests/DatabaseSmokeTests.cs`
- Modify: `tests/Web.Bff.Tests/BffFactory.cs` — `SingleOrDefault` to `Single`

**Interfaces:**
- Produces: `Web.Bff.BffPersistence.AddBffPersistence(IServiceCollection,
  IConfiguration)`, reading the literal key `"Bff"`; an
  `IDbConnectionFactory` singleton over the runtime key; `InboxTable("bff")`,
  `RetentionPolicy`, the hosted `RetentionPurgeService` with no outbox and no
  marker half; the health check `sql`, tagged `ready`.
- **Not** produced here: the `DbContext` alias §9.5's inbox filter resolves.
  Nothing in this PR resolves `DbContext`, so the alias arrives with the
  filter in PR-2 rather than as a registration nothing reads.

- [ ] **Step 1: Write the failing tests**

`tests/Web.Bff.Tests/PersistenceRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's schema as the host reaches it: refused without its key, and gating readiness (§13.5).</summary>
public sealed class PersistenceRegistrationTests
{
    [Fact]
    public void The_registration_names_the_runtime_key_it_cannot_find()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException thrown = Should.Throw<InvalidOperationException>(
            () => new ServiceCollection().AddBffPersistence(configuration));

        thrown.Message.ShouldContain("ConnectionStrings:Bff");
    }

    [Fact]
    public void The_host_refuses_to_start_without_the_runtime_key()
    {
        using NoDatabaseFactory factory = new();

        // Any exception, as a disposal race in the factory can replace the refusal; the test above names it.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_readiness_set_is_the_projections_sql_check()
    {
        // Registration, asserted directly, since unwired readiness and instant readiness look alike (§13.5).
        using BffFactory factory = new();
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        // Exactly this, so Catalog's hop joining the set fails here rather than in an outage (§9.7).
        options.Registrations.Select(r => r.Name).ShouldBe(["sql"]);
        options.Registrations.Single().Tags.ShouldContain("ready");
    }

    private sealed class NoDatabaseFactory : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
            base.Settings.Where(setting => setting.Key != "ConnectionStrings:Bff");
    }
}
```

`tests/Web.Bff.Tests/RetentionPurgeTests.cs`:

```csharp
using Common.Infrastructure.Inbox;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§9.5's purge over the BFF's inbox, with no outbox and no markers to compose (ADR-051).</summary>
[Collection(nameof(BffIntegrationCollection))]
public sealed class RetentionPurgeTests(BffServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_pass_purges_the_aged_inbox_row_and_reports_no_outbox_and_no_markers()
    {
        // Far from the window on either side, so the test is about the predicate, not a boundary.
        await fixture.StageInboxAsync(
            new InboxMessage(Guid.CreateVersion7(), "bff-probe", DateTimeOffset.UtcNow.AddDays(-30)),
            new InboxMessage(Guid.CreateVersion7(), "bff-probe", DateTimeOffset.UtcNow.AddDays(-1)));

        (int outbox, int inbox, int idempotency) = await fixture.PurgeRetentionAsync();

        outbox.ShouldBe(0);
        inbox.ShouldBe(1);
        idempotency.ShouldBe(0);
        (await fixture.InboxAsync()).Count.ShouldBe(1);
    }
}
```

In `tests/Web.Bff.Tests/DatabaseSmokeTests.cs`, add the probe against the
real database — a poll, because the SQL check runs on each request and the
first one can race the container's first login:

```csharp
    [Fact]
    public async Task Ready_probe_answers_200_against_the_migrated_database()
    {
        using HttpClient client = fixture.Factory.CreateClient();

        HttpStatusCode status = HttpStatusCode.ServiceUnavailable;
        Stopwatch stopwatch = Stopwatch.StartNew();

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(30))
        {
            using HttpResponseMessage response =
                await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            status = response.StatusCode;

            if (status == HttpStatusCode.OK)
                break;

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        }

        status.ShouldBe(HttpStatusCode.OK, "the projection's database is up and migrated");
    }
```

with `using System.Diagnostics;` and `using System.Net;`.

Replace `tests/Web.Bff.Tests/HostPipelineTests.cs`'s theory with:

```csharp
    [Fact]
    public async Task Liveness_answers_without_a_token()
    {
        // Anonymous for the kubelet, and gated on nothing, so it holds with the database unreachable (§13.5).
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/startup")]
    public async Task Readiness_answers_without_a_token_and_reports_the_database_it_cannot_reach(string path)
    {
        // 503 rather than 401: anonymous, and gated on a SQL Server this factory cannot reach (§13.5).
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
```

The 200 against a reachable database is `DatabaseSmokeTests`', above.

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet build tests/Web.Bff.Tests
```

Expected: the build fails — `AddBffPersistence` does not exist. With it
stubbed to return `services`, the refusal tests fail on their messages, the
readiness test finds an empty set, the two readiness probes answer 200, and
the purge test fails resolving `RetentionPurgeService`.

- [ ] **Step 3: The registration**

`src/BFF/Web.Bff/SqlConnectionFactory.cs`:

```csharp
using System.Data;
using Common.Application;
using Microsoft.Data.SqlClient;

namespace Web.Bff;

/// <summary>§6.5's port over §7.1's runtime identity, since a query has no business on the migrator's.</summary>
/// <remarks><c>Create</c> only constructs: Dapper opens a closed connection, and the caller disposes (§6.5).</remarks>
internal sealed class SqlConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public IDbConnection Create() => new SqlConnection(connectionString);
}
```

`src/BFF/Web.Bff/BffPersistence.cs`:

```csharp
using Common.Application;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Web.Bff.Persistence;

namespace Web.Bff;

/// <summary>ADR-051's schema as this host reaches it: context, connection port, purge and readiness.</summary>
public static class BffPersistence
{
    public static IServiceCollection AddBffPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        // Eager, so a host with no database does not start; an empty environment variable counts as none.
        // A literal key, as every service spells it, because smoke.sh holds the chart to the key it greps (§15.3).
        string? connectionString = configuration.GetConnectionString("Bff");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Bff is not configured. The buyer's order read is a projection " +
                "this host owns (ADR-051), and it cannot be written or read without its database (§7.1).");
        }

        // EnableRetryOnFailure is what makes an execution strategy a real retry rather than a no-op (§6.3).
        services.AddDbContext<BffDbContext>(o =>
            o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));

        // §6.5's port, which the purge reads through and the projection's SQL will too.
        services.AddSingleton<IDbConnectionFactory>(new SqlConnectionFactory(connectionString));

        // No OutboxTable and no marker half: the BFF publishes nothing and runs no command pipeline (§9.5).
        services.AddSingleton(new InboxTable(BffSchema.Name));
        services.AddSingleton(new RetentionPolicy());
        services.AddHostedService<RetentionPurgeService>();

        // Readiness (§13.5): this host's own SQL; Catalog's hop stays out, or its outage would unready the BFF.
        services
            .AddHealthChecks()
            .AddSqlServer(connectionString, name: "sql", tags: ["ready"]);

        return services;
    }
}
```

In `src/BFF/Web.Bff/Program.cs`, after
`builder.Services.AddSingleton(TimeProvider.System);`:

```csharp
// ADR-051's projection: its schema, its inbox purge and its readiness check (§7.1, §9.5, §13.5).
builder.Services.AddBffPersistence(builder.Configuration);
```

and replace the two health lines

```csharp
// No database, so no readiness check (§13.5); Catalog is left out, or its outage would unready this host too.
app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);   // §13.5 — anonymous; kubelet carries no token
```

with

```csharp
// SQL gates readiness (§13.5); Catalog is left out, or its outage would unready this host too.
app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
```

In `src/BFF/Web.Bff/Web.Bff.csproj`, replace the opening comment

```xml
  <!-- One project, like the gateway: §10.1 gives the BFF no domain and no database. -->
```

with

```xml
  <!-- The host, with ADR-051's schema beside it in Web.Bff.Persistence: §10.1 gives the BFF no domain layer. -->
```

add to the package group

```xml
    <!-- UseSqlServer, for ADR-051's projection; Web.Bff.Persistence carries it, and this project names it. -->
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" />
    <!-- SqlConnectionFactory (§6.5). -->
    <PackageReference Include="Microsoft.Data.SqlClient" />
    <!-- The SQL readiness check (§13.5). -->
    <PackageReference Include="AspNetCore.HealthChecks.SqlServer" />
```

and to the project group

```xml
    <!-- ADR-051's schema, which the migrator applies and this host reads and writes. -->
    <ProjectReference Include="..\Web.Bff.Persistence\Web.Bff.Persistence.csproj" />
```

The `Common.Contracts` reference's comment — "The BFF consumes no Ordering
message type and must not start" — stays: it is still true until the
consumers exist, and PR-2 is what makes it false.

In `src/BFF/Web.Bff/Dockerfile`, add the persistence project to the restore
block, after the `Common.Web` line:

```dockerfile
COPY src/BFF/Web.Bff.Persistence/Web.Bff.Persistence.csproj src/BFF/Web.Bff.Persistence/
```

and replace the final stage's comment

```dockerfile
# -extra, like every other image here. §15.2 argued the suffix from
# Microsoft.Data.SqlClient, which refuses to open a connection under
# globalization-invariant mode — and this host, like the gateway, opens no
# database connection at all; its hop is gRPC (§9.7). It takes the
# variant anyway, for the second reason that chapter gives: one base image
# across the platform, so what a host does with a culture-sensitive comparison
# never depends on which suffix somebody picked for its image. ICU and tzdata
# are the whole difference; no shell, no root.
```

with

```dockerfile
# -extra, like every other image here, for §15.2's reason:
# Microsoft.Data.SqlClient refuses to open a connection under
# globalization-invariant mode, and this host opens one to its projection's
# database (ADR-051). ICU and tzdata are the whole difference; no shell, no
# root.
```

In `tests/Web.Bff.Tests/BffFactory.cs`, the purge now always exists, so the
swap reads `Single` and loses its `if`:

```csharp
            // §9.5's purge, matched by the ImplementationType AddHostedService<T> sets, so a test drives each pass.
            ServiceDescriptor purge = services.Single(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(RetentionPurgeService));
            services.Remove(purge);

            services.AddSingleton<RetentionPurgeService>();
```

- [ ] **Step 4: Run the suite green**

```bash
dotnet test tests/Web.Bff.Tests --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests --filter "Category=Integration"
```

Expected: every non-container test passes, the quote, identity, resilience
and options suites included — none queries the database, so the unreachable
server costs them nothing, and `OptionsValidationTests`' theory fails for the
credential it removes because `MissingSettingFactory` now carries the
database key. Prove that last claim once: delete the
`new("ConnectionStrings:Bff", UnreachableDatabase)` line from
`MissingSettingFactory`, run `Each_credential_is_required_and_named_in_the_failure`
and `The_host_refuses_to_start_without_each_credential`, see the second still
pass for the wrong reason, and restore the line — the first is what names the
member, and that is why both exist. The integration run passes the Keycloak
collection and the BFF collection, the latter with the readiness probe at 200
and the purge test.

- [ ] **Step 5: The host image builds**

```bash
docker build -f src/BFF/Web.Bff/Dockerfile -t web-bff:local .
```

- [ ] **Step 6: Commit**

```bash
git add src/BFF/Web.Bff tests/Web.Bff.Tests
git commit -m "feat(bff): the host registers ADR-051's schema, its inbox purge and a SQL readiness check"
```

The body argues the readiness change: §13.5's rule is that a host with a
connection string has a readiness check; Catalog stays out for the reason
the chart's comment gives; the exemption goes rather than being kept beside
a non-empty set.

---

### Task 5: Compose — the migrator pair, the override, the example keys and the recipe

**Files:**
- Modify: `deploy/compose/services/web-bff.yml`
- Modify: `deploy/compose/docker-compose.infra-only.yml`
- Modify: `deploy/compose/.env.example`
- Modify: `deploy/compose/README.md`
- Modify: `.github/secret-scan/allowed/deploy.txt`

- [ ] **Step 1: The unit**

Rewrite `deploy/compose/services/web-bff.yml`. The two connection-string
values are **not printed here** (Global Constraints): each is
`services/ordering.yml`'s value for the same key with `ORDERING_` read as
`BFF_` and `Database=Ordering` as `Database=Bff`, the `${SQL_PASSWORD:-…}`
nesting left exactly as it is.

```yaml
# The BFF: POST /v1/checkout/quote (ADR-045), minting its own token (§11.5),
# and ADR-051's order projection, migrated by the one-shot beside it.
services:
  # §14.1's pair rule: a migrator one-shot and the host it gates, on
  # `condition: service_completed_successfully`, because no host may migrate
  # at startup (§4.1, ADR-007). Named bff-migrator for the key it reads.
  bff-migrator:
    build:
      context: ../../..
      dockerfile: src/BFF/Web.Bff.Migrator/Dockerfile
    environment:
      # Migrator identity (DDL) — §7.1. Locally both keys resolve to the one
      # sa login (§14.2's stated simplification); the KEY still differs.
      ConnectionStrings__BffMigrator: "<ordering.yml's ConnectionStrings__OrderingMigrator value, renamed as above>"
    depends_on:
      sql: { condition: service_healthy }
    restart: "no"

  # Client credentials, because this host calls a peer synchronously (§9.7);
  # §15.4 says which other hosts do. Named web-bff, matching the Aspire
  # resource (§14.2) and the YARP destination (§10.2) — the gateway resolves
  # the destination by hostname, so the container name is the routing
  # configuration.
  web-bff:
    build:
      context: ../../..
      dockerfile: src/BFF/Web.Bff/Dockerfile
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      # Runtime identity (DML only) — never the migrator connection. The host
      # refuses to start without it, and it is the one readiness check here:
      # Catalog's hop deliberately is not, since a BFF unready whenever
      # Catalog is down takes itself out of rotation for a fault it exists to
      # degrade around (§13.5).
      ConnectionStrings__Bff: "<ordering.yml's ConnectionStrings__Ordering value, renamed as above>"
      Identity__Authority: "http://keycloak:8080/realms/commerce"
      # Required by ValidateOnStart (§15.4) — this host refuses to boot without
      # all three, which is the whole point of the [Required] attributes on
      # ServiceIdentityOptions. Local values only; production mounts a secret.
      #
      # The client id must exist in the realm, and RealmClientTests is what
      # says so: a name here that Keycloak has never heard of satisfies
      # ValidateOnStart, boots cleanly, and fails every pricing call at the
      # token endpoint.
      Identity__Client__ClientId: "web-bff"
      Identity__Client__ClientSecret: "<the current file's value, unchanged>"
      # The scope that becomes the audience every service validates (§11.5).
      # Not the same claim as `aud`, and nothing but the realm's audience
      # mapper makes one imply the other.
      Identity__Client__Scope: "commerce-api"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
    ports: [ "127.0.0.1:5200:8080" ]
    depends_on:
      bff-migrator: { condition: service_completed_successfully }
      # Keycloak, because this host mints its own token and would otherwise
      # discover that on the first request.
      keycloak: { condition: service_healthy }
      # And catalog-api, which services/gateway.yml cannot say about its own
      # destinations for a different reason — it routes to services that do
      # not exist yet, where this one calls a service that does. The hop is
      # over 8081, catalog-api's HTTP/2 endpoint (§9.7): a cleartext port
      # cannot serve HTTP/1.1 and h2c at once, so Catalog declares two and this
      # is the second. It is not published to the host and no route reaches it.
      catalog-api: { condition: service_started }
```

Every comment block is five lines or fewer; the two that were longer in the
old file — the readiness block and the migrator note — are the ones
rewritten, because the comment gate judges a touched block whole.

- [ ] **Step 2: The override, the example keys and the recipe**

In `deploy/compose/docker-compose.infra-only.yml`, add `bff-migrator` above
`web-bff`:

```yaml
  bff-migrator:
    profiles: [ "excluded" ]
  web-bff:
    profiles: [ "excluded" ]
```

In `deploy/compose/.env.example`, above the BFF's client-secret block, add the
two keys in the services' form — the comment is Ordering's, with the name
changed, and the two commented values are Ordering's two lines with `ORDERING_`
read as `BFF_` and `Database=Ordering` as `Database=Bff`:

```
# The BFF's two §7.1 keys, commented out deliberately: the inline defaults
# nest ${SQL_PASSWORD:-…}, so overriding the password alone keeps both
# connection strings correct — a value uncommented here would freeze the
# password inside it and quietly stop following. Uncomment only to point
# the BFF at a different server or login entirely.
# BFF_CONNECTION=<Ordering's ORDERING_CONNECTION line, renamed as above>
# BFF_MIGRATOR_CONNECTION=<Ordering's ORDERING_MIGRATOR_CONNECTION line, renamed as above>
```

In `deploy/compose/README.md`, the BFF's host-run paragraph opens "The BFF is
excluded too, and it needs more than an authority"; make it say the database
as well, and add the key to its block — the value is Ordering's host-run
`ConnectionStrings__Ordering` export with `Database=Ordering` read as
`Database=Bff`:

```
The BFF is excluded too, and it needs more than an authority — its
projection's database (ADR-051), which the override leaves running and the
excluded `bff-migrator` would have migrated, so run the migrator first with
`ConnectionStrings__BffMigrator` set to the same value; §15.4's three
`Identity__Client__*` rows are required of a host that calls a peer,
`ValidateOnStart` refuses to boot without all three, and its own hop needs
Catalog's **gRPC** port rather than its REST one:
```

and in the block, after `export ASPNETCORE_ENVIRONMENT=Development`, one
more export: `ConnectionStrings__Bff`, carrying Ordering's host-run
`ConnectionStrings__Ordering` value with `Database=Ordering` read as
`Database=Bff`, quoted as that line is.

- [ ] **Step 3: Validate, then accept the scan's findings**

```bash
docker compose -f deploy/compose/docker-compose.yml config -q
docker compose -f deploy/compose/docker-compose.yml -f deploy/compose/docker-compose.infra-only.yml config -q
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: both `config` runs print nothing. The scan reports the new
findings in `deploy/compose/services/web-bff.yml`, `deploy/compose/.env.example`
and `deploy/compose/README.md`, each with a rule id and a fingerprint. Add
one line per finding to `.github/secret-scan/allowed/deploy.txt`, in the form
Notifications' entries take, for example:

```
deploy/compose/services/web-bff.yml | connection-string-password | <fingerprint the scan printed> | Section 14.1's local database default, nested inside the BFF's two connection defaults.
deploy/compose/services/web-bff.yml | credential-assignment | <fingerprint the scan printed> | The BFF's local connection default, in-cluster hostname.
```

and the same for the other two files, each reason saying which default it is.
Re-run the scan until it reports nothing. A finding whose fingerprint matches
an existing entry for another file still needs its own line: entries are per
path.

- [ ] **Step 4: Bring it up**

```bash
docker compose -f deploy/compose/docker-compose.yml up -d --wait --quiet-pull
docker compose -f deploy/compose/docker-compose.yml ps -a bff-migrator web-bff
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5200/health/ready
docker compose -f deploy/compose/docker-compose.yml down -v
```

Expected: `bff-migrator` exited 0, `web-bff` running, the probe `200`. A
host-side RabbitMQ holding 5672 on this machine is a known conflict; bring
the stack up with a scratchpad port override rather than editing the
committed files.

- [ ] **Step 5: Commit**

```bash
git add deploy/compose .github/secret-scan/allowed/deploy.txt
git commit -m "feat(compose): web-bff gains bff-migrator and its projection's connection"
```

---

### Task 6: CI builds the migrator image

**Files:**
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Watch the gate fail first**

```bash
py -3.12 .github/pipeline-gate/pipeline_gate.py images
```

Expected: it fails naming `src/BFF/Web.Bff.Migrator/Dockerfile` as a
Dockerfile no matrix entry builds.

- [ ] **Step 2: The leg**

In `ci.yml`'s `images` matrix, after the `web-bff` entry:

```yaml
          - filter: bff
            image: web-bff-migrator
            dockerfile: src/BFF/Web.Bff.Migrator/Dockerfile
```

The `bff` filter already matches `src/BFF/**`, the `changes` job already
exports it and the job's `if:` already reads it, so the leg is the whole
change.

- [ ] **Step 3: Run it green**

```bash
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
```

Expected: both pass.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: the images job builds web-bff-migrator"
```

---

### Task 7: The chart turns on its database and its migration Job

**Files:**
- Modify: `deploy/helm/web-bff/values.yaml`
- Create: `deploy/helm/web-bff/templates/migrate-job.yaml`
- Modify: `deploy/canary/deployables/web-bff.json`
- Modify: `deploy/helm/smoke.sh` — one comment
- Modify: `deploy/canary/canary.py` — one docstring
- Modify: `deploy/canary/test_canary.py` — one test

**Why the database half is this PR's** (spec, section 4). `smoke.sh` reads
each chart's source tree from its descriptor — `src/BFF/Web.Bff` — and once
that tree holds `GetConnectionString("Bff")` it requires the chart to name a
`connectionName`, so the Helm workflow, which this PR triggers through its
`src/BFF/Web.Bff/**` filter, goes red without this task. The reason under
the gate is worse: a release rendered from `main` after Task 4 would start a
pod that refuses to start. The broker half and the `consume` signal are
PR-2's, for the same reason one dependency later.

- [ ] **Step 1: Watch smoke fail first**

```bash
HELM=/path/to/helm PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

Expected: it fails on `web-bff resolves a connection string in src/, so its
chart names one`. If `helm` is on `PATH`, `HELM=` may be left out.

- [ ] **Step 2: The values**

In `deploy/helm/web-bff/values.yaml`, replace the `image` block's migrator
comment with the key:

```yaml
image:
  registry: registry.example.com/commerce
  api: web-bff
  # ADR-051's projection schema, applied by the pre-upgrade hook (§7.4) before
  # any pod of this tag starts; smoke.sh asserts the key and
  # templates/migrate-job.yaml agree.
  migrator: web-bff-migrator
  tag: ""
  pullPolicy: IfNotPresent
```

add, after `resources`, the services' job resources:

```yaml
migrationJob:
  # Smaller than the host's: the migrator opens one connection, applies a
  # migration and exits (§7.4). It is not serving anything.
  resources:
    requests: { cpu: 50m, memory: 128Mi }
    limits:   { memory: 256Mi }
```

and replace the readiness comment and the two capability blocks — from
`# No connection strings of any kind, and therefore no readiness check` through
`broker:\n  enabled: false` — with:

```yaml
# ADR-051's projection. GetConnectionString("Bff") at runtime, "BffMigrator"
# in the hook. It is the one readiness check (§13.5): Catalog's hop is not,
# since a BFF unready whenever Catalog is down takes itself out of rotation
# for a fault it exists to degrade around.
database:
  enabled: true
  connectionName: Bff
  runtimeSecretRef:
    name: web-bff-database
    key: connection-string
  migratorSecretRef:
    name: web-bff-migrator-secret
    key: connection-string

# This host reads no ConnectionStrings:RabbitMq, and smoke.sh holds that to
# src/ in the direction it can read.
broker:
  enabled: false
```

The `redis` block's comment says "on database and broker's terms"; it still
reads true and stays.

`deploy/helm/web-bff/templates/migrate-job.yaml`:

```yaml
{{- include "commerce.migrationJob" . }}
```

- [ ] **Step 3: The descriptor, and the three places that name the BFF databaseless**

In `deploy/canary/deployables/web-bff.json`, `"migrator": false` becomes
`"migrator": true`.

In `deploy/helm/smoke.sh`, the comment above the databaseless loop:

```bash
# The gateway owns no database (§10.1, §4.2), so the hook has nothing to run
# for it. The output assertion alone is vacuous — that chart carries no
# migration template at all (§15.3) — so the subject is the agreement between
# the two halves: a chart has a migration template exactly when its values
# name a migrator image, and it fires broken from either side.
```

In `deploy/canary/canary.py`, `migration_prefix`'s docstring:

```python
    """The `<workload>-migrate-` a Job name would start with, where there is one.

    None for a chart that renders no migration Job — the gateway owns no
    database (§10.1, §15.3), so its tags are bounded only by the label length.
    Derived from the templates on disk rather than listed, because a chart
    gains a migrator by having the file.
    """
```

In `deploy/canary/test_canary.py`, the test that asserts the BFF renders no
Job becomes the pair it now is:

```python
    def test_a_databaseless_workload_has_no_migration_budget(self) -> None:
        """The gateway owns no database (§10.1), so its chart renders no Job
        and its tags are bounded only by the label."""
        plan = canary.load_plan()

        self.assertIsNone(canary.migration_prefix("gateway", plan))
        canary.validate_tag("a" * 63, canary.migration_prefix("gateway", plan))

    def test_the_bff_spends_its_tag_budget_on_a_migration_job(self) -> None:
        """ADR-051's projection gives the BFF a schema, so its chart renders
        the hook and its tag is bounded by the Job's name."""
        plan = canary.load_plan()

        self.assertEqual(canary.migration_prefix("web-bff", plan), "web-bff-migrate-")
```

- [ ] **Step 4: Run them green**

```bash
HELM=/path/to/helm PYTHON="py -3.12" bash deploy/helm/smoke.sh
py -3.12 -m unittest discover -s deploy/canary
```

Expected: every smoke check passes, `web-bff` now among the migrator charts —
its Job renders with the hook annotations, mounts
`ConnectionStrings__BffMigrator`, and its pod is not an endpoint of its own
Service; the canary suite passes.

- [ ] **Step 5: Commit**

```bash
git add deploy/helm deploy/canary
git commit -m "feat(deploy): web-bff's chart renders its database and migration Job"
```

The body argues why the chart moves here (spec section 4): `smoke.sh`
holds the chart to the code, and a chart behind the code is a pod that does
not start.

---

### Task 8: The chapters and the repo map

**Files:**
- Modify: `docs/backend-architecture/04-solution-structure.md` — §4.1, §4.2
- Modify: `docs/backend-architecture/13-observability.md` — §13.5
- Modify: `docs/backend-architecture/14-local-development.md` — §14.1, §14.2
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.2, §15.3
- Modify: `docs/repo-map.md`
- Modify: `.github/secret-scan/allowed/docs.txt`

Each amendment names the set or cites ADR-051 and writes no count (the
spec's section 12 rule).

- [ ] **Step 1: §4.1's tree**

Replace

```
│   ├── BFF/
│   │   └── Web.Bff/                    Aggregation for the web client (§10.1).
│   │                                   The only host that calls a service
│   │                                   synchronously on a request path (§9.7,
│   │                                   ADR-052); it binds Identity:Client, and
│   │                                   the grant's code is
│   │                                   Common.Infrastructure's (§11.5)
```

with

```
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
```

and in the tests half, the `Web.Bff.Tests/` entry's first lines

```
│   ├── Web.Bff.Tests/                  §9.7's hop and §11.5's credentials: the
│   │                                   resilience hierarchy read off the built
│   │                                   host, the quote endpoint over a real
│   │                                   gRPC server on loopback, and the ONE
```

become

```
│   ├── Web.Bff.Tests/                  §9.7's hop and §11.5's credentials: the
│   │                                   resilience hierarchy read off the built
│   │                                   host, the quote endpoint over a real
│   │                                   gRPC server on loopback, ADR-051's
│   │                                   schema through the real migrator, and
│   │                                   the ONE
```

with the rest of the entry rewrapped to the same column and otherwise
unchanged.

- [ ] **Step 2: §4.2's readiness paragraph**

Replace

```
Two hosts pass it — this one and the BFF, which §13.5 names as the two whose
dependencies do not gate readiness. Neither owns *none*: the gateway proxies
four services and the BFF calls Catalog (§9.7). Every service fails to start
without its own checks.
```

with

```
One host passes it — this one, which §13.5 names as the host whose
dependencies do not gate readiness. It does not own *none*: it proxies the
services it routes to. The BFF does not pass it, because its projection is a
schema of its own
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md))
and so a readiness check of its own, which Catalog's hop is deliberately not
part of (§9.7). Every service fails to start without its own checks.
```

The sample above it — `app.MapCommonHealthEndpoints(ownsNoReadinessDependencies:
true);` — is the gateway's `Program.cs` and stays.

- [ ] **Step 3: §13.5**

Replace

```
guard above rather than left as prose. The **gateway** and the **BFF** own no
database (§4.2), so they declare their empty set at the call site and an
absence becomes a written decision. Every other host fails to start: each
service owns a schema, including the two with no public API, since Shipping
and Notifications both ship a migrator and both register a SQL check (§4.1,
[§3.2](03-bounded-contexts.md)).
```

with

```
guard above rather than left as prose. The **gateway** owns no database
(§4.2), so it declares its empty set at the call site and an absence becomes
a written decision. Every other host fails to start: each service owns a
schema, including the two with no public API, since Shipping and
Notifications both ship a migrator and both register a SQL check (§4.1,
[§3.2](03-bounded-contexts.md)), and so does the BFF, whose projection is a
schema of its own
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)).
```

- [ ] **Step 4: §14.1's fence and §14.2's AppHost**

In §14.1's Compose fence, the `web-bff` block becomes the shipped file's
shape: a `bff-migrator` service printed above it in the form the fence
prints `ordering-migrator`, and `web-bff` gaining `ConnectionStrings__Bff`
and `bff-migrator: { condition: service_completed_successfully }`. Copy
Ordering's two fence lines for the values, renamed as Task 5 renames them,
and keep the fence's existing `depends_on` comment about `catalog-api` being
elided. The fence's comment above `web-bff` loses nothing; the migrator's
reads:

```yaml
  # §14.1's pair rule for ADR-051's projection: the migrator one-shot the
  # host gates on.
  bff-migrator:
```

In §14.2's AppHost, beside the two databases:

```csharp
// One database per service or host that this AppHost runs. The rest are omitted
// deliberately — adding a database without the service and migrator
// resources that own it creates a schema nothing maintains, which is the
// shape §4.1 rules out.
var orderingDb = sql.AddDatabase("Ordering");
var catalogDb = sql.AddDatabase("Catalog");
var bffDb = sql.AddDatabase("Bff");
```

beside the two migrators:

```csharp
var bffMigrator = builder
    .AddProject<Projects.Web_Bff_Migrator>("bff-migrator")
    .WithReference(bffDb, connectionName: "BffMigrator")
    .WaitFor(sql);
```

and the BFF's resource:

```csharp
WithPlatformIdentity(
    builder.AddProject<Projects.Web_Bff>("web-bff")
        .WithReference(bffDb).WaitFor(bffDb)
        .WaitForCompletion(bffMigrator)
        .WithReference(catalog)
        .WithHttpHealthCheck("/health/ready"),
    callerClientId: "web-bff");
```

The comment above that resource — "The only resource with a callerClientId,
and the only one this model needs" — stays true and stays.

- [ ] **Step 5: §15.2**

Replace

```
**Two of the fourteen open no connection at all** — the gateway ([§10.1](10-api-gateway.md)) and the
BFF, whose one synchronous hop is gRPC rather than SQL ([§9.7](09-messaging.md)) — so the sentence
above is the reason for twelve images and not for those two. Twelve because
each of the six services builds **two**, a host and a migrator (§4.1), and both
talk to SQL Server. The other two take the variant for uniformity: one base
across the platform means what a host does with a culture-sensitive comparison
never depends on which suffix somebody picked for its image, and the saving
from dropping ICU on two deployables does not pay for a second answer to that
question.
```

with

```
**One image opens no connection at all** — the gateway's
([§10.1](10-api-gateway.md)) — so the sentence above is the reason for every
other image and not for that one. Every other deployable builds **two**, a
host and a migrator, and both talk to SQL Server: each service by §4.1, and
the BFF because its projection is a schema of its own
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)).
The gateway takes the variant for uniformity: one base across the platform
means what a host does with a culture-sensitive comparison never depends on
which suffix somebody picked for its image, and the saving from dropping ICU
on one deployable does not pay for a second answer to that question.
```

- [ ] **Step 6: §15.3**

No §15.3 sentence goes false — its gateway paragraph speaks of the gateway
alone — but the spec's section 12 gives it the BFF among the charts that
render a migration Job, so a reader of the gateway's exception is not left
to infer that the BFF shares it. Immediately before the paragraph that opens
"The gateway's chart is not a service chart with the database parts
deleted", insert:

```
**The BFF's chart carries a migrator, as a service's does.** Its projection
is a schema of its own
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)),
so its values name `image.migrator`, it renders `templates/migrate-job.yaml`,
and its descriptor says `migrator: true` — the gateway is the chart below
that has none of the three.
```

- [ ] **Step 7: The repo map**

In `docs/repo-map.md`, the `src/BFF/Web.Bff/` entry ends "Same shape as the
gateway", which is now false. Replace the entry with:

```
src/BFF/                     the third host, Web.Bff, and the one that calls
                             a peer synchronously on a request path (§9.7,
                             ADR-017) — it holds client credentials because
                             of it, as every caller of a peer does (§11.5,
                             ADR-052). Beside it, Web.Bff.Persistence and
                             Web.Bff.Migrator: ADR-051's projection is a
                             schema of its own, kept out of the host so the
                             migrator references no web host
```

- [ ] **Step 8: Check, accept the scan's chapter findings, and validate**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: findings in `docs/backend-architecture/14-local-development.md` for
the BFF's two fence defaults. Add one line each to
`.github/secret-scan/allowed/docs.txt`, beside that file's existing entries,
for example:

```
docs/backend-architecture/14-local-development.md | credential-assignment | <fingerprint the scan printed> | The BFF's local connection default, as printed by the chapter's compose excerpt.
```

Re-run until it reports nothing. Then run `/validate-blueprint` — this PR
edits chapters, which is the audit's trigger — and `/check-links`, for the
ADR-051 links this task and Task 1 add. Reconcile anything either finds
before the commit, and record the direction in the commit body.

- [ ] **Step 9: Commit**

```bash
git add docs/backend-architecture docs/repo-map.md .github/secret-scan/allowed/docs.txt
git commit -m "docs: §4.1, §4.2, §13.5, §14.1, §14.2, §15.2 and §15.3 give the BFF ADR-051's schema"
```

---

### Task 9: Verification and the PR

- [ ] **Step 1: The whole solution**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests --filter "Category=Integration"
dotnet test tests/Common.Infrastructure.Tests
```

Expected: 0 warnings; every suite green. The integration stage for the
whole solution runs in CI; locally the two runs above are the suites this PR
changed, and Task 1 Step 5 ran the two services whose fixture construction it
touched.

- [ ] **Step 2: The gates**

```bash
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 .github/pipeline-gate/pipeline_gate.py images
py -3.12 .github/pipeline-gate/pipeline_gate.py filters
dotnet restore Platform.slnx && dotnet build Platform.slnx && py -3.12 .github/output-gate/output_gate.py
py -3.12 -m unittest discover -s deploy/canary
HELM=/path/to/helm PYTHON="py -3.12" bash deploy/helm/smoke.sh
```

Expected: every one passes. The comment gate judges HEAD, so it runs after
the last commit; the output gate's reconciliation is what proves both new
projects are in `Platform.slnx`.

- [ ] **Step 3: The PR**

Open it with `/pr`. The body's `| Class |` row is `A+D+E` and its
`| Touch set |` row is exactly the Global Constraints line, paths only, with
the reasons under the table. It says `Refs #425` and closes this PR's own
issue with a bare `Closes #<n>` line, and **no** sentence containing a
closing keyword before `#425` in any form, negated or not. Before opening,
run `main`'s `locality_gate.py --map .github/locality-gate/classes.yml` over
the drafted body and `git diff --name-only origin/main..HEAD`, in the input
shape `.github/workflows/locality-gate.yml` builds.

---

## Interfaces for PR-2

Every name below exists on disk when this PR merges, and PR-2's plan
consumes them as written:

- **Projects**: `src/BFF/Web.Bff.Persistence` (namespace
  `Web.Bff.Persistence`) and `src/BFF/Web.Bff.Migrator`; both in
  `Platform.slnx`.
- **Schema**: `BffSchema.Name` = `"bff"`; tables `bff.Orders`,
  `bff.OrderLines`, `bff.Products`, `bff.InboxMessages`; columns exactly
  Task 2's `OrderRow`, `OrderLineRow` and `ProductRow` properties.
- **Constraints PR-2's statements must respect**: `CK_Orders_Total`
  (`Currency` and `TotalAmount` set together), `CK_Orders_Cancellation`
  (`CancelledAt` and `CancelOutcome`), `CK_Orders_Authorisation`
  (`AuthorisedAt` and `AuthorisedAmount`), `CK_Orders_Refund` (`RefundedAt`
  and `RefundedAmount`), `CK_Orders_CancelOutcome` (one of `CancelOutcomes`'
  three), and `CK_Orders_PaymentCurrency` (`PaymentCurrency` non-null if and
  only if `AuthorisedAmount` or `RefundedAmount` is); `FK_OrderLines_Orders_OrderId`,
  cascade, so the `Orders` row is inserted before its lines in the same
  transaction.
- **`PaymentCurrency`** (`nvarchar(3)`, `ProjectionLimits.CurrencyLength`):
  the payment handlers write it in the **same statement** as the first
  amount, set once — `COALESCE` over the existing value, as every set-once
  column — so the constraint holds at every commit; a later payment event's
  amount is written beside the value already there. Payment events never
  write `Currency`, and Ordering events never write `PaymentCurrency`.
- **Widths**: `ProjectionLimits.CurrencyLength`, `ProductNameMaxLength`,
  `TrackingNumberMaxLength`, `CancelOutcomeMaxLength` — the refusals of a
  wider wire value are PR-2's.
- **Migration**: `AddOrderProjection` is the only one; PR-2's
  `IndexUnattributedOrders` follows it.
- **Host**: `Web.Bff.BffPersistence.AddBffPersistence` registers
  `BffDbContext`, an `IDbConnectionFactory` singleton, `InboxTable("bff")`,
  `RetentionPolicy` and the hosted purge; **it does not register the
  `DbContext` alias** §9.5's `InboxFilter<>` resolves — PR-2 adds
  `services.AddScoped<DbContext>(sp => sp.GetRequiredService<BffDbContext>());`
  there with the filter.
- **Readiness**: `PersistenceRegistrationTests.The_readiness_set_is_the_projections_sql_check`
  asserts exactly `["sql"]`; PR-2's `AddMassTransit` adds `masstransit-bus`,
  and PR-2 changes that assertion to `["sql", "masstransit-bus"]` in the same
  commit.
- **Tests**: `BffServiceFixture` (in `tests/Web.Bff.Tests`, name `Bff`, so
  account `bff-svc` and password `local-dev-bff`), `BffIntegrationCollection`,
  `BffFactory.DatabaseConnectionString` and `BffFactory.UnreachableDatabase`.
  PR-2's `BffFactory` needs a placeholder `ConnectionStrings:RabbitMq` the
  same way, and `MissingSettingFactory` and `NoDatabaseFactory` must carry it.
- **Deploy**: the chart has `database.enabled: true` and `broker.enabled:
  false`; the descriptor `migrator: true` and `signals: ["http"]`. PR-2's
  `GetConnectionString("RabbitMq")` in `src/BFF/Web.Bff` makes `smoke.sh`
  require `broker.enabled: true`, and its `AddConsumer` makes the canary's
  consume scan require the `consume` signal — both are PR-2's to add (spec,
  section 4), with §14.2's broker half.

## Self-review

Against the spec, section by section:

- **Section 1, where the code lives**: Task 2 creates `Web.Bff.Persistence`
  with the context, rows, configurations and (Task 3) migrations and nothing
  else; Task 3 creates `Web.Bff.Migrator`; the migrator references the
  library and not the host. ✓
- **Section 1, the names**: database `Bff`, schema `bff`, keys
  `ConnectionStrings__Bff` and `ConnectionStrings__BffMigrator`, fixture name
  `Bff`; workload, chart and container stay `web-bff`. ✓ The broker account
  and queue are PR-2's.
- **Section 1, the rows**: three tables, a column per fact, step timestamps
  nullable and no status column. ✓
- **Section 1, readiness**: SQL now (Task 4), the bus with PR-2; the
  exemption removed; Catalog out, asserted by an exact set. ✓
- **Section 1, retention**: no purge of order rows; the inbox purged on its
  window (Task 4's container test); cascade delete for the erasure path. ✓
- **Section 3**: `RetentionPurgeService`'s optional half, both or neither,
  refused otherwise, with §9.5's sentence (Task 1); `BffFactory`'s
  placeholder key and the fixture under `Bff` (Task 3); existing quote,
  identity and pipeline tests run without a container (Task 4 Step 4). ✓
- **Section 4, PR-1's row**: the schema and migrator (Tasks 2, 3), the host
  (Task 4), the purge's optional half (Task 1), the factory keys and fixture
  (Task 3), Compose (Task 5), the image leg and solution file (Tasks 2, 3,
  6), the chart's database half, migrator, descriptor and the databaseless
  wording in `smoke.sh` and `canary.py` (Task 7), §14.2's database half and
  the repo map (Task 8), the allow entries (Tasks 5, 8). ✓
- **Section 5**: the table, `PaymentCurrency` among its columns at the
  currency width, the nullable columns, `FirstSeenAt` and `AsOf` from the
  registered clock (written by PR-2's statements), the owned index with its
  filter, `CancelOutcome`'s check constraint, `decimal(19,4)` by convention,
  widths from constants, `AddOrderProjection` emitted by the tool. The
  gauge's index is PR-2's. ✓ The spec says "a check constraint" for
  `CancelOutcome`; this plan adds four pair constraints beside it, which the
  spec's "one handler writes both" argument implies, and
  `CK_Orders_PaymentCurrency`, which its "an amount without its currency is
  a number the client cannot render" requires.
- **Section 9**: PR-1's two keys; the Compose pair with the host gated on
  the migrator; no new secret, the shared SQL login's local default. ✓
- **Section 11**: the SQL Server items PR-1 can hold — the migrator, the
  schema, the purge, the readiness set; the handler and route items are
  PR-2's and PR-3's. ✓
- **Section 12**: §4.1 and §4.2 (Task 8), §13.5 (Task 8), §14.1's SQL half
  (Tasks 5 and 8), §14.2's database half (Task 8), §15.2 and §15.3 (Task 8),
  `docs/repo-map.md` (Task 8), `Web.Bff.csproj`'s "one project" comment
  (Task 4), §9.5 (Task 1). ✓
- **Placeholder scan**: the only placeholders are the connection-string
  values deliberately not printed, each naming the exact line it copies, and
  the fingerprints the scan prints. No "TBD", no "similar to", no step
  without its code or its command.

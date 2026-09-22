# Shipping PR-6 — tracking, and the two events — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the service. A second `BackgroundService` asks the carrier for
each booked shipment's page, applies it through the aggregate, and lets the
outbox publish `ShipmentDispatched` and `ShipmentDelivered` — the two events
Ordering's saga, Inventory's handler and ADR-051's projection have been waiting
for. Beside it, `ShippingJurisdictionOptions` makes ADR-053's two statutory
windows values the deployment is given, a retention pass deletes the address
after the shipment is terminal and the tracking events after delivery, and
§15.4 stops saying the solution has one options type.

**Architecture:** the tracking worker is `OutboxDispatcher`'s shape a second
time — an atomic claim that leases rows, then per-row work where each row
succeeds or fails on its own — with the lease over `NextPollAt` and
`LockedUntil` rather than over the outbox's columns. The claim selects
`Booked` and `Dispatched` rows whose `NextPollAt` has passed; each row's page
comes back from `ICarrierGateway.GetEventsAsync` already translated, and is
applied by dispatching one command per shipment so §6.3's `TransactionBehavior`
opens the unit of work, §7.5's dispatcher stages the outbox rows and §9.3's
allow-list decides which of them reach the bus. The retention pass is a third
hosted service in `RetentionPurgeService`'s shape and not a branch of the
tracking loop, because an hourly delete measured in days and a thirty-second
poll paced by a carrier's rate limit fail differently and must not share a
tick. Nothing here calls the carrier from a consumer, and nothing throws into
a queue.

**Tech Stack:** .NET at `global.json`'s pin, EF Core with SQL Server, Dapper
for the claim and the purge, MassTransit over RabbitMQ, `Microsoft.Extensions
.Options` data annotations for the options class, xUnit with Shouldly,
Testcontainers and an in-process WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 3 (PR-6's row), 4 (the second worker, its claim, its lease and its
backoff), 5 (the promotions the tracking feed drives, and that `Unrecognised`
moves nothing), 6 (the `SIM-LATE` interleaving), 7 (retention as a value of the
deployment, and the pass that deletes by identity), 8 (the mapper's two
entries), 9 (`GetEventsAsync` and the empty page), 11
(`shipping.shipments.waiting`, and no address in a log line), 12 (the worker
suite, the made-up deployment and `Platform.IntegrationTests`) and 13 (§15.4 in
PR-6).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Shipping/**`,
  `tests/Shipping.Domain.Tests/**`, `tests/Shipping.Application.Tests/**`,
  `tests/Shipping.Worker.Tests/**`, `tests/Shipping.TestSupport/**`,
  `tests/Platform.IntegrationTests/**`,
  `deploy/compose/services/shipping.yml`,
  `.github/secret-scan/allowed/tests.txt`,
  `docs/backend-architecture/15-cicd-deployment.md`,
  `docs/backend-architecture/11-identity-authorization.md`,
  `docs/secrets.md`.
  Reasons, since the row above is paths only: **A** is the service's own code
  and its four suites, and the cross-service suite that holds two services to
  each other — `Platform.IntegrationTests` is where §4.3's one-assembly rule is
  already spent on purpose, and it already references two services'
  Infrastructure projects for exactly that. **D** is the Compose unit, the
  three documents, and the secret scan's allow-list, which is declared because
  a new test file is what a new finding would be reported against and a row
  there is the only way one is closed — Task 10 says why none is expected.
  **E** is the `.csproj` under
  `tests/Platform.IntegrationTests/`, which gains `Testcontainers.MsSql`,
  `Testcontainers.RabbitMq`, `Testcontainers.Redis`, `MassTransit`,
  `Microsoft.Extensions.DependencyInjection.Abstractions`, `Respawn` and
  `Microsoft.Data.SqlClient`
  and project references to `Ordering.TestSupport` and `Inventory.TestSupport` —
  every package already pinned in `Directory.Packages.props`, so **no
  `Version=` attribute, no `Directory.Packages.props` change and no Appendix B
  row**.
- **The locality gate admits `A+D+E` today** — `locality_gate.py` names it as
  the one three-member class and reads it as its three members — so the class
  row is spelled exactly `A+D+E` and no contract change is owed.
- **Depends on PR-5 having merged**, and on PR-1 through PR-4 behind it. What
  this plan consumes from PR-5, under PR-5's own spellings:
  - **`Shipping.Application.Shipments.IShipmentRepository`** — an Application
    port, not a Domain one — with
    `Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct)`,
    `Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct)` and
    `void Add(Shipment shipment)`. **There is no `GetForUpdateAsync`**: the
    read by shipment id is `GetAsync`, and both reads `Include` the tracking
    events, which is what makes a repeated page's deduplication correct.
  - **`Shipping.Application.Addresses.IDeliveryAddressStore`** with
    `SaveAsync(OrderId, Guid customerId, DeliveryAddress, DateTimeOffset
    fetchedAt, CancellationToken)` and `GetAsync(OrderId, CancellationToken)`,
    over `shipping.DeliveryAddresses(OrderId, CustomerId, Line1, Line2, City,
    PostalCode, Country, FetchedAt)`, implemented by
    `Shipping.Infrastructure.Persistence.SqlDeliveryAddressStore`. **It has no
    delete member**, because §11.7's erasure is owed whole with that
    extension — so Task 5's purge is raw SQL over PR-5's table and column
    names and not a call through the port.
  - **`Shipping.Infrastructure.Messaging.DependencyInjection.EventsQueue`** and
    its two consumers.
  - **`Shipping.Infrastructure.Fulfilment.FulfilmentClaims`**, the claim and
    the backoff over `shipping.Shipments` — `UPDLOCK, READPAST, ROWLOCK`,
    `TOP (FulfilmentWorker.ClaimBatchSize)`, `ORDER BY NextAttemptAt`, an
    `OUTPUT` projection into `FulfilmentWork`, and `FailSql` reading
    `OutboxDispatcher.BackoffAttemptCap` and `BackoffBaseSeconds` — with
    `FulfilmentWorker.ClaimBatchSize = 1`, `LeaseSeconds = 60` and
    `RunOnceAsync`, and `Shipment.ReleaseClaim()` on the aggregate.
    **`FulfilmentClaims` is not a general helper and is not extracted into
    one**: its two statements name the fulfilment populations and
    `NextAttemptAt` in their text. Task 3 writes a `TrackingClaims` beside it
    in exactly that shape rather than parameterising either, and the two are
    reconciled by shape and by their treatment of `LockedUntil`, not by a
    shared class.
  - **`ShippingWorkerFactory(string connectionString, string
    rabbitConnectionString, string carrierBaseUrl = UnreachableCarrier,
    string? carrierApiKey = null, string addressSourceBaseUrl =
    UnreachableAddressSource)`**, with `Tokens` (a `RecordingTokenCache`) and
    the three `Identity__Client__*` settings; and `ServiceFixture` with
    **`Carrier`, a `WireMockServer` started over `SimulatorMappings.Directory()`
    — PR-5 declares it with that type and puts `WireMock.Net` on
    `Shipping.TestSupport.csproj` for it** — `Ordering`, `CapturedLogs`,
    `FailNextCommit()`, `RunFulfilmentPassAsync()`, `QueueDepthAsync`,
    `BindingsAsync`, `NewWorkerHost(carrierBaseUrl)` and the static
    `CarrierAnswers(server, path, statusCode, method, delay)`, beside PR-1's
    `ScalarAsync`, `ExecuteAsync`, `ColumnsAsync` and `ResetAsync`.
  - **`tests/Shipping.OrderingStub`**, a library that is not a test project,
    holding `StubOrdering` and `StubAddress`. Nothing here names a generated
    type, which is the rule that project exists to keep.
  Every one of those is taken by name. **Nothing in this plan redefines a
  member PR-1 or PR-5 put on `Shipment`**: `PollApplied` is the one member
  Task 2 adds, and it calls PR-5's `ReleaseClaim()` rather than repeating its
  two assignments.
- **`shipping.shipments.waiting` is this pull request's, whole.** PR-5's
  self-review says so and argues why: two of the states a row waits in — a
  booked shipment awaiting its poll, and one whose cancellation the carrier
  has not answered — only become a waiting population with the tracking
  worker, so a gauge landing in PR-5 would have been rewritten here. Task 6
  adds it, on PR-2's `Shipping.Outbound` meter through
  `CarrierMetrics.MeterName`, in PR-5's `AddressMetrics` shape: a class of its
  own on the one meter rather than a second instrument bolted to
  `CarrierMetrics`' constructor.
- **No new meter.** §13.2's export names meters one by one and PR-2 already
  added `.AddMeter("Shipping.Outbound")` and its entry in
  `tests/Common.Web.Tests/ObservabilityTests.cs`. This PR adds an instrument to
  that meter and touches neither file.
- **No new alert rule and no runbook.** §13.6's two rules and the runbook they
  share are PR-7's, on the spec's section 3 row; a rule here would name a chart
  that does not exist.
- **One of `docs/secrets.md`'s five places does not apply, and one is
  deferred.** The two keys this PR adds are configuration and not credentials,
  so they take no rotation row and no local-development exception row — but
  those are not among the five. Three of the five are this pull request's:
  Compose, §15.4's inventory and the test fixture. The Aspire row is the one
  that does not apply, because §14.2 is not adopted. The Helm values row is
  deferred to PR-7, because that place is a file inside a chart that does not
  exist yet. **The key's name is not deferred with it**: Task 9 step 2 writes
  `jurisdiction.addressRetention` and `jurisdiction.trackingRetention` into
  §15.4's Source column, under PR-5's Task 6 step 3 rule.
- **The blueprint's vocabulary**: despatch and despatched in prose,
  `Dispatched` in identifiers, because the contract is `ShipmentDispatched`.
- Comments say why and cite the owner — a section, an ADR or a symbol, never a
  pull request or a test — and no comment block runs past ten lines.
- Explicit local types, file-scoped namespaces with a blank line after, braces
  on two statements or more, one space before `=`, `=>` and `{`, `sealed` where
  the codebase seals, 120 columns for code and 80 for prose, British spelling.
- `py -3.12`, never `python`, for anything Python.
- Container tests are `[Collection(nameof(IntegrationCollection))]` or an
  explicit `[Trait("Category", "Integration")]` where no collection exists, and
  are never skipped: without a daemon they fail.
- Every step that adds behaviour writes its test first.

---

### Task 1: `ShippingJurisdictionOptions`, refused at start

**Files:**
- Create: `src/Services/Shipping/Shipping.Infrastructure/Retention/ShippingJurisdictionOptions.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
  — the binding, beside the consumer
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs` — the two
  settings and their invented defaults, after PR-5's `addressSourceBaseUrl`,
  and the `UnreachableAuthority` block that counts the solution's options types
- Test: `tests/Shipping.Worker.Tests/JurisdictionOptionsTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Shipping.Infrastructure.Retention;

public sealed class ShippingJurisdictionOptions
{
    public const string SectionName = "Jurisdiction";
    public TimeSpan? AddressRetention { get; init; }
    public TimeSpan? TrackingRetention { get; init; }
}
```

- Produces: `ShippingWorkerFactory.InventedAddressRetention` and
  `InventedTrackingRetention`, and two optional constructor parameters
  defaulting to them — the sixth and seventh, after PR-1's two, PR-2's
  `carrierBaseUrl`/`carrierApiKey` and PR-5's `addressSourceBaseUrl`. Every
  test below passes them by name, so a later parameter inserted anywhere in
  that list moves nothing here.

- [ ] **Step 1: Write the failing test**

`tests/Shipping.Worker.Tests/JurisdictionOptionsTests.cs`, in
`Web.Bff.Tests/OptionsValidationTests.cs`'s two-halves shape — a host half
that asserts only that the host refused, and a validator half that asserts the
message names the member:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shipping.Infrastructure.Retention;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// ADR-053 rule 1, from the side that matters: a statutory window is a value the
/// deployment is given, and a missing or impossible one is §15.4's failure at
/// start rather than a number this service picks for a regulator.
/// </summary>
/// <remarks>
/// <c>[Required]</c> catches the key nobody supplied; the range catches the key
/// supplied as <c>00:00:00</c>, which a bound <c>TimeSpan</c> hides from it. A
/// host refusing to start races its disposal, so no exception type is asserted.
/// </remarks>
public sealed class JurisdictionOptionsTests
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";

    private static readonly string[] Members = ["AddressRetention", "TrackingRetention"];

    [Theory]
    [InlineData("AddressRetention", "")]
    [InlineData("TrackingRetention", "")]
    [InlineData("AddressRetention", "00:00:00")]
    [InlineData("TrackingRetention", "00:00:00")]
    public void The_host_refuses_to_start_without_a_usable_window(string member, string value)
    {
        using ShippingWorkerFactory factory = new(
            UnreachableSql,
            UnreachableRabbit,
            addressRetention: string.Equals(member, "AddressRetention", StringComparison.Ordinal)
                ? value
                : ShippingWorkerFactory.InventedAddressRetention,
            trackingRetention: string.Equals(member, "TrackingRetention", StringComparison.Ordinal)
                ? value
                : ShippingWorkerFactory.InventedTrackingRetention);

        // The factory builds the host on first use, so the throw arrives here
        // rather than at construction.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("AddressRetention", "")]
    [InlineData("TrackingRetention", "")]
    [InlineData("AddressRetention", "00:00:00")]
    [InlineData("TrackingRetention", "3651.00:00:00")]
    public void Each_window_is_named_in_the_failure(string member, string value)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(Members.Select(name => new KeyValuePair<string, string?>(
                $"{ShippingJurisdictionOptions.SectionName}:{name}",
                string.Equals(name, member, StringComparison.Ordinal) ? value : "11.00:00:00")))
            .Build());

        // The four registration lines rather than the whole infrastructure
        // helper: what is under test is the pairing of the annotations with
        // ValidateDataAnnotations, and the theory above is what still fails if
        // AddShippingInfrastructure ever drops the block.
        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain(member);
    }

    [Fact]
    public void An_invented_jurisdiction_satisfies_both_windows()
    {
        // ADR-053 rule 2: the made-up deployment's values are configuration and
        // nothing else, so the suite passes under them with no line of code
        // changed. Without this the theories above could pass against a class
        // nothing can satisfy.
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"{ShippingJurisdictionOptions.SectionName}:AddressRetention"] =
                        ShippingWorkerFactory.InventedAddressRetention,
                    [$"{ShippingJurisdictionOptions.SectionName}:TrackingRetention"] =
                        ShippingWorkerFactory.InventedTrackingRetention
                })
            .Build());

        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        using ServiceProvider provider = services.BuildServiceProvider();

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        ShippingJurisdictionOptions bound =
            provider.GetRequiredService<IOptions<ShippingJurisdictionOptions>>().Value;

        bound.AddressRetention.ShouldBe(TimeSpan.Parse(
            ShippingWorkerFactory.InventedAddressRetention, System.Globalization.CultureInfo.InvariantCulture));
        bound.TrackingRetention.ShouldBe(TimeSpan.Parse(
            ShippingWorkerFactory.InventedTrackingRetention, System.Globalization.CultureInfo.InvariantCulture));
    }
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~JurisdictionOptionsTests"`
Expected: compile failure — `Shipping.Infrastructure.Retention` does not exist
and `ShippingWorkerFactory` has no retention parameters.

- [ ] **Step 3: Write the options class**

```csharp
using System.ComponentModel.DataAnnotations;

namespace Shipping.Infrastructure.Retention;

/// <summary>
/// ADR-053 rule 1's one options class for this service: the statutory windows a
/// deployment is given, and nothing else — Shipping renders no customer message,
/// so it holds no language set and no time zone.
/// </summary>
/// <remarks>
/// It passes §15.4's test: both windows are a statute's, so a developer's stack
/// is given ADR-053 rule 2's invented ones and a deployment its own.
/// </remarks>
public sealed class ShippingJurisdictionOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Jurisdiction";

    /// <summary>
    /// The shortest window this class will accept. One second rather than
    /// zero, because a zero window deletes the row the instant the shipment
    /// turns terminal and reads on a values file as "not configured".
    /// </summary>
    public const string MinimumWindow = "00:00:01";

    /// <summary>
    /// Ten years, which is a configuration error rather than a policy. Past a
    /// decade the value is a typo, and the purge subtracts it from
    /// <c>DateTimeOffset</c>, which throws where nobody is looking when the
    /// result is not representable.
    /// </summary>
    public const string MaximumWindow = "3650.00:00:00";

    /// <summary>
    /// How long a delivery address is kept after its shipment turns terminal
    /// (spec, section 7). Nullable so <c>[Required]</c> can see a key nobody
    /// supplied: a non-nullable <c>TimeSpan</c> binds to <c>00:00:00</c> and
    /// passes the annotation it was given to satisfy.
    /// </summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? AddressRetention { get; init; }

    /// <summary>
    /// How long a shipment's tracking events are kept after delivery. Its own
    /// window and not the address's: one is about a person and the other about
    /// a parcel, and ADR-053's table gives the two clocks separately.
    /// </summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? TrackingRetention { get; init; }
}
```

`ParseLimitsInInvariantCulture` is load-bearing: `RangeAttribute`'s string
limits go through `TimeSpanConverter`, which reads the current culture, so a
runner with another culture would fail to parse the bound and refuse every
value. Say that in one line above the first annotation and not twice.

- [ ] **Step 4: Bind it beside its consumer**

In `AddShippingInfrastructure` — PR-5 left it registering
`IShipmentRepository`, `IDeliveryAddressStore`, `FulfilmentClaims` and
`AddHostedService<FulfilmentWorker>()`, and `AddDeliveryAddressSource` is
`Program.cs`'s and stays there — immediately above the retention registration
Task 5 adds:

```csharp
        // §15.4's shape, in the registration helper that owns the consumer
        // rather than in Program.cs: a binding hoisted upwards re-imposes the
        // key on every host, which is the mistake that section spends a
        // paragraph on. ValidateOnStart is what turns a missing statutory
        // window into a refusal to boot — IOptions<T> always resolves, so
        // without it the purge would run with a default-constructed instance
        // and delete nothing while reporting healthy (ADR-053).
        services
            .AddOptions<ShippingJurisdictionOptions>()
            .BindConfiguration(ShippingJurisdictionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
```

with `using Shipping.Infrastructure.Retention;` added in sorted position.

- [ ] **Step 5: The invented deployment reaches the factory**

In `tests/Shipping.TestSupport/ShippingWorkerFactory.cs`, two constants beside
`UnreachableCarrier` and PR-5's `UnreachableAddressSource`, and two optional
parameters after PR-5's `addressSourceBaseUrl`:

```csharp
    /// <summary>
    /// ADR-053 rule 2's made-up jurisdiction, and deliberately a value no real
    /// one uses: eleven days and twenty-three days match neither the six years
    /// nor the five that record's table names, so a test passing under them is
    /// a test that read its configuration rather than a constant.
    /// </summary>
    public const string InventedAddressRetention = "11.00:00:00";

    /// <inheritdoc cref="InventedAddressRetention"/>
    public const string InventedTrackingRetention = "23.00:00:00";
```

and, in `ConfigureWebHost`, below PR-5's three
`{ServiceIdentityOptions.SectionName}:*` settings:

```csharp
            .UseSetting($"{ShippingJurisdictionOptions.SectionName}:AddressRetention", addressRetention)
            .UseSetting($"{ShippingJurisdictionOptions.SectionName}:TrackingRetention", trackingRetention)
```

The section is named through the constant and not as a literal, because
`ShippingJurisdictionOptions.SectionName` is its owner and PR-5's settings
already spell `Identity:Client` that way. The parameters are
`string addressRetention = InventedAddressRetention` and
`string trackingRetention = InventedTrackingRetention`, strings rather than
`TimeSpan` so a test can supply the empty and the zero cases that the binder
sees in a real deployment. The `Shipping.TestSupport` project already
references `Shipping.Infrastructure` for PR-5's
`AddressRegistration.BaseUrlKey`, so the constant is reachable and no
reference is added.

- [ ] **Step 6: The factory's own claim about §15.4's count**

The same file carries a rendered claim that this task makes false. PR-1 copied
`tests/Catalog.TestSupport/CatalogApiFactory.cs`'s `UnreachableAuthority`
block into `ShippingWorkerFactory`, and it argues that the host binds nothing
`ValidateDataAnnotations` could check. That stopped being true of this host
when PR-5 bound `ServiceIdentityOptions`, and step 4 above binds a second
class. The summary and its `<remarks>` are one sixteen-line comment run, which
is already past the gate's ten, so the two are replaced together rather than
the stale sentence corrected in place. Before:

```csharp
    /// <summary>
    /// The authority every host over this <c>Program</c> must name (§11.3).
    /// Deliberately fake and deliberately unreachable — <c>.invalid</c> is
    /// reserved and never resolves, so a test that accidentally dials the
    /// authority fails loudly rather than reaching a real identity provider.
    /// </summary>
    /// <remarks>
    /// Required rather than optional for the same reason both connection
    /// strings are: <c>AddJwtAuthentication</c> reads this key eagerly and
    /// throws naming it, so a service host that cannot name its identity
    /// provider does not start. §12.4 attributed that failure to
    /// <c>ValidateOnStart</c> and <c>OptionsValidationException</c>, and the
    /// chapter was amended — §15.4 keeps <c>ServiceIdentityOptions</c> as the
    /// solution's only options type, so there is nothing here for
    /// <c>ValidateDataAnnotations</c> to check.
    /// </remarks>
```

After, which is `tests/Payments.TestSupport/PaymentsApiFactory.cs`'s form for
the same constant — ten lines, the authority's own argument and nothing about
how many options types the solution has:

```csharp
    /// <summary>
    /// The authority every host over this <c>Program</c> must name (§11.3).
    /// Deliberately fake and deliberately unreachable — <c>.invalid</c> is
    /// reserved and never resolves, so a test that accidentally dials the
    /// authority fails loudly rather than reaching a real identity provider.
    /// Required rather than optional for the same reason both connection
    /// strings are: <c>AddJwtAuthentication</c> reads this key eagerly and
    /// throws naming it, so a host that cannot name its identity provider
    /// does not start.
    /// </summary>
```

The count is dropped rather than raised to two. §15.4 owns it, Task 9 amends
it there, and a corrected copy here would be a further place to correct the
next time the number moves — which is what `docs/change-locality.md` asks a
mention to avoid. The four copies in other services' test support are left
alone for the opposite reason: each is still true of the host it is written
about, and none is in this touch set.

- [ ] **Step 7: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~JurisdictionOptionsTests"
```

Expected: 0 warnings; every case green.

```bash
git add src/Services/Shipping/Shipping.Infrastructure tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "feat(shipping): ShippingJurisdictionOptions binds the two statutory windows"
```

The body argues the three decisions a reviewer would question: why the members
are nullable — so `[Required]` can see a missing key that a `TimeSpan` would
bind to zero — why a statutory window is refused rather than clamped and
does not join `RetentionPolicy`, citing ADR-053, and why the factory's
rendered remark about §15.4's count was cut rather than raised to two.

---

### Task 2: The page the carrier answered, applied through the aggregate

**Files:**
- Modify: `src/Services/Shipping/Shipping.Domain/Shipments/Shipment.cs` —
  `PollApplied`, beside PR-5's `ReleaseClaim`. One file and one member:
  PR-1 wrote `Shipment` as a `sealed` class that is **not** `partial`, and
  PR-5 added `ReleaseClaim` into that file rather than beside it, so a second
  file here would make this service's aggregate the only partial one in the
  solution to save nothing.
- Create: `src/Services/Shipping/Shipping.Application/Shipments/ShipmentErrors.cs`
- Create: `src/Services/Shipping/Shipping.Application/Tracking/ApplyTrackingPageCommand.cs`
- Create: `src/Services/Shipping/Shipping.Application/Tracking/ApplyTrackingPageHandler.cs`
- Test: `tests/Shipping.Domain.Tests/ShipmentPollTests.cs`
- Test: `tests/Shipping.Application.Tests/ApplyTrackingPageHandlerTests.cs`

**Interfaces:**
- Consumes: `Shipment` with `For`/`Book`/`Record`/`Cancel`/`CarrierCancelled`
  and the bookkeeping properties `Attempts`, `NextAttemptAt`, `LockedUntil`,
  `NextPollAt`, `ShipmentId`, `ShipmentStatus`, `TrackingStatus` (PR-1);
  `Shipment.ReleaseClaim()` and
  `Shipping.Application.Shipments.IShipmentRepository` with `GetAsync(
  ShipmentId, CancellationToken)` (PR-5); `CarrierEvent` (PR-2).
- Produces:

```csharp
namespace Shipping.Domain.Shipments;
public sealed class Shipment            // exists; one member added here
{
    public void PollApplied(DateTimeOffset nextPollAt);
}

namespace Shipping.Application.Shipments;
public static class ShipmentErrors { public static readonly Error NotFound; }

namespace Shipping.Application.Tracking;
public sealed record ApplyTrackingPageCommand(
    ShipmentId ShipmentId,
    IReadOnlyList<CarrierEvent> Page,
    DateTimeOffset NextPollAt) : ICommand<Result>;
```

- [ ] **Step 1: Write the failing domain test**

```csharp
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Domain.Tests;

/// <summary>
/// The one piece of the tracking worker's bookkeeping that belongs on the row:
/// when the next poll is due, and that a terminal shipment is never polled
/// again (spec, section 4).
/// </summary>
public class ShipmentPollTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static Shipment Booked()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        return shipment;
    }

    [Fact]
    public void An_applied_page_schedules_the_next_poll_and_clears_the_claim()
    {
        Shipment shipment = Booked();

        shipment.PollApplied(Now.AddSeconds(30));

        shipment.NextPollAt.ShouldBe(Now.AddSeconds(30));

        // The last two are ReleaseClaim's postcondition, asserted again at the
        // member a caller actually reaches for: a PollApplied that scheduled
        // the next poll and left the lease standing would hold the row for the
        // rest of the minute for nothing.
        shipment.LockedUntil.ShouldBeNull();
        shipment.Attempts.ShouldBe(0);
    }

    [Fact]
    public void A_delivered_shipment_is_never_polled_again()
    {
        Shipment shipment = Booked();
        shipment.Record("e1", TrackingStatus.Delivered, Now, Now);

        shipment.PollApplied(Now.AddSeconds(30));

        shipment.NextPollAt.ShouldBeNull(
            "a terminal shipment has no further fact to learn, and a row still due is a row the claim keeps");
    }

    [Fact]
    public void A_voided_shipment_is_never_polled_again()
    {
        Shipment shipment = Booked();
        shipment.Cancel(Now);
        shipment.CarrierCancelled(Now);

        shipment.PollApplied(Now.AddSeconds(30));

        shipment.NextPollAt.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Write the failing handler test**

`tests/Shipping.Application.Tests/ApplyTrackingPageHandlerTests.cs`, against a
fake repository — this suite has no database:

```csharp
using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Application.Tracking;
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

/// <summary>
/// One carrier page applied to one shipment. The handler is where the page's
/// order stops mattering: the aggregate already refuses a superseded arrival,
/// and applying by rank is what makes one page's inserts and its raised events
/// the same whichever order the carrier listed them in (spec, section 5).
/// </summary>
public class ApplyTrackingPageHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_reversed_page_despatches_before_it_delivers()
    {
        // The simulator's SIM-REVERSED script, at the layer that meets it.
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);

        Result result = await Handle(repository, shipment.Id,
        [
            new CarrierEvent("e2", TrackingStatus.Delivered, Now.AddHours(2)),
            new CarrierEvent("e1", TrackingStatus.Collected, Now.AddHours(1))
        ]);

        result.IsSuccess.ShouldBeTrue();
        shipment.Status.ShouldBe(ShipmentStatus.Delivered);
        shipment.DomainEvents.Select(e => e.GetType()).ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)]);
    }

    [Fact]
    public async Task An_unrecognised_status_is_stored_and_moves_nothing()
    {
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);

        await Handle(repository, shipment.Id,
        [
            new CarrierEvent("e1", TrackingStatus.Unrecognised, Now),
            new CarrierEvent("e2", TrackingStatus.InTransit, Now.AddMinutes(1))
        ]);

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.TrackingEvents.Count.ShouldBe(2, "a carrier's fact is kept whether or not it moves the row");
        shipment.DomainEvents.ShouldBeEmpty();
        shipment.NextPollAt.ShouldBe(Now.AddSeconds(30), "a shipment still moving is polled again");
    }

    [Fact]
    public async Task An_empty_page_is_an_answer_and_only_reschedules()
    {
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);

        await Handle(repository, shipment.Id, []);

        shipment.Status.ShouldBe(ShipmentStatus.Booked);
        shipment.TrackingEvents.ShouldBeEmpty();
        shipment.NextPollAt.ShouldBe(Now.AddSeconds(30));
    }

    [Fact]
    public async Task A_repeated_page_raises_the_events_once()
    {
        Shipment shipment = Booked();
        FakeShipments repository = new(shipment);
        CarrierEvent[] page = [new CarrierEvent("e1", TrackingStatus.Collected, Now.AddHours(1))];

        await Handle(repository, shipment.Id, page);
        shipment.ClearDomainEvents();
        await Handle(repository, shipment.Id, page);

        shipment.TrackingEvents.Count.ShouldBe(1);
        shipment.DomainEvents.ShouldBeEmpty("the key makes a repeated page free, and the outbox is not asked twice");
    }

    [Fact]
    public async Task A_shipment_that_is_gone_is_a_refusal_and_not_a_throw()
    {
        // The row was claimed and then voided by a cancellation that committed
        // first. A throw here is a worker row retried for ever; a refusal rolls
        // the unit back and the claim lapses.
        Result result = await Handle(new FakeShipments(null), ShipmentId.New(), []);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ShipmentErrors.NotFound);
    }

    private static Shipment Booked()
    {
        Shipment shipment = Shipment.For(ShipmentId.New(), new OrderId(Guid.CreateVersion7()), Now);
        shipment.Book("car_1", "TRK1", Now);
        shipment.ClearDomainEvents();
        return shipment;
    }

    private static Task<Result> Handle(
        FakeShipments repository,
        ShipmentId id,
        IReadOnlyList<CarrierEvent> page) =>
        new ApplyTrackingPageHandler(repository, TimeProvider.System).HandleAsync(
            new ApplyTrackingPageCommand(id, page, Now.AddSeconds(30)),
            TestContext.Current.CancellationToken);

    /// <summary>
    /// <c>IShipmentRepository</c>, whole: the two members this handler never
    /// calls throw rather than answering, so a handler that started reading by
    /// order would fail here rather than pass.
    /// </summary>
    /// <remarks>
    /// Nested deliberately: this assembly already holds a <c>FakeShipments</c>
    /// that records what was added, and this one answers one shipment — two
    /// doubles for two questions rather than one shared helper.
    /// </remarks>
    private sealed class FakeShipments(Shipment? shipment) : IShipmentRepository
    {
        public Task<Shipment?> GetAsync(ShipmentId id, CancellationToken ct) =>
            Task.FromResult(shipment is not null && shipment.Id == id ? shipment : null);

        public Task<Shipment?> GetByOrderAsync(OrderId orderId, CancellationToken ct) =>
            throw new NotSupportedException("The tracking path reads by shipment id.");

        public void Add(Shipment added) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 3: Run both to see them fail**

```bash
dotnet test tests/Shipping.Domain.Tests --filter "FullyQualifiedName~ShipmentPollTests"
dotnet test tests/Shipping.Application.Tests --filter "FullyQualifiedName~ApplyTrackingPageHandlerTests"
```

Expected: compile failure on `PollApplied`, `ShipmentErrors`,
`ApplyTrackingPageCommand` and `ApplyTrackingPageHandler`.

- [ ] **Step 4: Add the one domain member**

In `Shipment.cs`, below `Record` and beside PR-5's `ReleaseClaim`:

```csharp
    /// <summary>
    /// A tracking pass has been applied: the claim released, the failed-pass
    /// counter cleared, the next poll due at <paramref name="nextPollAt"/>
    /// unless the shipment is terminal (spec, section 4).
    /// </summary>
    /// <remarks>
    /// The release is <see cref="ReleaseClaim"/>'s rather than a second copy, so
    /// no second member drops the lease differently. <c>Attempts</c> counts
    /// failed passes whichever worker took them: one carrier fails both.
    /// </remarks>
    public void PollApplied(DateTimeOffset nextPollAt)
    {
        NextPollAt = Status is ShipmentStatus.Booked or ShipmentStatus.Dispatched ? nextPollAt : null;
        ReleaseClaim();
    }
```

- [ ] **Step 5: Write the catalogue, the command and its handler**

`ShipmentErrors.cs` — this service's first `Error`, and therefore its
catalogue. `Error`'s own remarks make the catalogue the rule rather than a
convention: `Code` is a metric dimension, so its value set has to be closed and
readable in one file, and an `Error` constructed at the call site is a value set
nobody can enumerate. `ReservationErrors` and `OrderErrors` are the two this
one is written to look like:

```csharp
using Common.Application;

namespace Shipping.Application.Shipments;

/// <summary>
/// The catalogue. Every <see cref="Error"/> this service can return is
/// constructed here and nowhere else, which is what keeps <c>Code</c> a bounded
/// set rather than whatever string the nearest handler happened to type.
/// </summary>
/// <remarks>
/// No shipment id and no order id appears in a code below: an id interpolated
/// into a metric dimension is a cardinality incident, and the description is
/// the member written for a person.
/// </remarks>
public static class ShipmentErrors
{
    public static readonly Error NotFound =
        Error.NotFound("shipment.not_found", "No shipment under that identifier.");
}
```

`ApplyTrackingPageCommand.cs`:

```csharp
using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>
/// One page the carrier answered, for one shipment. A command rather than a
/// method the worker calls, so §6.3's <c>TransactionBehavior</c> opens the unit
/// of work and §7.5's dispatcher stages the outbox rows inside it — the two
/// integration events and the state they describe commit together or not at
/// all.
/// </summary>
public sealed record ApplyTrackingPageCommand(
    ShipmentId ShipmentId,
    IReadOnlyList<CarrierEvent> Page,
    DateTimeOffset NextPollAt) : ICommand<Result>;
```

`ApplyTrackingPageHandler.cs`:

```csharp
using Common.Application;
using Shipping.Application.Carrier;
using Shipping.Application.Shipments;
using Shipping.Domain.Shipments;

namespace Shipping.Application.Tracking;

/// <summary>
/// Applies one carrier page through the aggregate and schedules the next poll.
/// </summary>
/// <remarks>
/// Applied by rank, not by arrival: the carrier's key orders nothing (spec,
/// section 5) and the aggregate is monotonic, but events raised in one unit of
/// work are read in the order raised, and a delivery ahead of its despatch is a
/// timeline no consumer can read. The read <c>Include</c>s the tracking events
/// because <c>Shipment.Record</c> deduplicates over the loaded ones (§5.2).
/// </remarks>
public sealed class ApplyTrackingPageHandler(IShipmentRepository shipments, TimeProvider clock)
    : ICommandHandler<ApplyTrackingPageCommand, Result>
{
    public async Task<Result> HandleAsync(ApplyTrackingPageCommand command, CancellationToken ct)
    {
        Shipment? shipment = await shipments.GetAsync(command.ShipmentId, ct);

        // A refusal rather than a throw: the row was claimed and then voided by
        // a cancellation that committed first, and a throw from a worker is a
        // row retried for ever (spec, section 5).
        if (shipment is null)
            return Result.Failure(ShipmentErrors.NotFound);

        DateTimeOffset now = clock.GetUtcNow();

        foreach (CarrierEvent carrierEvent in command.Page.OrderBy(Rank).ThenBy(e => e.OccurredAt))
            shipment.Record(carrierEvent.CarrierEventId, carrierEvent.Status, carrierEvent.OccurredAt, now);

        shipment.PollApplied(command.NextPollAt);

        return Result.Success();
    }

    /// <summary>
    /// The order a page is applied in: the two statuses that promote, in the
    /// order they promote, and everything that moves nothing last. It is an
    /// ordering and not a second state machine — the aggregate still refuses a
    /// superseded arrival, and this only decides which of a page's facts is
    /// offered first.
    /// </summary>
    private static int Rank(CarrierEvent carrierEvent) => carrierEvent.Status switch
    {
        TrackingStatus.Collected => 0,
        TrackingStatus.Delivered => 1,
        _ => 2,
    };
}
```

- [ ] **Step 6: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Domain.Tests
dotnet test tests/Shipping.Application.Tests
```

Expected: 0 warnings, green. `AddShippingApplication`'s handler scan finds the
new handler — the rendered registration scans this assembly — so nothing is
registered by hand; confirm by the handler test passing through the
constructor and by Task 3's worker resolving `IDispatcher` at all.

```bash
git add src/Services/Shipping tests/Shipping.Domain.Tests tests/Shipping.Application.Tests
git commit -m "feat(shipping): ApplyTrackingPageCommand applies a carrier page by rank"
```

---

### Task 3: The tracking worker, its lease and its backoff

**Files:**
- Create: `src/Services/Shipping/Shipping.Infrastructure/Tracking/TrackingWork.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Tracking/TrackingClaims.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Tracking/TrackingWorker.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
  — `services.AddScoped<TrackingClaims>();` and
  `services.AddHostedService<TrackingWorker>();`, beside PR-5's two
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs` — the tracking
  worker's descriptor removed and re-registered, as PR-5 does the fulfilment
  worker's
- Modify: `tests/Shipping.TestSupport/ServiceFixture.cs` — the tracking pass
  helper and the row readers, beside PR-5's `RunFulfilmentPassAsync`
- Test: `tests/Shipping.Worker.Tests/TrackingWorkerTests.cs`
- Test: `tests/Shipping.Worker.Tests/TrackingFaultTests.cs`

**Interfaces:**
- Consumes: `ICarrierGateway.GetEventsAsync`, `CarrierEvent`, `CarrierHop`
  (PR-2); `ApplyTrackingPageCommand` (Task 2); `IDbConnectionFactory`,
  `OutboxDispatcher.BackoffBaseSeconds`, `OutboxDispatcher.BackoffAttemptCap`;
  `FulfilmentClaims` and `FulfilmentWork`, as the shape this file copies and
  the neighbour it is registered beside, and `ServiceFixture.NewWorkerHost`
  and the static `ServiceFixture.CarrierAnswers` (PR-5).
- Produces:

```csharp
namespace Shipping.Infrastructure.Tracking;

public sealed record TrackingWork(Guid Id, Guid OrderId, string CarrierReference, int Attempts);

internal sealed class TrackingClaims
{
    public Task<IReadOnlyList<TrackingWork>> ClaimAsync(CancellationToken ct);
    public Task FailAsync(Guid id, CancellationToken ct);
    public Task ReleaseAsync(Guid id, CancellationToken ct);
}

public sealed class TrackingWorker : BackgroundService
{
    public const int ClaimBatchSize = 20;
    public const int LeaseSeconds = 45;
    public static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(25);
    public Task<int> ProcessBatchAsync(CancellationToken ct);
}
```

**`TrackingClaims` rather than a helper shared with PR-5.** `FulfilmentClaims`
is not a general shape and is not made into one: both of its statements name
the fulfilment populations and `NextAttemptAt` in their own text, and a class
parameterised over a `WHERE` clause and a column name would be one indirection
holding two SQL statements that still have to be read separately to be
understood. So this is a second claim class in the first's shape — same
`UPDLOCK, READPAST, ROWLOCK`, same `TOP (…)`/`ORDER BY`/`OUTPUT` form, same
ladder read from `OutboxDispatcher`'s constants, same `IDbConnectionFactory`
and `CommandDefinition` — and the two are held together by shape and by the
one column they share, not by a base class.

**Neither worker can claim a row the other holds, and it is `LockedUntil` that
says so.** The two populations overlap on purpose: a `Booked` shipment whose
cancellation the carrier has not answered is in the fulfilment claim's second
population *and* pollable, so it is due to both. Every claim below and PR-5's
alike carries `AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())`
and stamps `LockedUntil` in the same statement that selects the row, under
`UPDLOCK` with `READPAST`, so the row belongs to whichever pass took it until
its lease lapses and to neither in the meantime. The status filter decides
which rows a worker is *interested* in; the lease decides who *holds* one. The
two leases are different lengths — 60 seconds there, 45 here — because each
bounds its own worst-case pass, and that is safe precisely because a row is
released by the pass that took it and never by the clock of the other.

- [ ] **Step 1: Write the failing tests**

`tests/Shipping.Worker.Tests/TrackingWorkerTests.cs`, over PR-5's
`ServiceFixture` — its SQL Server container, its in-process carrier simulator
over `SimulatorMappings.Directory()`, and its Ordering stub — so the postal
codes below are section 9's script and no second WireMock.Net is started. The
one case that ends in an outage is the sibling suite below, for the reason that
paragraph gives:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Fulfilment;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The second worker against a real database and the simulator's own mappings:
/// what one pass claims, what it leaves, and which rows it will not take
/// (spec, sections 4 and 9).
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class TrackingWorkerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public void The_lease_outlives_the_hop_and_the_pass()
    {
        // Every_attempt_and_every_bounded_delay_fit_inside_the_total's shape,
        // one level up: a lease shorter than either would let a second replica
        // claim a row this pass is still calling the carrier about, and the two
        // would book or record against the same aggregate.
        TimeSpan lease = TimeSpan.FromSeconds(TrackingWorker.LeaseSeconds);

        lease.ShouldBeGreaterThan(CarrierHop.TotalRequestTimeout, "one call must finish inside the lease");
        lease.ShouldBeGreaterThan(TrackingWorker.PassBudget, "a pass must finish inside its own lease");
        TrackingWorker.PassBudget.ShouldBeGreaterThan(
            CarrierHop.TotalRequestTimeout,
            "a budget below one hop's total would make every pass claim rows and process none");
        TrackingWorker.PassBudget.ShouldBeLessThan(
            TimeSpan.FromSeconds(30),
            "the host's shutdown timeout is thirty seconds (§15.3), and a pass that outlives it is killed mid-row");

        // The two leases are separate numbers over one column, so the pair is
        // stated once here: each bounds its own worst-case pass, and neither is
        // a bound on the other's. What keeps the two workers apart is the
        // LockedUntil predicate both claims carry, which the two tests below
        // drive; the lengths only decide how long a killed replica's row waits.
        TimeSpan.FromSeconds(FulfilmentWorker.LeaseSeconds).ShouldBeGreaterThan(
            TimeSpan.FromSeconds(TrackingWorker.LeaseSeconds),
            "a fulfilment pass makes two hops and a tracking pass one, so its lease is the longer");
    }

    [Fact]
    public async Task A_booked_shipment_is_polled_and_a_collected_page_despatches_it()
    {
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Dispatched");
        (await fixture.OutboxAsync())
            .Select(row => row.MessageType)
            .ShouldContain(type => type.Contains("ShipmentDispatched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_delivered_page_publishes_the_despatch_first_and_stops_the_polling()
    {
        Shipment shipment = await fixture.BookedAsync("050000");

        await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken);

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");
        (await fixture.NextPollAtAsync(shipment.Id)).ShouldBeNull(
            "a terminal shipment has nothing further to learn");

        // Order, not membership: Ordering's saga finalises on the first and
        // ADR-051's projection reads both.
        (await fixture.OutboxAsync())
            .OrderBy(row => row.OccurredAt)
            .Select(row => row.MessageType.Split('.')[^1])
            .ShouldBe(["ShipmentDispatched", "ShipmentDelivered"]);
    }

    [Fact]
    public async Task A_row_still_being_polled_is_not_claimed_by_a_second_pass()
    {
        await fixture.BookedAsync("SIM-TRANSIT");

        // Staged, not two passes back to back: the claim's lease is what the
        // second pass must see, and a pass that has already committed would
        // prove nothing about a row in flight.
        await fixture.ClaimForTrackingAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task A_lapsed_lease_is_taken_by_another_pass()
    {
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");

        await fixture.ClaimForTrackingAsync();
        await fixture.ExpireLeasesAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Dispatched");
    }

    [Fact]
    public async Task A_row_the_fulfilment_worker_holds_is_not_polled()
    {
        // The one row both claims select: a Booked shipment whose cancellation
        // the carrier has not answered is in FulfilmentClaims' second
        // population AND due a poll. The status filters overlap by design and
        // are not what keeps the two workers apart — the LockedUntil predicate
        // is, and this is the test that says so.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(shipment.Id);

        await fixture.ClaimForFulfilmentAsync();

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await fixture.LockedUntilAsync(shipment.Id)).ShouldNotBeNull(
            "the tracking pass skipped the row rather than releasing a lease it does not hold");
    }

    [Fact]
    public async Task A_row_this_worker_holds_is_not_claimed_by_a_fulfilment_pass()
    {
        // The same overlap, from the other side. Driven through
        // ServiceFixture.RunFulfilmentPassAsync rather than a second copy of
        // that loop, so what is under test is FulfilmentClaims' own claim and
        // not this suite's idea of it.
        Shipment shipment = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.RequestCancellationAsync(shipment.Id);

        await fixture.ClaimForTrackingAsync();

        (await fixture.RunFulfilmentPassAsync()).ShouldBe(0);
        (await fixture.StatusAsync(shipment.Id)).ShouldBe(
            "Booked", "nothing cancelled at the carrier while this worker held the row");
    }

    [Fact]
    public async Task A_pass_that_throws_leaves_the_host_running()
    {
        // The claim itself failing — the database unreachable — is the case
        // ExecuteAsync's filter exists for. Driven through the loop and not
        // through ProcessBatchAsync, because what is under test is the catch
        // around the pass rather than the pass.
        using ShippingWorkerFactory broken = new(
            "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://shipping-svc:x@rabbit.invalid:5672");

        TrackingWorker worker = broken.Services.GetRequiredService<TrackingWorker>();

        // The pass itself throws, which is what the catch below is about and
        // what a carrier outage would never produce: that is caught per row.
        await Should.ThrowAsync<Exception>(
            () => worker.ProcessBatchAsync(TestContext.Current.CancellationToken));

        await worker.StartAsync(TestContext.Current.CancellationToken);

        // One tick and a margin, not two ticks: PeriodicTimer fires first one
        // whole interval after the loop starts, so one is what the assertion
        // needs and the second is thirty seconds bought for nothing.
        await Task.Delay(
            CarrierHop.TrackingPollInterval + TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // ExecuteTask is the loop, and a faulted one is the host on its way
        // down: the default BackgroundServiceExceptionBehavior stops it.
        worker.ExecuteTask!.IsFaulted.ShouldBeFalse();

        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    private TrackingWorker Worker() => fixture.Factory.Services.GetRequiredService<TrackingWorker>();
}
```

The fixture helpers this task needs — `BookedAsync`, `StatusAsync`,
`NextPollAtAsync`, `AttemptsAsync`, `LockedUntilAsync`,
`SetCarrierReferenceAsync`, `RequestCancellationAsync`,
`ClaimForTrackingAsync`, `ClaimForFulfilmentAsync` and `ExpireLeasesAsync` —
go on `ServiceFixture` beside PR-5's
`RunFulfilmentPassAsync`. All but `BookedAsync` are a single
`ExecuteAsync` or `ScalarAsync` over `shipping.Shipments`, in
`SetOutboxAttemptsAsync`'s shape. **On the fixture and not in this file**,
because Tasks 5, 6 and 7 read the same columns; PR-5's
`ShipmentFulfilmentTests` keeps its own private `StatusAsync` and
`AttemptsAsync` over `fixture.ScalarAsync`, and those stay where they are
rather than being re-pointed by this pull request.

`BookedAsync(postalCode, country = "KZ", line1 = null, city = null)` is the one
that is not a single statement: it seeds `fixture.Ordering.Addresses` with an
address carrying that postal code, publishes `OrderConfirmed`, runs **PR-5's**
`RunFulfilmentPassAsync()`, and answers the aggregate it reads back — so the
row reaches `Booked` through the fulfilment worker and this suite books nothing
by hand. The two optional address parts default to the ASCII address every test
in this task is indifferent to; Task 5 is what needs a Kazakh-script line in the
table the purge reads, and passes them by name. The lease lengths are read from
the constants that own them, never written out:

```csharp
    /// <summary>
    /// Stamps a live tracking lease on every pollable row, so a second pass
    /// meets a row in flight rather than one a previous pass has finished with.
    /// </summary>
    public Task ClaimForTrackingAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Status IN ('Booked', 'Dispatched');",
            TrackingWorker.LeaseSeconds);

    /// <summary>
    /// The same, under the fulfilment worker's lease and over its populations.
    /// Two helpers rather than one with a parameter: what a test is saying is
    /// WHICH worker holds the row, and a number passed in says that nowhere.
    /// </summary>
    public Task ClaimForFulfilmentAsync() =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET LockedUntil = DATEADD(second, {0}, SYSDATETIMEOFFSET()) " +
            "WHERE Status = 'Pending' " +
            "   OR (Status = 'Booked' AND CancellationRequestedAt IS NOT NULL AND CancellationRefusedAt IS NULL);",
            FulfilmentWorker.LeaseSeconds);

    /// <summary>Lets every lease lapse, which is what a killed replica leaves behind.</summary>
    public Task ExpireLeasesAsync() =>
        ExecuteAsync("UPDATE shipping.Shipments SET LockedUntil = NULL;");

    /// <summary>
    /// Stamps the cancellation request without asking the carrier, which is the
    /// one state a row is due to both workers at once (spec, sections 5 and 6).
    /// </summary>
    public Task RequestCancellationAsync(ShipmentId id) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET CancellationRequestedAt = SYSDATETIMEOFFSET() WHERE Id = {0};",
            id.Value);
```

**`CarrierAnswers` is PR-5's and is taken by name**, as `Carrier` and
`RunFulfilmentPassAsync` are: that plan puts
`ServiceFixture.CarrierAnswers(server, path, statusCode, method = "GET",
delay = null)` on the fixture as a static, with the `ExactMatcher` at priority
0 and the handle that removes the mapping again. This task adds a second caller
and no member, and the call below leaves the method at its default, because an
events feed is a `GET`.

**The one case that ends in a carrier fault is a suite of its own, for PR-5's
reason and not a new one.** A 503 answered to a poll is
`CarrierHop.MaxRetryAttempts + 1` failed attempts inside one call, and
`CarrierHop.CircuitBreakerMinimumThroughput` is four in a sixty-second window
with a thirty-second break — so the case leaves the collection's pipeline
holding failures that outlast the rest of this class, and a test after it would
be refused without a request leaving the process. `ResetAsync` cannot reach a
pipeline the host owns. PR-5 splits `FulfilmentFaultTests` off for exactly
this and writes `NewWorkerHost` for it; this task takes both by name:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Tracking;
using Shipping.TestSupport;
using Shouldly;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// The poll's transient row, over a host of its own because the breaker it
/// fills is sized to open (<c>CarrierHop</c>). The database, the broker and
/// the Ordering stub stay the collection's.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class TrackingFaultTests : IAsyncLifetime
{
    private readonly ServiceFixture _fixture;
    private readonly WireMockServer _carrier = WireMockServer.Start();
    private readonly ShippingWorkerFactory _host;

    public TrackingFaultTests(ServiceFixture fixture)
    {
        _fixture = fixture;
        _carrier.ReadStaticMappings(SimulatorMappings.Directory());
        _host = fixture.NewWorkerHost(_carrier.Urls[0] + "/");
    }

    public ValueTask InitializeAsync() => new(_fixture.ResetAsync());

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        _carrier.Stop();

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_failing_carrier_backs_the_row_off_and_leaves_it()
    {
        // Booked through the collection's own host and then repointed at a
        // reference this host answers 503 on, which is what the carrier being
        // down looks like to a poll: nothing about the row is wrong. The
        // mapping is registered here and not in the simulator's directory —
        // that directory is the postal-code script a person at the keyboard
        // drives (spec, section 9), and a dead events feed is no script.
        Shipment shipment = await _fixture.BookedAsync("SIM-TRANSIT");
        await _fixture.SetCarrierReferenceAsync(shipment.Id, "crr_down");
        using IDisposable down = ServiceFixture.CarrierAnswers(
            _carrier, "/v1/shipments/crr_down/events", 503);

        // Captured before the pass, because FailSql stamps NextPollAt from
        // SYSDATETIMEOFFSET() at the moment of the update: an instant read
        // after the pass is already later than the one the ladder was added to,
        // and the assertion would be against a deadline that has moved.
        DateTimeOffset before = DateTimeOffset.UtcNow;

        (await Worker().ProcessBatchAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        (await _fixture.StatusAsync(shipment.Id)).ShouldBe("Booked", "an outage is never an answer");
        (await _fixture.AttemptsAsync(shipment.Id)).ShouldBe(1);
        (await _fixture.LockedUntilAsync(shipment.Id)).ShouldBeNull("a backed-off row is released, not held");
        (await _fixture.NextPollAtAsync(shipment.Id)).ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(
            before.AddSeconds(5),
            "the dispatcher's ladder is 2^min(Attempts, 8) x 5 s, so the first backoff is at least five seconds");
    }

    private TrackingWorker Worker() => _host.Services.GetRequiredService<TrackingWorker>();
}
```

`BookedAsync` runs the collection's fulfilment pass against the collection's
carrier, and that is the point: the host this suite builds is the one that
polls, so the row it polls was booked through a pipeline this test's failures
never reach.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~Tracking"`
Expected: compile failure on `TrackingWorker`, `TrackingClaims` and the new
fixture helpers. The filter is the prefix and not the class, because this step
wrote two suites.

- [ ] **Step 3: Write the claim, in `FulfilmentClaims`' shape**

`TrackingWork.cs`:

```csharp
namespace Shipping.Infrastructure.Tracking;

/// <summary>
/// One row a tracking pass has leased, projected to exactly what the poll
/// needs — <c>FulfilmentWork</c>'s counterpart, with the carrier's reference
/// non-null because only a booked shipment has one.
/// </summary>
/// <remarks>
/// No address is projected here, which is what keeps this worker's log lines
/// free of one (spec, section 11).
/// </remarks>
public sealed record TrackingWork(Guid Id, Guid OrderId, string CarrierReference, int Attempts);
```

`TrackingClaims.cs` — `FulfilmentClaims` with the population and the schedule
column changed, and nothing else:

```csharp
using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Shipping.Infrastructure.Tracking;

/// <summary>
/// The lease and the backoff over <c>shipping.Shipments</c> for the tracking
/// pass, in <c>FulfilmentClaims</c>' shape and for its reasons.
/// </summary>
/// <remarks>
/// Raw statements rather than the repository: the claim is an atomic
/// select-and-lease the change tracker cannot express, and the failure path runs
/// when the aggregate was never loaded. A second class rather than a parameter
/// on the first: each statement names its own population and schedule column.
/// </remarks>
internal sealed class TrackingClaims(IDbConnectionFactory connections)
{
    // Atomic claim: selects and leases in one statement, so two replicas
    // cannot take the same row. READPAST skips rows another replica holds.
    //
    // The LockedUntil predicate is what keeps this pass and the fulfilment
    // pass off each other's rows, and not the status filter: a Booked shipment
    // whose cancellation the carrier has not answered is in FulfilmentClaims'
    // second population and is pollable at the same time, so the two claims
    // overlap by design. Whichever stamps LockedUntil first holds the row until
    // its lease lapses, which is what two writers to one aggregate should be.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({TrackingWorker.ClaimBatchSize}) *
            FROM shipping.Shipments WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Status IN ('Booked', 'Dispatched')
                AND NextPollAt IS NOT NULL
                AND NextPollAt <= SYSDATETIMEOFFSET()
                AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
            ORDER BY NextPollAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {TrackingWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.Id, inserted.OrderId, inserted.CarrierReference, inserted.Attempts;
        """;

    // NextPollAt where FulfilmentClaims pushes NextAttemptAt, and the same
    // ladder read from the dispatcher's own constants (spec, section 4): one
    // number tuned in two places is two backoffs that stop agreeing. Attempts
    // is the one column both workers share, which Shipment.PollApplied and
    // Shipment.ReleaseClaim both clear — a carrier that is down fails the
    // booking and the poll alike.
    //
    // Nothing is abandoned by count: the shipment's deadline is the saga's,
    // and a row that outlives it is already a review row in Ordering.
    private static readonly string FailSql =
        $"""
        UPDATE shipping.Shipments
        SET
            Attempts    = Attempts + 1,
            LockedUntil = NULL,
            NextPollAt  = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE Id = @Id;
        """;

    // Hands a claimed row back unchanged, for the next tick. Neither Attempts
    // nor NextPollAt moves: the pass ran out of time, which is not a fact about
    // the carrier.
    private const string ReleaseSql =
        "UPDATE shipping.Shipments SET LockedUntil = NULL WHERE Id = @Id;";

    public async Task<IReadOnlyList<TrackingWork>> ClaimAsync(CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        // CommandDefinition, so the token reaches the database command: with
        // the plain overload a shutdown cannot interrupt a blocked claim.
        return [.. await connection.QueryAsync<TrackingWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task FailAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(FailSql, new { Id = id }, cancellationToken: ct));
    }

    public async Task ReleaseAsync(Guid id, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(ReleaseSql, new { Id = id }, cancellationToken: ct));
    }
}
```

The table is the literal `shipping.Shipments`, exactly as `FulfilmentClaims`
writes it. **No `ShipmentsTable` value object is introduced**: `OutboxTable` is
registered because the outbox's schema is the scaffold's and a service may move
it, and this service's own migration names these two tables — one spelling, in
two claim classes that sit beside each other.

- [ ] **Step 4: Write the worker**

```csharp
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shipping.Application.Carrier;
using Shipping.Application.Tracking;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Tracking;

/// <summary>
/// The second of this service's two workers (spec, section 4): it asks the
/// carrier what has happened to each booked shipment and applies the answer.
/// </summary>
/// <remarks>
/// <c>FulfilmentWorker</c>'s shape, which is <c>OutboxDispatcher</c>'s: the claim
/// leases its rows, each row fails alone, and the loop's filter asks the token
/// because no host sets <c>BackgroundServiceExceptionBehavior</c>. A second loop
/// rather than a branch, because the two are paced by different things.
/// </remarks>
public sealed class TrackingWorker(
    IServiceScopeFactory scopes,
    ILogger<TrackingWorker> log) : BackgroundService
{
    /// <summary>
    /// How many rows one claim leases. Smaller than the outbox's, because each
    /// row here is a round trip to a third party rather than a publish.
    /// </summary>
    public const int ClaimBatchSize = 20;

    /// <summary>
    /// How long a claim holds the rows it leased. Above <see cref="PassBudget"/>
    /// and <c>CarrierHop.TotalRequestTimeout</c>, which keeps a row still being
    /// called about out of either worker's next claim — the lease is one column.
    /// </summary>
    /// <remarks>
    /// Shorter than <c>FulfilmentWorker.LeaseSeconds</c> and not one constant
    /// with it: that pass makes two hops and this one makes one, so each lease
    /// bounds its own worst case.
    /// </remarks>
    public const int LeaseSeconds = 45;

    /// <summary>
    /// The most one pass spends on carrier calls. Below §15.3's thirty-second
    /// shutdown drain and above one hop's total, so a pass always makes at least
    /// one call and never outlives the host's stop.
    /// </summary>
    public static readonly TimeSpan PassBudget = TimeSpan.FromSeconds(25);

    // Compiled once rather than parsed per call — CA1848 (ADR-019), the shape
    // §9.4's dispatcher takes.
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Tracking claim failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, Guid, int, Exception?> PollFailed =
        LoggerMessage.Define<Guid, Guid, int>(
            LogLevel.Warning,
            new EventId(2, nameof(PollFailed)),
            "Tracking poll for shipment {ShipmentId} of order {OrderId} failed; attempt {Attempt}, backing off.");

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name (ADR-019 makes it an error).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The loop's period is the poll interval and not a second number: a row
        // polled during one tick comes due exactly one period later, which is
        // the tick after it, so the two uses are one latency budget rather than
        // two that can drift (CarrierHop.TrackingPollInterval).
        using PeriodicTimer timer = new(CarrierHop.TrackingPollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The claim itself failed — database unreachable. Next tick.
                // The filter asks the token, not the exception's type: a call
                // enforcing its own deadline throws OperationCanceledException
                // while this token is still live, and testing the type would
                // let that escape and fault the whole background service.
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>
    /// One claim-and-poll pass. Returns the number of shipments whose page the
    /// handler applied — a claimed row whose command refused is not one. Public
    /// so tests drive it directly instead of racing a timer, the same seam
    /// <c>OutboxDispatcher.ProcessBatchAsync</c> offers (§12.4).
    /// </summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();

        TrackingClaims claims = claimScope.ServiceProvider.GetRequiredService<TrackingClaims>();
        IReadOnlyList<TrackingWork> claimed = await claims.ClaimAsync(ct);

        TimeProvider clock = claimScope.ServiceProvider.GetRequiredService<TimeProvider>();
        DateTimeOffset started = clock.GetUtcNow();
        int applied = 0;

        foreach (TrackingWork work in claimed)
        {
            // A row whose call could not finish inside the budget is released
            // rather than started: the next tick claims it, and nothing is left
            // leased behind a pass that ran out of time.
            if (clock.GetUtcNow() - started + CarrierHop.TotalRequestTimeout > PassBudget)
            {
                await claims.ReleaseAsync(work.Id, ct);
                continue;
            }

            try
            {
                if (await PollAsync(work, ct))
                    applied++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One row's carrier, one row's backoff. A page that cannot be
                // read is not a fact about any other shipment. The same split
                // FulfilmentWorker makes, through the claim class rather than a
                // statement of this worker's own.
                await claims.FailAsync(work.Id, ct);

                PollFailed(log, work.Id, work.OrderId, work.Attempts + 1, ex);
            }
        }

        return applied;
    }

    /// <summary>
    /// One shipment's page: read outside any transaction, applied inside one.
    /// Answers whether the handler applied it.
    /// </summary>
    /// <remarks>
    /// The call is made before the unit of work opens, which is the whole of
    /// section 4's bulkhead: a transaction held across a third party's latency
    /// is a lock nothing downstream can wait out. The claim is what makes that
    /// safe — the row is this pass's until the lease lapses.
    /// </remarks>
    private async Task<bool> PollAsync(TrackingWork work, CancellationToken ct)
    {
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();

        IReadOnlyList<CarrierEvent> page = await scope.ServiceProvider
            .GetRequiredService<ICarrierGateway>()
            .GetEventsAsync(work.CarrierReference, ct);

        DateTimeOffset nextPollAt =
            scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow()
            + CarrierHop.TrackingPollInterval;

        // The command releases the claim through Shipment.PollApplied, inside
        // the same unit of work as the page it applied: the lease is dropped by
        // the commit that used it, never by a second statement that could land
        // on its own.
        Result result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(
            new ApplyTrackingPageCommand(new ShipmentId(work.Id), page, nextPollAt),
            ct);

        // A refusal is not an applied page, and the caller's count says so:
        // ShipmentErrors.NotFound is the row a cancellation voided while this
        // pass held it, and §6.3's behaviour rolled the unit back rather than
        // moving anything.
        return result.IsSuccess;
    }
}
```

- [ ] **Step 5: Register both**

In `AddShippingInfrastructure`, beside the two registrations PR-5 left —
`services.AddScoped<FulfilmentClaims>();` and
`services.AddHostedService<FulfilmentWorker>();`:

```csharp
        // Spec section 4's second worker. AddHostedService<T> rather than a
        // factory overload, exactly as the fulfilment worker is registered, so
        // a suite that drives one pass can find and remove this registration by
        // its implementation type — a poll running underneath an assertion
        // about a row is the same race §12.4 removes the outbox dispatcher for.
        services.AddScoped<TrackingClaims>();
        services.AddHostedService<TrackingWorker>();
```

and, in `ShippingWorkerFactory`, the same removal PR-5 wrote for
`FulfilmentWorker`, one type over:

```csharp
                ServiceDescriptor tracking = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(TrackingWorker));
                services.Remove(tracking);

                services.AddSingleton<TrackingWorker>();
```

**The `AddHostedService<T>` form is load-bearing**, and this is why PR-5's is
copied rather than a factory registration written here: the removal matches on
`ImplementationType`, and `AddHostedService(sp => …)` leaves that null, so a
factory registration would be invisible to the line above and the worker would
run underneath every assertion in this suite.

`ServiceFixture` gains, beside PR-5's `RunFulfilmentPassAsync`:

```csharp
    public Task<int> RunTrackingPassAsync() =>
        Factory.Services.GetRequiredService<TrackingWorker>().ProcessBatchAsync(
            TestContext.Current.CancellationToken);
```

- [ ] **Step 6: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~Tracking"
```

Expected: 0 warnings, green, Docker running.
`A_pass_that_throws_leaves_the_host_running` takes about thirty-two seconds and
the cost is named here rather than found, as PR-5 names its overlapping pass's:
`CarrierHop.TrackingPollInterval` is the loop's period, a loop that has
not ticked says nothing about the catch inside it, and the interval is a
latency number §13.7 fixes rather than one a test may lower. §12.4's trade is
the fidelity against the seconds, and this is the fidelity.

```bash
git add src/Services/Shipping tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "feat(shipping): the tracking worker claims under a lease and polls the carrier"
```

The body argues the two numbers: why the lease is above both the hop's total
and the pass budget, and why the pass is bounded by a budget rather than by its
batch — a pass that outlives §15.3's drain is killed mid-row. It also says why
the failing carrier took a host of its own, which is the breaker and not the
database.

---

### Task 4: `ShipmentDispatched` and `ShipmentDelivered` through the outbox

**Files:**
- Modify: `src/Services/Shipping/Shipping.Application/Integration/ShippingIntegrationEventMapper.cs`
  — two entries and two `ToContract` methods, keeping PR-5's
  `RegisteredEvents`
- Modify: `tests/Shipping.Application.Tests/ShippingIntegrationEventMapperTests.cs`
  — PR-5's `The_registry_is_empty_because_nothing_here_promotes_a_shipment`
  goes, because this is the pull request that promotes one

**Interfaces:**
- Consumes: `ShipmentDispatchedDomainEvent`, `ShipmentDeliveredDomainEvent`
  (PR-1); `Common.Contracts.Shipping.V1.ShipmentDispatched` and
  `ShipmentDelivered`, whose members are
  `MessageId`, `CorrelationId`, `OccurredAt`, `OrderId`, `TrackingNumber`, all
  `required`.
- Produces: §9.3's allow-list for this service, with exactly two entries.

**What PR-5 left here, and what happens to it.** PR-5 created this suite with
one test — that `ShippingIntegrationEventMapper.RegisteredEvents` is empty,
because nothing in that pull request promoted a shipment — and added
`internal static IReadOnlyCollection<Type> RegisteredEvents => Registry.Keys;`
to the rendered mapper for it to read, together with the
`InternalsVisibleTo Include="Shipping.Application.Tests"` in
`Shipping.Application.csproj` that lets the suite see it. Both are PR-5's and
neither is re-declared here: the mapper is an `internal sealed class` and the
member follows it, so nothing in this pull request widens either. That test
goes false here and is **deleted and replaced**, not left beside its
contradiction; `RegisteredEvents` stays and
`The_registry_is_exactly_the_publishes_column` below is what now reads it, so
the member PR-5 added keeps its only caller and the registry is still asserted
as a whole rather than one mapping at a time.

- [ ] **Step 1: Write the failing test**

```csharp
using Common.Application;
using Common.Contracts.Shipping.V1;
using Common.Domain;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application;
using Shipping.Application.Integration;
using Shipping.Domain.Shipments;
using Shipping.Domain.Shipments.Events;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class ShippingIntegrationEventMapperTests
{
    private static readonly DateTimeOffset Raised = new(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);

    private static IIntegrationEventMapper Mapper()
    {
        ServiceCollection services = new();
        services.AddShippingApplication();
        return services
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider
            .GetRequiredService<IIntegrationEventMapper>();
    }

    [Fact]
    public void A_despatch_becomes_ShipmentDispatched_correlated_on_the_order()
    {
        OrderId order = new(Guid.CreateVersion7());

        ShipmentDispatched contract = Mapper()
            .Map([new ShipmentDispatchedDomainEvent(ShipmentId.New(), order, "TRK1", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<ShipmentDispatched>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value, "§9.6's saga correlates on the order");
        contract.TrackingNumber.ShouldBe("TRK1");
        contract.OccurredAt.ShouldBe(Raised);
        contract.MessageId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_delivery_becomes_ShipmentDelivered_correlated_on_the_order()
    {
        OrderId order = new(Guid.CreateVersion7());

        ShipmentDelivered contract = Mapper()
            .Map([new ShipmentDeliveredDomainEvent(ShipmentId.New(), order, "TRK1", Raised)])
            .ShouldHaveSingleItem().ShouldBeOfType<ShipmentDelivered>();

        contract.OrderId.ShouldBe(order.Value);
        contract.CorrelationId.ShouldBe(order.Value);
        contract.TrackingNumber.ShouldBe("TRK1");
    }

    [Fact]
    public void One_commit_raising_both_reaches_both_contracts_in_order()
    {
        OrderId order = new(Guid.CreateVersion7());

        IReadOnlyList<object> mapped = Mapper().Map(
            [
                new ShipmentDispatchedDomainEvent(ShipmentId.New(), order, "TRK1", Raised),
                new ShipmentDeliveredDomainEvent(ShipmentId.New(), order, "TRK1", Raised.AddHours(1))
            ]);

        // Order, not membership: a delivery staged ahead of its despatch is a
        // timeline no consumer can make sense of, and the outbox delivers in
        // OccurredAt order.
        mapped.Select(c => c.GetType()).ShouldBe([typeof(ShipmentDispatched), typeof(ShipmentDelivered)]);
    }

    [Fact]
    public void The_registry_is_exactly_the_publishes_column()
    {
        // §3.2 gives Shipping two published contracts and no third, and section
        // 1 of the spec refuses a tracking event on the bus. Asserted over
        // ShippingIntegrationEventMapper.RegisteredEvents rather than over a
        // Map, because what would go wrong is an entry nobody noticed and a Map
        // over a hand-written list can only find the entries the list names.
        ShippingIntegrationEventMapper.RegisteredEvents.ShouldBe(
            [typeof(ShipmentDispatchedDomainEvent), typeof(ShipmentDeliveredDomainEvent)],
            ignoreOrder: true);
    }

    [Fact]
    public void An_entry_in_the_registry_reaches_its_contract()
    {
        // The other half: the registry names two domain events, and each maps
        // to the contract §3.2's Publishes column gives it. Both assertions are
        // needed — a registry with the right keys and a mapping to the wrong
        // type is two green tests apart.
        OrderId order = new(Guid.CreateVersion7());

        Mapper().Map(
            [
                new ShipmentDispatchedDomainEvent(ShipmentId.New(), order, "TRK1", Raised),
                new ShipmentDeliveredDomainEvent(ShipmentId.New(), order, "TRK1", Raised),
                new UnpublishedDomainEvent(Raised)
            ])
            .Select(c => c.GetType().FullName)
            .ShouldBe(
                ["Common.Contracts.Shipping.V1.ShipmentDispatched", "Common.Contracts.Shipping.V1.ShipmentDelivered"],
                ignoreOrder: true);
    }

    [Fact]
    public void An_unregistered_domain_event_reaches_no_contract()
    {
        Mapper().Map([new UnpublishedDomainEvent(Raised)])
            .ShouldBeEmpty("an unregistered domain event is local-only, and that is not an error");
    }

    private sealed record UnpublishedDomainEvent(DateTimeOffset OccurredAt) : IDomainEvent;
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Shipping.Application.Tests --filter "FullyQualifiedName~ShippingIntegrationEventMapperTests"`
Expected: red on the two mapping tests and on the registry test — PR-5 left
the registry empty, so every `Map` returns nothing and `RegisteredEvents` is
empty. PR-5's own empty-registry test is deleted in the same edit, so it is
not in that run: it asserted the truth this pull request ends, and a test
kept as a comment about history is what the style guide's *Comments* section
refuses in code.

- [ ] **Step 3: Fill the registry**

The rendered file's placeholder comment about an empty allow-list goes, along
with the sentence PR-5's suite argued it by; both are replaced rather than
appended to, because the comment gate owns a touched block whole and a
correction beside a stale claim is two claims.

```csharp
using Common.Application;
using Common.Contracts.Shipping.V1;
using Common.Domain;
using Shipping.Domain.Shipments.Events;

namespace Shipping.Application.Integration;

/// <summary>
/// §9.3's allow-list for this service. §5.5 states the principle — never publish
/// a domain event to the bus — and this is the mechanism that makes it
/// structural rather than aspirational: a domain event absent from
/// <see cref="Registry"/> never reaches the bus, by construction, not by review.
/// </summary>
internal sealed class ShippingIntegrationEventMapper : IIntegrationEventMapper
{
    // §3.2's Publishes column for Shipping, and exactly it: two entries. A
    // tracking event is deliberately not here — the two milestones are the
    // timeline, and a third contract would carry a carrier's vocabulary onto
    // the bus for one screen.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        [typeof(ShipmentDispatchedDomainEvent)] = e => ToContract((ShipmentDispatchedDomainEvent)e),
        [typeof(ShipmentDeliveredDomainEvent)] = e => ToContract((ShipmentDeliveredDomainEvent)e)
    };

    /// <summary>
    /// The allow-list as a whole, so a suite can assert what is in
    /// <see cref="Registry"/> rather than what one <see cref="Map"/> returned:
    /// an entry nobody noticed is invisible to a call over a hand-written list.
    /// </summary>
    internal static IReadOnlyCollection<Type> RegisteredEvents => Registry.Keys;

    public IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents)
    {
        List<object> mapped = [];

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            if (!Registry.TryGetValue(domainEvent.GetType(), out Func<IDomainEvent, object>? map))
                continue;                       // Unregistered → local-only. Not an error.

            mapped.Add(map(domainEvent));       // Registered and throwing → fails the command.
        }

        return mapped;
    }

    // The correlation is the ORDER: §9.6's saga correlates every event about a
    // fulfilment on it, and the shipment's own id means nothing outside this
    // service.
    private static ShipmentDispatched ToContract(ShipmentDispatchedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        TrackingNumber = e.TrackingNumber
    };

    // The shipment's id is not carried, and the contract has no field for one:
    // a tracking number is what a buyer takes to the carrier, and an identifier
    // of this service's own would be a coupling nobody asked for (§9.1).
    private static ShipmentDelivered ToContract(ShipmentDeliveredDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.OrderId.Value,
        OccurredAt = e.OccurredAt,
        OrderId = e.OrderId.Value,
        TrackingNumber = e.TrackingNumber
    };
}
```

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Shipping.Application.Tests
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~MessageTypeMapValidatorTests"
```

Expected: green. The type-map validator is what catches a contract the map
cannot resolve by name, and it now has two to resolve.

```bash
git add src/Services/Shipping/Shipping.Application tests/Shipping.Application.Tests
git commit -m "feat(shipping): the mapper's two entries, and nothing else on the bus"
```

---

### Task 5: The retention pass

**Files:**
- Create: `src/Services/Shipping/Shipping.Infrastructure/Retention/ShippingRetentionService.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Persistence/SqlDeliveryAddressStore.cs`
  — the one sentence in its summary that this pass makes false
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs` — the
  retention service's descriptor removed and re-registered, as both workers'
  are
- Modify: `tests/Shipping.TestSupport/ServiceFixture.cs` — one pass driven,
  and the five readers and arrangers these tests need: `DeliveredAsync`,
  `VoidedWithTrackingAsync`, `AgeTerminalAsync`, `AddressCountAsync` and
  `TrackingEventCountAsync`
- Test: `tests/Shipping.Worker.Tests/ShippingRetentionTests.cs`

**Interfaces:**
- Consumes: `ShippingJurisdictionOptions` (Task 1); `IDbConnectionFactory`;
  `TimeProvider`; PR-5's `shipping.DeliveryAddresses(OrderId, CustomerId, …)`
  and PR-1's `shipping.Shipments(Id, OrderId, Status, TerminalAt, …)` and
  `shipping.TrackingEvents(ShipmentId, CarrierEventId, …)`, by those column
  names.

**Why raw statements and not `IDeliveryAddressStore`.** PR-5's port has two
members, `SaveAsync` and `GetAsync`, and deliberately no delete: §11.7's
erasure is a `DELETE … WHERE CustomerId = @CustomerId` owed whole with that
extension, and PR-5 says so in the port's own remarks. This pass deletes on a
clock rather than on a request and by a different key, so it composes its own
statements over PR-5's table and column names rather than growing that port a
third member that the one consumer it was designed for would not use.
- Produces:

```csharp
namespace Shipping.Infrastructure.Retention;

public sealed class ShippingRetentionService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public const int BatchSize = 500;
    public Task<(int Addresses, int TrackingEvents)> PurgeAsync(CancellationToken ct);
}

namespace Shipping.TestSupport;
public sealed class ServiceFixture                  // exists; five members added here
{
    public Task<Shipment> DeliveredAsync(string? line1 = null, string? city = null);
    public Task<Shipment> VoidedWithTrackingAsync();
    public Task AgeTerminalAsync(ShipmentId id, TimeSpan age);
    public Task<int> AddressCountAsync(OrderId orderId);
    public Task<int> TrackingEventCountAsync(ShipmentId id);
    public Task<(int Addresses, int TrackingEvents)> PurgeShippingRetentionAsync();
}
```

**Why a third hosted service and not a pass inside the tracking worker.** The
two differ in every dimension that decides where code lives: the tracking loop
ticks at the carrier's poll interval and a purge is measured in days; a failed
poll backs one row off while a failed purge is logged and retried next hour;
and a pass that deletes thousands of rows does not fit the budget §15.3's drain
gives a poll. `RetentionPurgeService` is already this service's shape for
exactly this job, and a second service is what lets a test drive one without
driving the other.

- [ ] **Step 1: Write the failing tests**

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Retention;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// ADR-053's two windows against the tables they are about: an address is
/// deleted its window after its shipment turns terminal, and a shipment's
/// tracking events theirs after delivery. The shipment's own record survives
/// both, which is why the address is a table of its own (spec, section 7).
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ShippingRetentionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_address_outlives_a_live_shipment_and_not_a_terminal_one_past_its_window()
    {
        // The live row is arranged second, and the order is the arrangement:
        // DeliveredAsync runs a tracking pass, and that claim takes every
        // Booked row whose poll is due — a shipment booked before it would be
        // delivered by it and stop being the live one this asserts over.
        Shipment terminal = await fixture.DeliveredAsync();
        Shipment live = await fixture.BookedAsync("050000");
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        (int addresses, _) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        (await fixture.AddressCountAsync(live.OrderId)).ShouldBe(1, "a shipment still moving still needs its address");
        (await fixture.AddressCountAsync(terminal.OrderId)).ShouldBe(0);
        (await fixture.StatusAsync(terminal.Id)).ShouldBe("Delivered", "the shipment's own record is whole");
    }

    [Fact]
    public async Task An_address_inside_its_window_is_kept()
    {
        Shipment terminal = await fixture.DeliveredAsync();
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(10));

        (int addresses, _) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(0, "eleven days is the invented deployment's window and ten is inside it");
        (await fixture.AddressCountAsync(terminal.OrderId)).ShouldBe(1);
    }

    [Fact]
    public async Task Tracking_events_go_on_their_own_window_and_not_the_address_s()
    {
        Shipment terminal = await fixture.DeliveredAsync();
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        events.ShouldBe(0, "twenty-three days is the tracking window, and twelve is inside it");

        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(24));
        (_, int later) = await fixture.PurgeShippingRetentionAsync();

        later.ShouldBeGreaterThan(0);
        (await fixture.TrackingEventCountAsync(terminal.Id)).ShouldBe(0);
        (await fixture.StatusAsync(terminal.Id)).ShouldBe("Delivered");
    }

    [Fact]
    public async Task A_voided_shipments_events_are_not_deleted_by_the_delivery_window()
    {
        // The two clocks are not one: an address goes on any terminal state and
        // tracking events only after a delivery, because a voided shipment's
        // feed is the record of what the carrier did with a parcel nobody
        // received.
        Shipment voided = await fixture.VoidedWithTrackingAsync();
        await fixture.AgeTerminalAsync(voided.Id, TimeSpan.FromDays(40));

        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        events.ShouldBe(0);
        (await fixture.TrackingEventCountAsync(voided.Id)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task No_line_of_the_purge_holds_an_address()
    {
        // Spec section 11, at the one pass that reads the address table. The
        // log takes the shipment's id and the order's id; a row count is not a
        // person.
        Shipment terminal = await fixture.DeliveredAsync(line1: "12 Абай даңғылы", city: "Алматы");
        await fixture.AgeTerminalAsync(terminal.Id, TimeSpan.FromDays(12));

        await fixture.PurgeShippingRetentionAsync();

        // CapturedLogs.Everything and not a search over formatted messages: it
        // holds the message, the state's values and any exception's
        // ToString(), so the structured half is searched too (spec, section 11).
        fixture.CapturedLogs.Everything.ShouldNotBeEmpty(
            "a capture that recorded nothing would pass whatever the pass logged");
        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => line.Contains("Абай", StringComparison.Ordinal)
                || line.Contains("Алматы", StringComparison.Ordinal));
    }
}
```

**No log-capture helper is added.** Spec section 11's export is PR-5's
`CapturedLogs` — an `ILoggerProvider` registered by `ShippingWorkerFactory`,
reset in `ResetAsync`, exposed as `fixture.CapturedLogs` with `Everything` —
and this task takes it by name. `InitializeAsync` above has already called
`ResetAsync`, so what `Everything` holds at the assertion is this test's own
run and nothing before it.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~ShippingRetentionTests"`
Expected: compile failure on `ShippingRetentionService` and the fixture
helpers.

- [ ] **Step 3: Write the pass**

```csharp
using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Shipping.Infrastructure.Retention;

/// <summary>
/// ADR-053's two statutory windows, applied: an address is deleted its window
/// after its shipment turns terminal, a shipment's tracking events theirs after
/// delivery, and the shipment's own record survives both.
/// </summary>
/// <remarks>
/// Its own hosted service, not a branch of a worker's tick: a purge is measured
/// in days and a poll in seconds. Separate from <c>RetentionPurgeService</c>
/// because these windows are statutory and its are housekeeping (ADR-053).
/// </remarks>
public sealed class ShippingRetentionService : BackgroundService
{
    /// <summary>
    /// How often a pass runs. Slow on purpose: a statutory window is measured
    /// in days, and a purge competing with two workers' claims for the same
    /// table's locks buys nothing.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Candidate keys per pass. Well under SQL Server's 2,100-parameter
    /// ceiling, because the delete carries one parameter per key.
    /// </summary>
    public const int BatchSize = 500;

    private static readonly Action<ILogger, int, string, Exception?> Purged =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Purged)),
            "Shipping retention deleted {Rows} row(s) from {Table}.");

    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(PurgeFailed)),
            "Shipping retention failed; retrying next pass.");

    private readonly IServiceScopeFactory _scopes;
    private readonly ShippingJurisdictionOptions _windows;
    private readonly ILogger<ShippingRetentionService> _log;

    public ShippingRetentionService(
        IServiceScopeFactory scopes,
        IOptions<ShippingJurisdictionOptions> windows,
        ILogger<ShippingRetentionService> log)
    {
        _scopes = scopes;
        _windows = windows.Value;
        _log = log;
    }

    // stoppingToken, not ct: CA1725 requires an override to keep the base's
    // parameter name (ADR-019 makes it an error).
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Logged and swallowed, because an exception out of ExecuteAsync
                // stops the host: a database blip during housekeeping must not
                // take the service down. The token rather than the type, for
                // §9.4's reason.
                PurgeFailed(_log, ex);
            }
        }
    }

    /// <summary>
    /// One pass over both tables. Returns the rows deleted from each. Public so
    /// tests drive it directly instead of racing a timer — the seam
    /// <c>RetentionPurgeService.PurgeAsync</c> offers, for the same reason
    /// (§12.4).
    /// </summary>
    public async Task<(int Addresses, int TrackingEvents)> PurgeAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's
        // reason: a test host substitutes it, and a row written on one clock
        // and aged on another is one no substituted clock can reason about.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Validated at start, so the value is present here by construction
        // (ADR-053); the assertion is what the annotations bought.
        int addresses = await PurgeAddressesAsync(connection, now - _windows.AddressRetention!.Value, ct);
        Purged(_log, addresses, "delivery addresses", null);

        int events = await PurgeTrackingEventsAsync(connection, now - _windows.TrackingRetention!.Value, ct);
        Purged(_log, events, "tracking events", null);

        return (addresses, events);
    }

    /// <summary>
    /// Every address whose shipment turned terminal before <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// Selected then deleted by identity rather than deleted by a join: a join in
    /// the <c>DELETE</c> holds locks on <c>Shipments</c>, the table both workers
    /// claim from with <c>UPDLOCK</c>, and a purge blocking a claim reads as a
    /// slow carrier. It is <c>RetentionPurgeService</c>'s shape and the delete by
    /// identity ADR-052 asks for.
    /// </remarks>
    private static async Task<int> PurgeAddressesAsync(
        IDbConnection connection,
        DateTimeOffset before,
        CancellationToken ct)
    {
        Guid[] orders =
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT TOP (@BatchSize) address.OrderId
                    FROM shipping.DeliveryAddresses address
                    INNER JOIN shipping.Shipments shipment ON shipment.OrderId = address.OrderId
                    WHERE shipment.TerminalAt IS NOT NULL
                        AND shipment.TerminalAt < @Before
                    ORDER BY shipment.TerminalAt;
                    """,
                    new { BatchSize, Before = before },
                    cancellationToken: ct))
        ];

        if (orders.Length == 0)
            return 0;

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM shipping.DeliveryAddresses WHERE OrderId IN @Orders;",
                new { Orders = orders },
                cancellationToken: ct));
    }

    /// <summary>
    /// Every tracking event of a shipment delivered before <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// <c>Delivered</c> and not any terminal state, because a voided shipment's
    /// feed is the record of what the carrier did with a parcel nobody
    /// received; ADR-053's clock for these rows starts at the delivery.
    /// </remarks>
    private static async Task<int> PurgeTrackingEventsAsync(
        IDbConnection connection,
        DateTimeOffset before,
        CancellationToken ct)
    {
        Guid[] shipments =
        [
            .. await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    """
                    SELECT TOP (@BatchSize) Id
                    FROM shipping.Shipments
                    WHERE Status = 'Delivered'
                        AND TerminalAt IS NOT NULL
                        AND TerminalAt < @Before
                        AND EXISTS (SELECT 1 FROM shipping.TrackingEvents e WHERE e.ShipmentId = Id)
                    ORDER BY TerminalAt;
                    """,
                    new { BatchSize, Before = before },
                    cancellationToken: ct))
        ];

        if (shipments.Length == 0)
            return 0;

        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM shipping.TrackingEvents WHERE ShipmentId IN @Shipments;",
                new { Shipments = shipments },
                cancellationToken: ct));
    }
}
```

The table names are literals here, exactly as PR-5's `FulfilmentClaims` and
`SqlDeliveryAddressStore` write theirs and Task 3's `TrackingClaims` writes
`shipping.Shipments`; Task 3 is where the decision not to introduce a
`ShipmentsTable` is argued, and the same argument covers the two tables this
pass adds to it.

- [ ] **Step 4: Correct the sentence this pass makes false**

`SqlDeliveryAddressStore`'s summary reads, from PR-5:

> The only reader and writer of `shipping.DeliveryAddresses` (spec, section 7).

There is now a second writer of that table, and it is this pass. Replace the
sentence — replaced and not appended to, because the comment gate owns a
touched block whole:

```csharp
/// <summary>
/// The only code that writes a whole address to
/// <c>shipping.DeliveryAddresses</c>, and the only one that reads one back
/// (spec, section 7). <c>ShippingRetentionService</c> and §11.7's erasure
/// delete rows of it by identity and read none, which is the whole reason the
/// address is a table of its own.
/// </summary>
```

The remarks below it are unchanged: the argument about the own connection and
the unit of work is still exactly true. `DeliveryAddressRow`'s own doc comment
is left alone — its claim is scoped to EF, and nothing here loads or saves that
type.

- [ ] **Step 5: Register it**

In `AddShippingInfrastructure`, after the options binding and in the form PR-5
registered `FulfilmentWorker` and Task 3 registered `TrackingWorker`:

```csharp
        // AddHostedService<T> for §12.4's reason, and by implementation type
        // because that is what the fixture's removal matches on: an hourly
        // timer would not race a run this short, but "the pass never happened"
        // and "the pass spared the row" are the same green, so a test drives it
        // rather than waits for it.
        services.AddHostedService<ShippingRetentionService>();
```

and, in `ShippingWorkerFactory`, the same two lines the other two hosted
services already take, one type over:

```csharp
                ServiceDescriptor retention = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(ShippingRetentionService));
                services.Remove(retention);

                services.AddSingleton<ShippingRetentionService>();
```

`Shipping.Infrastructure.Retention` is already imported there, for Task 1
step 5's two `UseSetting` calls. `ServiceFixture` gains

```csharp
    public Task<(int Addresses, int TrackingEvents)> PurgeShippingRetentionAsync() =>
        Factory.Services.GetRequiredService<ShippingRetentionService>().PurgeAsync(
            TestContext.Current.CancellationToken);
```

beside `RunFulfilmentPassAsync` and `RunTrackingPassAsync`, and the five
arrangers and readers Step 1's tests are written against, beside Task 3's
`ClaimForTrackingAsync` and `RequestCancellationAsync`. Each is on the fixture
rather than in the suite for Task 3's reason: Tasks 5, 6 and 7 read the same
columns.

```csharp
    /// <summary>
    /// A shipment the carrier has delivered, with its address row and its
    /// tracking events in place: booked through the fulfilment worker and then
    /// polled once, so the state and the rows are the workers' own.
    /// </summary>
    /// <remarks>
    /// "050000" is the simulator's delivered script (spec, section 9). The two
    /// optional parts carry a non-ASCII address into the table the purge reads.
    /// </remarks>
    public async Task<Shipment> DeliveredAsync(string? line1 = null, string? city = null)
    {
        Shipment shipment = await BookedAsync("050000", line1: line1, city: city);

        await RunTrackingPassAsync();

        return shipment;
    }

    /// <summary>
    /// A voided shipment that already carries a tracking event. It is the one
    /// shape that separates ADR-053's two clocks — terminal, so its address is
    /// due, and never delivered, so its feed is not.
    /// </summary>
    /// <remarks>
    /// The tracking row is written here rather than polled for: every simulator
    /// script either promotes the shipment out of the cancellable population or
    /// is refused whole (spec, section 9).
    /// </remarks>
    public async Task<Shipment> VoidedWithTrackingAsync()
    {
        Shipment shipment = await BookedAsync("SIM-TRANSIT");

        await ExecuteAsync(
            """
            INSERT INTO shipping.TrackingEvents (ShipmentId, CarrierEventId, Status, OccurredAt, RecordedAt)
            VALUES ({0}, 'evt-in-transit', 'InTransit', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """,
            shipment.Id.Value);

        await RequestCancellationAsync(shipment.Id);
        await RunFulfilmentPassAsync();

        return shipment;
    }

    /// <summary>
    /// Moves a terminal shipment's clock back, so a window measured in days can
    /// be crossed inside a test. <c>TerminalAt</c> and not the row's creation,
    /// because ADR-053's two clocks both start there — and set absolutely, so a
    /// test may age one row twice without compounding.
    /// </summary>
    public Task AgeTerminalAsync(ShipmentId id, TimeSpan age) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET TerminalAt = DATEADD(second, {0}, SYSDATETIMEOFFSET()) WHERE Id = {1};",
            -(int)age.TotalSeconds,
            id.Value);

    /// <summary>
    /// How many delivery addresses are held for one order — one or none, since
    /// the table is keyed by the order (spec, section 7).
    /// </summary>
    public Task<int> AddressCountAsync(OrderId orderId) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.DeliveryAddresses WHERE OrderId = {0};",
            orderId.Value);

    /// <summary>How many tracking events a shipment still holds.</summary>
    public Task<int> TrackingEventCountAsync(ShipmentId id) =>
        ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM shipping.TrackingEvents WHERE ShipmentId = {0};",
            id.Value);
```

`BookedAsync` grows the two optional address parts in Task 3 rather than here,
because that is where it is written; `DeliveredAsync` is the only caller that
passes them.

- [ ] **Step 6: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~ShippingRetentionTests"
```

```bash
git add src/Services/Shipping tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "feat(shipping): the retention pass deletes addresses and tracking events by identity"
```

The body argues the third hosted service over a branch of the tracking loop,
and the select-then-delete over a joined delete: the join would take locks on
the table both workers claim from.

---

### Task 6: `shipping.shipments.waiting`

**Files:**
- Create: `src/Services/Shipping/Shipping.Infrastructure/Observability/IShipmentStats.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Observability/ShipmentStats.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Observability/ShipmentMetrics.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Carrier/CarrierMetrics.cs`
  — its remark's last clause, which this task makes true
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Observability/MetricsInitialiser.cs`
- Modify: `src/Services/Shipping/Shipping.Infrastructure/DependencyInjection.cs`
- Modify: `tests/Shipping.TestSupport/ServiceFixture.cs` — `ReadWaitingGauge`,
  `SetAttemptsAsync`
- Test: `tests/Shipping.Worker.Tests/WaitingGaugeTests.cs`

**The gauge is this pull request's, and nothing is checked for first.** PR-5's
self-review states that `shipping.shipments.waiting` lands here and argues why:
two of the states a row waits in are populations the tracking worker creates.
There is no grep step and no branch — PR-5 added no gauge, and a task that
opens by asking whether the previous pull request did something is a task whose
outcome is not written down.

**A class of its own, in `AddressMetrics`' shape.** PR-2 predicted both of this
meter's later instruments in `CarrierMetrics`' remarks, and PR-5 answered the
first of them with a separate `AddressMetrics` creating
`factory.Create(CarrierMetrics.MeterName)` — `IMeterFactory` caches by name, so
the classes hold one meter between them and §13.2's single `AddMeter` line
covers all three. This task follows PR-5 rather than PR-2's wording: bolting a
gauge onto `CarrierMetrics` would give a class about the carrier an
`IShipmentStats` and an `ILogger` in its constructor, which is `OutboxMetrics`'
shape wearing the carrier's name.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.Infrastructure.Observability;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// Spec section 11's third instrument: rows past their first backoff, by state.
/// Delivery lag stops when a consumer starts, so it never sees a worker waiting
/// on a carrier — this gauge is the only signal that does.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class WaitingGaugeTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_row_past_its_first_backoff_is_counted_under_its_own_state()
    {
        Shipment booked = await fixture.BookedAsync("SIM-TRANSIT");
        await fixture.SetAttemptsAsync(booked.Id, 1);

        Shipment healthy = await fixture.BookedAsync("050000");

        IReadOnlyList<(string State, double Value)> measured = fixture.ReadWaitingGauge();

        measured.ShouldContain(m => m.State == "Booked" && m.Value == 1);
        measured.ShouldContain(m => m.State == "Pending" && m.Value == 0,
            "every state reports, because a state missing from a sum reads as a healthy zero");
        healthy.Id.ShouldNotBe(booked.Id);
    }

    [Fact]
    public void All_three_instruments_land_on_the_one_meter_section_13_2_exports()
    {
        // The factory caches by name, which is what lets CarrierMetrics,
        // AddressMetrics and ShipmentMetrics each create §11's meter and still
        // produce one. If it ever stopped, the later classes' instruments would
        // be on a meter no AddMeter line names and would be collected by
        // nothing (§13.2).
        IMeterFactory factory = fixture.Factory.Services.GetRequiredService<IMeterFactory>();

        factory.Create(CarrierMetrics.MeterName).ShouldBeSameAs(factory.Create(CarrierMetrics.MeterName));
    }
}
```

`ReadWaitingGauge` is the fixture's `MeterListener` over **this host's** meter
— never one matched by name, because a listener is process-wide and another
host's gauge would be enabled with it and its callback run against a container
that may be gone. **The shape is `MetricsRegistrationTests`' outbox-gauge
listener**, which PR-1 renders into `tests/Shipping.Worker.Tests`, and not
PR-2's `UnavailableCount` or PR-5's `RefusedCount`: those two count a
`Counter<long>` as it is recorded and never call
`RecordObservableInstruments()`, and an observable gauge publishes nothing
until a listener asks it to. `double` and not `long`, because
`CreateObservableGauge` over `Measurement<double>` is `OutboxMetrics`' shape
and the listener sees what the callback produced.

```csharp
    /// <summary>
    /// Spec section 11's waiting gauge, read once per call: one entry per
    /// state the callback reported, with the value it produced. An observable
    /// gauge is published on demand, so this asks for a collection rather than
    /// waiting for one, and the filter is on the meter instance and never on
    /// its name. The instrument name is written out rather than shared with
    /// the registration, which would make the reading agree with itself
    /// whatever the gauge is called.
    /// </summary>
    public IReadOnlyList<(string State, double Value)> ReadWaitingGauge()
    {
        // Resolved before the listener starts: the gauge is created in the
        // metrics type's constructor, so a listener attached first sees no
        // instrument published and reports an empty list instead of a failure.
        Factory.Services.GetRequiredService<ShipmentMetrics>();
        Meter mine = Factory.Services.GetRequiredService<IMeterFactory>().Create(CarrierMetrics.MeterName);

        List<(string State, double Value)> measured = [];
        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "shipping.shipments.waiting")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>(
            (_, value, tags, _) => measured.Add((StateOf(tags), value)));

        listener.Start();
        listener.RecordObservableInstruments();

        return measured;
    }

    /// <summary>
    /// The <c>state</c> tag a measurement carries, or the empty string where
    /// it carries none — which no assertion matches, so a tag renamed fails
    /// the assertion that reads it rather than being silently dropped.
    /// </summary>
    private static string StateOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == "state")
                return tag.Value?.ToString() ?? "";
        }

        return "";
    }
```

with three `using` lines added to the fixture in sorted position, each only
where the file does not already carry one: `System.Diagnostics.Metrics`,
`Shipping.Infrastructure.Carrier` for `CarrierMetrics.MeterName`, and
`Shipping.Infrastructure.Observability` for `ShipmentMetrics`.

`SetAttemptsAsync` goes on the fixture beside it, in `SetOutboxAttemptsAsync`'s
shape one table over — a single statement over the column both workers write,
so a test says which row is past its first backoff instead of failing a pass to
arrive at one:

```csharp
    /// <summary>
    /// Seeds a prior attempt count through the same column the workers write,
    /// which is what puts a row past its first backoff. Explicit rather than
    /// hidden in a builder, so no state carries between tests (§12.8).
    /// </summary>
    public Task SetAttemptsAsync(ShipmentId id, int attempts) =>
        ExecuteAsync(
            "UPDATE shipping.Shipments SET Attempts = {0} WHERE Id = {1};",
            attempts,
            id.Value);
```

Run it now:
`dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~WaitingGaugeTests"` —
expected, a compile failure on `Shipping.Infrastructure.Observability`'s new
types and on `ReadWaitingGauge`/`SetAttemptsAsync`.

- [ ] **Step 2: Write the stats port and its reader**

`IShipmentStats.cs` and `ShipmentStats.cs` are `IOutboxStats` and `OutboxStats`
one table over, with the same arguments that file already makes — a gauge
callback runs on the collector's thread, a `commandTimeout` starts only once a
connection is open, and a metrics type that loads the database it measures is a
monitor that causes the symptom. The connect half of that bound is the
registration's: step 4 composes this type's connection string with
`ConnectTimeout = OutboxStats.ConnectTimeoutSeconds`. `OutboxTable` has no
counterpart here, so the statement spells `shipping.Shipments` as every other
statement in this service does.

```csharp
namespace Shipping.Infrastructure.Observability;

/// <summary>
/// The one question spec section 11's waiting gauge asks: how many shipments in
/// each state are past their first failed pass.
/// </summary>
public interface IShipmentStats
{
    int WaitingCount(string state);
}
```

and the reader:

```csharp
using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;

namespace Shipping.Infrastructure.Observability;

/// <summary>
/// <see cref="IShipmentStats"/> over one aggregate query, in <c>OutboxStats</c>'
/// shape and on its arguments: a connection factory rather than a scope, a
/// bounded command timeout, and a short cache, because a metrics type that
/// loads the database it measures is a monitor that causes the symptom. A read
/// that throws surfaces as an absent series, which
/// <c>ShipmentMetrics.PerState</c> is what makes true.
/// </summary>
internal sealed class ShipmentStats(IDbConnectionFactory connections) : IShipmentStats, IDisposable
{
    /// <summary>
    /// Short enough that a stalled state is visible within one export interval,
    /// long enough that a burst of scrapes is not a burst of queries. One entry
    /// per state rather than one shared snapshot, so a state nobody asks about
    /// costs nothing.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A bound on the statement, because this runs inside a gauge callback on
    /// the metric reader's own thread: a wait here serialises with every other
    /// callback in the pass and takes unrelated telemetry down with it.
    /// </summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>
    /// Rows past their first failed pass, whichever worker took them. Both
    /// claim paths increment <c>Attempts</c> and <c>Shipment.ReleaseClaim</c>
    /// clears it, so one column answers for both (spec, section 4).
    /// </summary>
    private const string WaitingSql =
        """
        SELECT COUNT(*)
        FROM shipping.Shipments
        WHERE Status = @Status
            AND Attempts > 0;
        """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public int WaitingCount(string state) =>
        _cache.GetOrCreate(state, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            return connection.ExecuteScalar<int>(
                new CommandDefinition(
                    WaitingSql,
                    new { Status = state },
                    commandTimeout: CommandTimeoutSeconds));
        });

    public void Dispose() => _cache.Dispose();
}
```

`Attempts > 0` is the predicate and not a lease or a schedule: a row that has
failed a pass is waiting whichever worker took it, which is exactly what makes
one counter answer for both. `FulfilmentClaims.FailSql` and
`TrackingClaims.FailSql` increment that column, and `Shipment.ReleaseClaim` —
which `Shipment.PollApplied` calls — clears it, so the gauge reads the same
counter both workers keep (spec, section 4).

- [ ] **Step 3: Write `ShipmentMetrics`**

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;

namespace Shipping.Infrastructure.Observability;

/// <summary>
/// §11's gauge over shipments past their first failed pass, by state, on the
/// same meter as the carrier's and the address's instruments.
/// </summary>
/// <remarks>
/// <c>CarrierMetrics.MeterName</c> rather than a string of its own, so §13.2's
/// one <c>AddMeter</c> line covers them. A class of its own because this one
/// reads the database, in <c>OutboxMetrics</c>' shape; a singleton built eagerly
/// by <c>MetricsInitialiser</c>, since the gauge is a callback the meter holds.
/// </remarks>
public sealed class ShipmentMetrics
{
    /// <inheritdoc cref="OutboxMetrics"/>
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Waiting-shipment gauge read failed. PerState runs once per collection, so this "
            + "collection omits every state rather than reporting some — absent rather than "
            + "wrong, see ShipmentMetrics.");

    public ShipmentMetrics(IMeterFactory factory, IShipmentStats stats, ILogger<ShipmentMetrics> logger)
    {
        Meter meter = factory.Create(CarrierMetrics.MeterName);

        // Rows past their first backoff, by state. The tag value is the enum's
        // own name, never a hand-written string: the Status column stores
        // ToString() and a PromQL query on another spelling matches no series
        // and never fires, which looks exactly like health.
        meter.CreateObservableGauge(
            "shipping.shipments.waiting",
            () => PerState(stats, logger),
            unit: "{shipment}",
            description: "Shipments past their first failed pass, by state.");
    }

    /// <summary>
    /// One measurement per state, read from the enum rather than from a list
    /// here: a state added to <see cref="ShipmentStatus"/> and forgotten at a
    /// call site would be a state with no gauge and therefore no alert.
    /// </summary>
    /// <remarks>
    /// The read is contained, because an observable callback that throws does
    /// not fail alone — <c>MeterListener.RecordObservableInstruments</c> drops
    /// the rest of the pass with it, as <c>OutboxMetrics.PerLane</c> argues.
    /// </remarks>
    private static List<Measurement<double>> PerState(IShipmentStats stats, ILogger logger)
    {
        List<Measurement<double>> measurements = [];

        foreach (ShipmentStatus status in Enum.GetValues<ShipmentStatus>())
        {
            int waiting;

            try
            {
                waiting = stats.WaitingCount(status.ToString());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Every state is dropped, not just this one: half a reading is
                // worse than none, because a state missing from a
                // `sum by (state)` reads as a healthy zero rather than no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(waiting, Tag(status)));
        }

        return measurements;
    }

    private static KeyValuePair<string, object?> Tag(ShipmentStatus status) =>
        new("state", status.ToString());
}
```

- [ ] **Step 4: Register it, and close `CarrierMetrics`' prediction**

In `AddShippingInfrastructure`, beside the `IOutboxStats`/`OutboxMetrics` pair
the render left and PR-5's `AddSingleton<AddressMetrics>()`:

```csharp
        // Its own connection factory with the bounded connect timeout, for
        // OutboxStats' reason: this runs inside a gauge callback, and a command
        // timeout bounds only the statement. One argument and not two, because
        // shipping.Shipments is this service's own table and is spelled inside
        // the type, where OutboxStats takes the registered OutboxTable.
        //
        // Through a factory rather than as a built instance, exactly as
        // OutboxStats is: the class holds a MemoryCache and is IDisposable, and
        // the container disposes what it constructed and never what it was
        // handed.
        services.AddSingleton<IShipmentStats>(
            _ => new ShipmentStats(new SqlConnectionFactory(metricsConnectionString)));
        services.AddSingleton<ShipmentMetrics>();
```

reusing the `metricsConnectionString` the render already composes for
`OutboxStats`, and `ShipmentMetrics` joins `MetricsInitialiser`'s constructor
with its `ArgumentNullException.ThrowIfNull` guard — the file's own remark says
membership asks "can this service run for an hour without constructing it", and
nothing injects this type at all.

`CarrierMetrics`' remark predicts two later instruments on this meter. PR-5
delivered the first and this task delivers the second, so the prediction has
nothing left to predict: **delete the remark whole** rather than leave a
sentence naming instruments that now exist beside it. The comment gate owns a
touched block whole, and a two-clause prediction with both clauses answered is
not corrected by editing one of them.

- [ ] **Step 5: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~WaitingGaugeTests"
py -3.12 deploy/observability/check.py
```

Expected: green, and the observability gate exits 0 — the gauge joins a meter
§13.2 already exports, so no `AddMeter` line is owed and none is added.

```bash
git add src/Services/Shipping tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "feat(shipping): shipping.shipments.waiting counts rows past their first backoff"
```

---

### Task 7: The fixture is ADR-053's made-up deployment

**Files:**
- Modify: `tests/Shipping.TestSupport/ServiceFixture.cs`
- Test: `tests/Shipping.Worker.Tests/MadeUpDeploymentTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Shipping.Domain.Shipments;
using Shipping.TestSupport;
using Shouldly;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// ADR-053 rule 2: a made-up jurisdiction proves rule 1. The suite runs under
/// invented windows and an address country no country uses, and nothing in the
/// service had a line changed for it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class MadeUpDeploymentTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_address_in_ZZ_books_at_the_carrier()
    {
        // Address already constructs ZZ, and nothing between the store and the
        // simulator may learn a country: a type that knew one would refuse the
        // deployment this record exists to keep possible.
        Shipment shipment = await fixture.BookedAsync(postalCode: "050000", country: "ZZ");

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Booked");
        (await fixture.CarrierReferenceAsync(shipment.Id)).ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_whole_tracking_and_retention_path_runs_under_the_invented_windows()
    {
        Shipment shipment = await fixture.BookedAsync(postalCode: "050000", country: "ZZ");

        await fixture.RunTrackingPassAsync();

        (await fixture.StatusAsync(shipment.Id)).ShouldBe("Delivered");

        await fixture.AgeTerminalAsync(shipment.Id, TimeSpan.FromDays(24));
        (int addresses, int events) = await fixture.PurgeShippingRetentionAsync();

        addresses.ShouldBe(1);
        events.ShouldBeGreaterThan(0);
    }
}
```

- [ ] **Step 2: Point the fixture at the invented windows**

In `ServiceFixture.InitializeAsync`, the factory construction gains nothing:
`ShippingWorkerFactory`'s defaults are already `InventedAddressRetention` and
`InventedTrackingRetention` (Task 1), which is the point — the whole suite runs
under a jurisdiction that does not exist and no test opts in.

Add one line of comment above the factory construction saying so, citing
ADR-053 rule 2.

The fixture gains one reader, which is the only thing above this suite needs
that no earlier task wrote — the carrier's reference, read the way Task 3's
`StatusAsync` and Task 5's counts read their columns:

```csharp
    /// <summary>The reference the carrier answered a booking with, or null.</summary>
    public Task<string?> CarrierReferenceAsync(ShipmentId id) =>
        ScalarAsync<string?>(
            "SELECT Value = CarrierReference FROM shipping.Shipments WHERE Id = {0};",
            id.Value);
```

- [ ] **Step 3: Run; commit**

```bash
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~MadeUpDeploymentTests"
```

```bash
git add tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "test(shipping): the worker suite runs as ADR-053's made-up deployment"
```

---

### Task 8: One despatch, two services

**Files:**
- Modify: `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj`
- Create: `tests/Platform.IntegrationTests/PlatformFixture.cs`
- Create: `tests/Platform.IntegrationTests/DespatchFanOutTests.cs`

**Interfaces:**
- Consumes: `Common.Contracts.Shipping.V1.ShipmentDispatched`;
  `Ordering.TestSupport.OrderingApiFactory`;
  `Inventory.TestSupport.InventoryApiFactory` and their migrator runners.
- Produces: `PlatformFixture`, one SQL Server, one broker and two Redis
  containers shared by two hosts.

- [ ] **Step 1: The project's new references**

```xml
  <ItemGroup>
    <!--
      §12's cross-service leg, and the one place a test assembly may name two
      services: this suite exists to hold two of them to each other, which is
      why it already references two Infrastructure projects. Each service's own
      fixture still refuses the crossing (§4.3) and duplicates its helpers
      rather than sharing them.
    -->
    <ProjectReference Include="..\Ordering.TestSupport\Ordering.TestSupport.csproj" />
    <ProjectReference Include="..\Inventory.TestSupport\Inventory.TestSupport.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="MassTransit" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" />
    <PackageReference Include="Testcontainers.MsSql" />
    <PackageReference Include="Testcontainers.RabbitMq" />
    <PackageReference Include="Testcontainers.Redis" />
    <!-- Reset-by-truncation between tests (§12.4), and the open connection it
         inspects. Named here on the register's honesty rule PR-5 applies: this
         assembly constructs a Respawner and a SqlConnection itself, whatever
         the two TestSupport references carry transitively. -->
    <PackageReference Include="Respawn" />
    <PackageReference Include="Microsoft.Data.SqlClient" />
  </ItemGroup>
```

The package is `Respawn`; `Respawner` is the type step 4's `ResetAsync`
constructs from it. None of the seven carries a `Version=`: every one is pinned
in `Directory.Packages.props` already, which is why this PR adds no Appendix B
row.

- [ ] **Step 2: Write the failing test**

`tests/Platform.IntegrationTests/DespatchFanOutTests.cs`:

```csharp
using Common.Contracts.Shipping.V1;
using Inventory.TestSupport;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Platform.IntegrationTests;

/// <summary>
/// §3.2's fan-out, over one broker: a despatch is one fact and two services act
/// on it independently. Ordering's saga finalises the fulfilment and Inventory
/// fulfils the reservation, and neither is the other's trigger — which is what
/// the two single-host tests below are for, because with both hosts running a
/// coincidence and a causation look the same.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DespatchFanOutTests(PlatformFixture fixture) : IClassFixture<PlatformFixture>, IAsyncLifetime
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(30);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Orderings_saga_finalises_with_no_Inventory_host_running()
    {
        Guid orderId = Guid.CreateVersion7();

        using OrderingApiFactory ordering = fixture.Ordering();
        await fixture.SeedConfirmedSagaAsync(orderId);

        await PublishAsync(ordering.Services, Despatch(orderId));

        await Eventually(
            () => fixture.SagaRowsAsync(orderId),
            expected: 0,
            because: "Confirmed's ShipmentDispatched sends MarkOrderShipped and finalises (§9.6)");
    }

    [Fact]
    public async Task Inventorys_reservation_is_fulfilled_with_no_Ordering_host_running()
    {
        Guid orderId = Guid.CreateVersion7();

        using InventoryApiFactory inventory = fixture.Inventory();
        await fixture.SeedHeldReservationAsync(orderId);

        await PublishAsync(inventory.Services, Despatch(orderId));

        await Eventually(
            () => fixture.FulfilledReservationsAsync(orderId),
            expected: 1,
            because: "ShipmentDispatchedHandler sends FulfilReservationCommand and the stock has left (§3.2)");
    }

    [Fact]
    public async Task One_despatch_reaches_both_services()
    {
        Guid orderId = Guid.CreateVersion7();

        using OrderingApiFactory ordering = fixture.Ordering();
        using InventoryApiFactory inventory = fixture.Inventory();

        await fixture.SeedConfirmedSagaAsync(orderId);
        await fixture.SeedHeldReservationAsync(orderId);

        await PublishAsync(ordering.Services, Despatch(orderId));

        await Eventually(() => fixture.SagaRowsAsync(orderId), 0, "the saga finalises");
        await Eventually(() => fixture.FulfilledReservationsAsync(orderId), 1, "the reservation is fulfilled");

        // Each service consumed its own copy off its own queue; neither read
        // the other's outcome, and nothing reached an error queue.
        (await fixture.ErrorQueueDepthAsync()).ShouldBe(0);
    }

    private static ShipmentDispatched Despatch(Guid orderId) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = DateTimeOffset.UtcNow,
        OrderId = orderId,
        TrackingNumber = "TRK-PLATFORM"
    };

    private static Task PublishAsync(IServiceProvider host, ShipmentDispatched message) =>
        host.GetRequiredService<IPublishEndpoint>().Publish(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            TestContext.Current.CancellationToken);

    private static async Task Eventually(Func<Task<int>> read, int expected, string because)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeliveryBudget;
        int actual = -1;

        while (DateTimeOffset.UtcNow < deadline)
        {
            actual = await read();

            if (actual == expected)
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        }

        actual.ShouldBe(expected, because);
    }
}
```

- [ ] **Step 3: Run to see it fail**

Run: `dotnet test tests/Platform.IntegrationTests`
Expected: compile failure — `PlatformFixture` does not exist.

- [ ] **Step 4: Write the fixture**

`PlatformFixture` is Ordering's `ServiceFixture` with the parts that make it
one service's removed and the parts that make it two added: one SQL Server
holding both databases, one broker holding both accounts, and the two Redis
servers shared. Four things about it are decisions rather than code, so they
are said here and cited there.

**The broker is built from `deploy/compose/rabbitmq`'s Dockerfile**, because
Ordering's saga schedules and the delayed exchange is in that image and not in
the base tag (ADR-021). **The two accounts are both real**: the container's
default is `ordering-svc` and Inventory's connection string is composed over
the same container for `inventory-svc`, which `definitions.json` already
declares, so ADR-036's split is exercised rather than collapsed. **One Redis
pair serves two services**, which is honest here — the keys are
service-prefixed and this suite's subject is the bus. And **both prior states
are seeded rather than choreographed**: the subject is one event reaching two
services, and arranging it through each service's whole lifecycle would make
this suite fail for either service's reasons rather than for the fan-out's.

`tests/Platform.IntegrationTests/PlatformFixture.cs`:

```csharp
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using Inventory.TestSupport;
using Microsoft.Data.SqlClient;
using Ordering.TestSupport;
using Respawn;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using Testcontainers.Redis;
using Xunit;
// Aliased because both assemblies name a ServiceFixture, and this class names
// a member after each service.
using InventoryFixture = Inventory.TestSupport.ServiceFixture;
using OrderingFixture = Ordering.TestSupport.ServiceFixture;

namespace Platform.IntegrationTests;

/// <summary>
/// One SQL Server, one broker and two Redis servers, over which either real
/// host can be started (§12.4).
/// </summary>
/// <remarks>
/// The containers are shared and the hosts are not: a test starts whichever
/// hosts its claim is about, which is what tells a fan-out from a coincidence.
/// Neither service's own fixture is reused, because §4.3 keeps each inside its
/// boundary and this assembly is the one §4.3 spends on the crossing.
/// </remarks>
public sealed class PlatformFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    /// <summary>
    /// §8.1's two servers, shared by both hosts: <c>AddRedisConnections</c>
    /// reads both keys eagerly and §8.5's behaviour claims a key on every
    /// protected command, so an unreachable default would not do.
    /// </summary>
    private readonly RedisContainer _redisCache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _redisCoordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    /// <summary>
    /// Built in <see cref="InitializeAsync"/>, because the image it runs does
    /// not exist until this fixture builds it.
    /// </summary>
    private RabbitMqContainer? _rabbit;

    /// <summary>
    /// The write each account needs to publish what this suite publishes.
    /// Copied from each service's fixture rather than shared, because §4.3
    /// lets no test helper cross a boundary — and widened for both, because
    /// both hosts publish here.
    /// </summary>
    private const string OrderingHarnessWrite =
        "^(ordering-|inventory-commands|payments-commands|Common\\.Contracts|" +
        "Ordering\\.Infrastructure\\.Messaging:|MassTransit:)";

    private const string InventoryHarnessWrite =
        "^(inventory-|Common\\.Contracts|Inventory\\.Infrastructure\\.Messaging:|MassTransit:)";

    private Respawner? _orderingRespawner;
    private Respawner? _inventoryRespawner;
    private string _orderingConnectionString = null!;
    private string _inventoryConnectionString = null!;
    private string _inventoryBroker = null!;

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        // Its own image name and WithCleanUp(false), for the reason Ordering's
        // fixture spells out: the build context is tarred under the image's
        // name, so two suites sharing a name race on a file rather than on
        // Docker. Every layer but the tag is shared, so this is a cache hit.
        IFutureDockerImage broker = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(BrokerContextPath())
            .WithDockerfile("Dockerfile")
            .WithName("ashamray-test-broker-platform:4.1-delayed")
            .WithCleanUp(false)
            .Build();

        // The container's default account is Ordering's; Inventory's is a
        // second account on the same broker, which the imported definitions
        // declare with its own permissions (ADR-036). One account for both
        // hosts would make the grant this suite runs under nobody's.
        _rabbit = new RabbitMqBuilder()
            .WithImage(broker)
            .WithUsername("ordering-svc")
            .WithPassword("local-dev-ordering")
            .Build();

        await broker.CreateAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(
            _sql.StartAsync(TestContext.Current.CancellationToken),
            _rabbit.StartAsync(TestContext.Current.CancellationToken),
            _redisCache.StartAsync(TestContext.Current.CancellationToken),
            _redisCoordination.StartAsync(TestContext.Current.CancellationToken));

        _inventoryBroker =
            $"amqp://inventory-svc:local-dev-inventory@{_rabbit.Hostname}:{_rabbit.GetMappedPublicPort(5672)}";

        await WidenWriteForTheHarnessAsync("ordering-svc", OrderingHarnessWrite);
        await WidenWriteForTheHarnessAsync("inventory-svc", InventoryHarnessWrite);

        _orderingConnectionString = DatabaseNamed("Ordering");
        _inventoryConnectionString = DatabaseNamed("Inventory");

        await MigrateAsync("Ordering", OrderingFixture.RunMigratorAsync(_orderingConnectionString));
        await MigrateAsync("Inventory", InventoryFixture.RunMigratorAsync(_inventoryConnectionString));
    }

    /// <summary>A host over these containers, for a test that wants Ordering running.</summary>
    public OrderingApiFactory Ordering() =>
        new(
            _orderingConnectionString,
            _rabbit!.GetConnectionString(),
            _redisCache.GetConnectionString(),
            _redisCoordination.GetConnectionString());

    /// <summary>The same, under the second broker account.</summary>
    public InventoryApiFactory Inventory() =>
        new(
            _inventoryConnectionString,
            _inventoryBroker,
            _redisCache.GetConnectionString(),
            _redisCoordination.GetConnectionString());

    /// <summary>
    /// §12.4's reset, once per database. Two <c>Respawner</c>s because each
    /// reads one connection's schema graph, and they are kept because that
    /// read is the expensive half rather than the reset.
    /// </summary>
    public async Task ResetAsync()
    {
        _orderingRespawner = await ResetOneAsync(_orderingConnectionString, "ordering", _orderingRespawner);
        _inventoryRespawner = await ResetOneAsync(_inventoryConnectionString, "inventory", _inventoryRespawner);
    }

    private static async Task<Respawner> ResetOneAsync(string connectionString, string schema, Respawner? respawner)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        // dbo is excluded, so EF's migration history survives the truncation.
        respawner ??= await Respawner.CreateAsync(
            connection,
            new RespawnerOptions
            {
                DbAdapter = DbAdapter.SqlServer,
                SchemasToInclude = [schema]
            });

        await respawner.ResetAsync(connection);

        return respawner;
    }

    /// <summary>
    /// The saga row §9.6 leaves in <c>Confirmed</c>, which is the state a
    /// despatch finalises from. The columns omitted carry defaults, and the
    /// correlation is the order id because that is what the saga correlates on.
    /// </summary>
    public Task SeedConfirmedSagaAsync(Guid orderId) =>
        ExecuteAsync(
            _orderingConnectionString,
            """
            INSERT INTO ordering.OrderFulfilmentStates
                (CorrelationId, CurrentState, OrderId, Total, Currency, StartedAt)
            VALUES (@OrderId, 'Confirmed', @OrderId, 19.99, 'EUR', SYSDATETIMEOFFSET());
            """,
            orderId);

    /// <summary>
    /// The stock and reservation rows a held reservation is, one line over one
    /// product. <c>Reserved</c> is the held status in
    /// <c>ReservationStatus</c>'s own spelling, and the unavailable-ids column
    /// is the empty JSON list its converter writes.
    /// </summary>
    public Task SeedHeldReservationAsync(Guid orderId) =>
        ExecuteAsync(
            _inventoryConnectionString,
            """
            DECLARE @ProductId uniqueidentifier = NEWID();

            INSERT INTO inventory.StockItems (ProductId, Available, Reserved, UpdatedAt)
            VALUES (@ProductId, 0, 1, SYSDATETIMEOFFSET());

            INSERT INTO inventory.Reservations (OrderId, Status, CreatedAt, UpdatedAt, UnavailableProductIds)
            VALUES (@OrderId, 'Reserved', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), '[]');

            INSERT INTO inventory.ReservationLines (OrderId, ProductId, Quantity)
            VALUES (@OrderId, @ProductId, 1);
            """,
            orderId);

    /// <summary>Saga rows for one order — one while it runs, none once it finalises.</summary>
    public Task<int> SagaRowsAsync(Guid orderId) =>
        ScalarAsync(
            _orderingConnectionString,
            "SELECT COUNT(*) FROM ordering.OrderFulfilmentStates WHERE CorrelationId = @OrderId;",
            orderId);

    /// <summary>Reservations for one order the stock has left.</summary>
    public Task<int> FulfilledReservationsAsync(Guid orderId) =>
        ScalarAsync(
            _inventoryConnectionString,
            """
            SELECT COUNT(*)
            FROM inventory.Reservations
            WHERE OrderId = @OrderId
                AND Status = 'Fulfilled';
            """,
            orderId);

    /// <summary>
    /// Messages sitting in every <c>_error</c> queue, read from the broker
    /// itself. MassTransit declares one on a consumer's first fault, so a
    /// fault's arrival there is an outcome no table shows.
    /// </summary>
    public async Task<int> ErrorQueueDepthAsync()
    {
        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "list_queues", "--quiet", "--no-table-headers", "name", "messages"],
            TestContext.Current.CancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not list the broker's queues (exit {result.ExitCode}). stderr: {result.Stderr}");
        }

        int total = 0;

        foreach (string line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split('\t', StringSplitOptions.TrimEntries);

            if (columns.Length == 2 && columns[0].EndsWith("_error", StringComparison.Ordinal))
                total += int.Parse(columns[1], CultureInfo.InvariantCulture);
        }

        return total;
    }

    /// <summary>
    /// The container hands out a connection to <c>master</c>; each service owns
    /// a database of its own (§7.1), and its migrator is what creates it.
    /// </summary>
    private string DatabaseNamed(string database)
    {
        DbConnectionStringBuilder connection = new() { ConnectionString = _sql.GetConnectionString() };
        connection["Database"] = database;

        return connection.ConnectionString;
    }

    /// <summary>
    /// Drives the real §7.4 job host through the public static each fixture
    /// already exposes, so this suite copies no wiring. A non-zero code throws
    /// here rather than surfacing as a missing table in every test below.
    /// </summary>
    private static async Task MigrateAsync(string service, Task<int> run)
    {
        int exitCode = await run;

        if (exitCode != 0)
            throw new InvalidOperationException($"{service}'s migrator exited {exitCode}.");
    }

    /// <summary>
    /// The harness publishes a despatch under each host's own account, which
    /// the deployed grant refuses (ADR-036). Only the test container's write
    /// moves; <c>configure</c> and <c>read</c> are read back from the imported
    /// definitions, so the topology is still judged by the scope that deploys.
    /// </summary>
    private async Task WidenWriteForTheHarnessAsync(string user, string write)
    {
        (string configure, string read) = ImportedGrant(user);

        ExecResult result = await _rabbit!.ExecAsync(
            ["rabbitmqctl", "set_permissions", "-p", "/", user, configure, write, read],
            TestContext.Current.CancellationToken);

        // A silent failure here would surface as every test retrying a refused
        // publish until its budget ran out, naming a message rather than a
        // permission.
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not widen {user}'s broker permissions for the harness "
                + $"(exit {result.ExitCode}). stdout: {result.Stdout} stderr: {result.Stderr}");
        }
    }

    // The mapped file rather than the container, because it is the same text
    // the broker imported and it can be read before anything starts.
    private static (string Configure, string Read) ImportedGrant(string user)
    {
        string path = Path.Combine(BrokerContextPath(), "definitions.json");
        using JsonDocument definitions = JsonDocument.Parse(File.ReadAllText(path));

        foreach (JsonElement entry in definitions.RootElement.GetProperty("permissions").EnumerateArray())
        {
            if (entry.GetProperty("user").GetString() != user || entry.GetProperty("vhost").GetString() != "/")
                continue;

            return (entry.GetProperty("configure").GetString()!, entry.GetProperty("read").GetString()!);
        }

        throw new InvalidOperationException(
            $"{path} grants {user} nothing on the default vhost, so there is no scope to preserve.");
    }

    /// <summary>
    /// <c>deploy/compose/rabbitmq</c>, found by walking up to the directory
    /// holding <c>Platform.slnx</c>. It throws rather than falling back: a
    /// fixture that quietly used the stock tag would lose the plugin the saga's
    /// schedules need, and lose it on whichever machine had the odd layout.
    /// </summary>
    private static string BrokerContextPath()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                continue;

            string context = Path.Combine(dir.FullName, "deploy", "compose", "rabbitmq");
            if (!File.Exists(Path.Combine(context, "Dockerfile")))
            {
                throw new InvalidOperationException(
                    $"Found the solution at {dir.FullName} but no Dockerfile at {context} (§14.1, ADR-021).");
            }

            return context;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the broker image cannot be built.");
    }

    /// <summary>
    /// A statement outside either host, for arranging. ADO rather than either
    /// <c>DbContext</c>: this assembly holds neither, and a parameter is what
    /// keeps the order id out of the statement text.
    /// </summary>
    private static async Task ExecuteAsync(string connectionString, string sql, Guid orderId)
    {
        await using SqlConnection connection = new(connectionString);
        using SqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("@OrderId", orderId);

        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>One count outside either host, for asserting. Same rule on the parameter.</summary>
    private static async Task<int> ScalarAsync(string connectionString, string sql, Guid orderId)
    {
        await using SqlConnection connection = new(connectionString);
        using SqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("@OrderId", orderId);

        await connection.OpenAsync(TestContext.Current.CancellationToken);

        return (int)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        // Each teardown runs even when an earlier one throws: a failed SQL
        // disposal must not leave the broker running for the rest of the job.
        // The broker is null-guarded because InitializeAsync can throw on
        // either side of its assignment; the others are field initialisers.
        try
        {
            await _sql.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_rabbit is not null)
                    await _rabbit.DisposeAsync();
            }
            finally
            {
                try
                {
                    await _redisCache.DisposeAsync();
                }
                finally
                {
                    await _redisCoordination.DisposeAsync();
                }
            }
        }
    }
}
```

No factory is held: each test disposes the hosts it started, so a class that
starts one host leaves the other's queues unbound and the fan-out's two halves
stay separable.

- [ ] **Step 5: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Platform.IntegrationTests
```

Expected: green with a Docker daemon; without one the container legs fail on
`Failed to connect to Docker endpoint` and are never skipped.

```bash
git add tests/Platform.IntegrationTests
git commit -m "test(platform): one despatch reaches Ordering's saga and Inventory's handler"
```

The body argues the two project references — this suite is the one assembly
§4.3 already spends on the crossing — and why the two prior states are seeded
rather than choreographed.

---

### Task 9: The two new keys, in Compose and in §15.4

**Files:**
- Modify: `deploy/compose/services/shipping.yml`
- Modify: `docs/backend-architecture/15-cicd-deployment.md`
- Modify: `docs/backend-architecture/11-identity-authorization.md`
- Modify: `docs/secrets.md`

- [ ] **Step 1: Compose**

In `shipping-worker`'s `environment:`, **below the whole block PR-5 added** —
`AddressSource__BaseUrl` and the three `Identity__Client__*` keys, which sit
under PR-2's `Carrier__BaseUrl` and `Carrier__ApiKey` — so PR-5's four lines
and their two comments stay one block and this pull request adds a third
rather than splitting the second:

```yaml
      # ADR-053's statutory windows, as values this deployment is given. A
      # developer's stack is no jurisdiction either, so these are that record's
      # made-up ones rather than either country's — a real window belongs in a
      # values file and never in a file everybody runs.
      Jurisdiction__AddressRetention: "11.00:00:00"
      Jurisdiction__TrackingRetention: "23.00:00:00"
```

The two values are the same strings as `ShippingWorkerFactory`'s
`InventedAddressRetention` and `InventedTrackingRetention`, and deliberately
not read from them: a Compose file names no C# constant, and ADR-053 rule 2's
point is that the made-up jurisdiction is supplied by each deployment rather
than compiled in anywhere. **That the two stacks with no jurisdiction carry the
same pair is not a failure of §15.4's rule**, which asks for a member that
differs between environments: a statutory window differs wherever a deployment
has a statute, and neither of these two does.

They are configuration and not credentials, so they take no `.env.example`
entry — PR-5's `SHIPPING_CLIENT_SECRET` line there is untouched — no
`docs/secrets.md` rotation row and no local-development exception row; §14.1's
defaults rule reaches only values that carry a credential. No
`.github/secret-scan/allowed/deploy.txt` entry is owed either, for the same
reason, so PR-5's entry for this file is the only one it has.

- [ ] **Step 2: §15.4's inventory gains two rows**

After the two `PaymentProvider__*` rows:

```markdown
| `Jurisdiction__AddressRetention` | Config | Helm `jurisdiction.addressRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a delivery address, and the host refuses to start without it |
| `Jurisdiction__TrackingRetention` | Config | Helm `jurisdiction.trackingRetention` → ConfigMap | ✓ — **Shipping only**; ADR-053's statutory window for a shipment's tracking events |
```

**The Source column names the Helm key although the chart is PR-7's**, under
the rule PR-5's Task 6 step 3 states and argues for `AddressSource__BaseUrl`.
PR-7 therefore rewrites neither of these two rows; what it owes them is the
chart that renders the spelling they already carry.

- [ ] **Step 3: The sentence and the callout's close**

ADR-053 rule 1 gives this edit to the first pull request that binds such a
class, and this is it. The sentence at §15.4 reads today:

> **This is the only options type in the solution, and that is the point.** The
> tempting next line is a `ServiceOptions`-shaped bag — batch sizes, poll
> intervals, retry caps — bound to an `Ordering` section that no environment ever
> sets.

and becomes:

> **There are two options types in the solution, and both had to earn it.**
> `Identity:Client` holds a secret that differs per environment, and
> `Jurisdiction` holds the statutory windows
> [ADR-053](adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
> makes values a deployment is given. The tempting third is a
> `ServiceOptions`-shaped bag — batch sizes, poll intervals, retry caps — bound
> to an `Ordering` section that no environment ever sets.

The rest of that paragraph — `ValidateOnStart` gating boot on a section nobody
supplies, `[Required]` on any member stopping every host, and there being no
third outcome — is unchanged, because the argument is about the bag and not
about the count.

The callout's close reads today:

> `Identity:Client` earns its options type by holding a secret that must differ
> per environment, and it is the only thing here that does.

and becomes:

> `Identity:Client` earns its options type by holding a secret that must differ
> per environment, and `Jurisdiction` earns one because a statutory window is a
> fact about where a deployment runs: ADR-053 rule 2 gives a developer's stack
> invented ones precisely because it is no jurisdiction, and a deployment that
> is one supplies its own.

- [ ] **Step 4: The two restatements that cite it by value**

Both say what §15.4 said, by the count rather than by the rule, and both go
false with Step 3. Each is corrected to cite the owner and keep its own
argument — which is what `docs/change-locality.md` asks of a mention.

`docs/backend-architecture/11-identity-authorization.md`, in the paragraph
about the authority being read eagerly. Before:

> It is deliberately **not** an
> options type with `ValidateOnStart` — [§15.4](15-cicd-deployment.md) makes
> `ServiceIdentityOptions` the only options type in the solution and argues why,
> and a second bag bound to a section holding one value is the shape that rule
> forbids.

After:

> It is deliberately **not** an options type with `ValidateOnStart` —
> [§15.4](15-cicd-deployment.md) admits one only where a member would differ
> between Compose, the fixture and production, and a bag bound to a section
> holding one value is the shape that rule forbids.

The sentence before it — the posture `AddSqlServer` and
`AddMassTransitMessaging` already take — and the one after it, about §12.4's
fixture comment, are both unchanged: neither counts the options types.

`docs/secrets.md`, in *Before adding an options type at all*. Before:

> `Identity:Client` is the only options type in the solution, and it earns that
> by holding a secret that must differ per environment.

After:

> `Identity:Client` earns its options type by holding a secret that must differ
> per environment; §15.4's inventory is where every type that has earned one is
> listed.

The two paragraphs above it in that section — that §15.4 is blunt about this,
and that an options type needs a member differing between environments — are
unchanged, because the rule is what they state and only the count moved.

Everything else in the corpus that mentions the count stays: the four
other copies — `Catalog.TestSupport`, `Inventory.TestSupport`,
`Ordering.TestSupport` and `Common.Web.Tests` — argue why *their* host binds
nothing, each is true of the host it is written about, none is in this touch
set, and
`docs/change-locality.md` is explicit about a stale restatement met in passing.
Shipping's own copy is not among them: PR-1 rendered the same block into
`ShippingWorkerFactory`, where it is false of this host, and Task 1 step 6
rewrites it.

- [ ] **Step 5: Run the document checks**

```bash
py -3.12 .github/licence-gate/licence_gate.py
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

then `/check-links` and `/validate-blueprint`, because two chapters changed.

- [ ] **Step 6: Commit**

```bash
git add deploy/compose/services/shipping.yml docs/backend-architecture/15-cicd-deployment.md \
        docs/backend-architecture/11-identity-authorization.md docs/secrets.md
git commit -m "docs: §15.4 gains the jurisdiction windows and a second options type"
```

The body names ADR-053 rule 1 as what assigns this edit to this pull request,
and says which restatements were corrected and which were deliberately left.

---

### Task 10: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings. `TreatWarningsAsErrors` makes
  that the build's own claim, and the one thing to read by eye is that no
  `#pragma` was added anywhere.
- [ ] `dotnet test Platform.slnx` — green, with a Docker daemon running: the
  Shipping worker suite, `Platform.IntegrationTests` and every other
  `Category=Integration` leg are never skipped.
- [ ] `py -3.12 -m unittest discover -s .github/licence-gate` then
  `py -3.12 .github/licence-gate/licence_gate.py` — both exit 0. The suite
  first, which is `docs/testing.md`'s order and the only way a red gate means
  the tree rather than the gate.
- [ ] `py -3.12 -m unittest discover -s .github/secret-scan` then
  `py -3.12 .github/secret-scan/secret_scan.py` — both exit 0. **No allow-list
  row is written from this plan, and none is expected.** The broker password in
  `PlatformFixture` is §14.1's local-development default, written the way
  `Ordering.TestSupport` and `Inventory.TestSupport` write theirs — a
  `.WithPassword("…")` argument on the container builder, and an interpolated
  connection string for the second account — and `credential-assignment` fires
  only where a credential-shaped **name** is assigned a quoted literal, which
  neither shape is. Neither of those two fixtures carries a row in
  `.github/secret-scan/allowed/tests.txt` today, and that absence is the
  measurement rather than an oversight to copy. A finding the gate does report
  is closed by a row in that file carrying the digest the gate computed and
  never one written from here — which is why the path is in the touch set and
  why the gate is run before the row is believed to be unnecessary.
- [ ] `py -3.12 -m unittest discover -s .github/pipeline-gate` then
  `py -3.12 .github/pipeline-gate/pipeline_gate.py` — both exit 0. No service
  directory is added here, so the filter written in PR-1 still covers it.
- [ ] `py -3.12 deploy/observability/check.py` — exit 0.
- [ ] `git fetch origin main` then
  `py -3.12 .github/comment-gate/comment_gate.py --base origin/main` — exit 0.
  The blocks it judges are the deleted `CarrierMetrics` remark, the rendered
  mapper's replaced comment, `SqlDeliveryAddressStore`'s replaced summary and
  `ShippingWorkerFactory`'s rendered authority block, and a touched block
  counts whole — which is why each of the four is replaced rather than
  corrected beside its stale half, and why the last of them, already sixteen
  lines, comes back as ten.
- [ ] `py -3.12 .github/locality-gate/locality_gate.py --base origin/main` after
  the PR body exists — exit 0, with the class row spelled `A+D+E` and the touch
  set paths only.
- [ ] `/validate-blueprint` and `/check-links`, because §15.4 and §11.5 moved.
- [ ] The PR body in the house form, its touch-set table paths only and its
  reasons beneath, closing the issue the spec's section 3 row is tracked under
  if one exists and saying so explicitly if none does.

---

## Self-review

**Spec coverage.**

- Section 3's PR-6 row — the tracking worker and its lease, the monotonic
  promotion, both events through the outbox, the retention pass over addresses
  and tracking events, the cross-service container test → Tasks 2, 3, 4, 5, 8.
- Section 4 — the second `BackgroundService` in `OutboxDispatcher`'s shape,
  reached here through `FulfilmentWorker`/`FulfilmentClaims`, paced by the
  carrier's rate limit, with its own lease over `NextPollAt`/`LockedUntil`; the
  claim before the call; the backoff on the row, read from
  `OutboxDispatcher`'s own constants as PR-5's is; the loop surviving its tick
  and the pass fitting §15.3's drain → Task 3, with the inequality asserted in
  its first test and the two claims' shared `LockedUntil` asserted in the two
  that follow the lapsed-lease case.
- Section 5 — `Booked`→`Dispatched` on `Collected`, `Booked`/`Dispatched`→
  `Delivered` on `Delivered` with the despatch raised first when it was never
  raised, `TrackingEvent` keyed `(ShipmentId, CarrierEventId)`, `Unrecognised`
  moving nothing, monotonic by rank and not by arrival → PR-1's aggregate,
  driven by Task 2's handler and asserted in its reversed-page and
  unrecognised-status tests.
- Section 6 — the `SIM-LATE` case publishing `ShipmentDispatched` when
  `Collected` arrives → PR-1's `CarrierRefusedCancellation` test proves the
  domain half and Task 3's worker is what drives it; the `SIM-LATE` end-to-end
  leg belongs to PR-5's cancel path and is not re-proved here.
- Section 7's retention — `ShippingJurisdictionOptions` binding
  `Jurisdiction__AddressRetention` and `Jurisdiction__TrackingRetention`,
  `[Required]` with a stated bound, validated at start as §15.4 validates
  `ServiceIdentityOptions`, refused not clamped, neither joining
  `RetentionPolicy`; the pass deleting by identity → Tasks 1 and 5. The
  amendment ADR-053 rule 1 assigns to the first pull request to bind such a
  class → Task 9.
- Section 8 — both events through the outbox via
  `ShippingIntegrationEventMapper`'s two entries, and the registry test →
  Task 4.
- Section 9 — `GetEventsAsync` and the 404 that is an empty page → Task 2's
  empty-page test and Task 3's poll; the adapter itself is PR-2's.
- Section 11 — `shipping.shipments.waiting`, on `CarrierMetrics.MeterName` in
  `AddressMetrics`' shape → Task 6; no log line holding an address → Task 5's
  assertion over PR-5's `CapturedLogs`, and `TrackingWork`, which projects no
  address.
- Section 12 — the worker legs (the inequality, the staged second pass, the
  lapsed lease, the pass that throws, and the two that hold each worker off a
  row the other has leased), the made-up deployment, and
  `Platform.IntegrationTests`'s cross-service leg → Tasks 3, 7 and 8.
- Section 13 — §15.4 in PR-6 → Task 9.

**Type consistency.** `Shipment.PollApplied(DateTimeOffset)` is produced by
Task 2 and consumed by Task 2's handler alone.
`ApplyTrackingPageCommand(ShipmentId, IReadOnlyList<CarrierEvent>,
DateTimeOffset)` is produced by Task 2 and consumed by Task 3's worker.
`ShipmentErrors.NotFound` is produced by Task 2, returned by its handler and
read by Task 3's pass count and by Task 2's own suite.
`TrackingWork`, `TrackingClaims.ClaimAsync/FailAsync/ReleaseAsync` and
`TrackingWorker.ClaimBatchSize`, `LeaseSeconds`, `PassBudget` and
`ProcessBatchAsync` are produced by Task 3 and consumed by its own suite and by
`ServiceFixture.RunTrackingPassAsync`.
`ShippingJurisdictionOptions.SectionName`, `AddressRetention` and
`TrackingRetention` are produced by Task 1 and consumed by Task 5's pass, Task
9's Compose unit and §15.4's two rows.
`ShippingWorkerFactory.InventedAddressRetention` and
`InventedTrackingRetention` are produced by Task 1 and consumed by Tasks 1 and
7. `IShipmentStats.WaitingCount(string)` and `ShipmentMetrics` are produced by
Task 6 and consumed by `MetricsInitialiser`. On `ServiceFixture`,
`BookedAsync(postalCode, country, line1, city)`, `StatusAsync`,
`NextPollAtAsync`, `AttemptsAsync`, `LockedUntilAsync`,
`SetCarrierReferenceAsync`, `RequestCancellationAsync`,
`ClaimForTrackingAsync`, `ClaimForFulfilmentAsync`, `ExpireLeasesAsync` and
`RunTrackingPassAsync` are Task 3's, and `NewWorkerHost` and `CarrierAnswers`
are PR-5's, consumed by Task 3's second suite; `DeliveredAsync`,
`VoidedWithTrackingAsync`, `AgeTerminalAsync`, `AddressCountAsync`,
`TrackingEventCountAsync` and `PurgeShippingRetentionAsync` are Task 5's;
`ReadWaitingGauge` and `SetAttemptsAsync` are Task 6's; and
`CarrierReferenceAsync` is Task 7's. Each is written in the task that first
needs it, and no later task consumes one no task wrote.
`PlatformFixture.Ordering()`,
`Inventory()`, `SeedConfirmedSagaAsync`, `SeedHeldReservationAsync`,
`SagaRowsAsync`, `FulfilledReservationsAsync` and `ErrorQueueDepthAsync` are
produced and consumed inside Task 8.

Everything else is an earlier pull request's under **that** pull request's
spelling, and this plan was reconciled against PR-5's plan rather than against
the spec's prose where the two differ:

- PR-1's: `Shipment` — a `sealed` class, not `partial`, which is why
  `PollApplied` is added to `Shipment.cs` — with `For`, `Book`, `Record`,
  `Cancel`, `CarrierCancelled`, and `Attempts`, `NextAttemptAt`,
  `LockedUntil`, `NextPollAt`, `TerminalAt`; `ShipmentId`, `OrderId`,
  `ShipmentStatus`, `TrackingStatus`, the two domain events; `ServiceFixture`
  with `ScalarAsync`, `ExecuteAsync`, `ColumnsAsync`, `ResetAsync`;
  `MetricsInitialiser` and the `OutboxStats`/`OutboxMetrics` pair.
- PR-2's: `ICarrierGateway.GetEventsAsync`, `CarrierEvent`,
  `CarrierHop.TrackingPollInterval`, `CarrierHop.TotalRequestTimeout`,
  `CarrierMetrics.MeterName` (`"Shipping.Outbound"`), `SimulatorMappings`.
- PR-5's: `Shipping.Application.Shipments.IShipmentRepository` with
  **`GetAsync(ShipmentId, …)`** — there is no `GetForUpdateAsync`, and Task 2
  reads through `GetAsync`; `Shipment.ReleaseClaim()`, which `PollApplied`
  calls rather than repeats; `IDeliveryAddressStore` and
  `shipping.DeliveryAddresses`' eight columns, which Task 5 deletes from by
  identity; `FulfilmentClaims`, `FulfilmentWork`,
  `FulfilmentWorker.ClaimBatchSize`/`LeaseSeconds`/`RunOnceAsync`, which
  Task 3 copies the shape of and Task 3's suite drives;
  `ShippingWorkerFactory`'s five parameters and `ServiceFixture`'s `Carrier`,
  `Ordering`, `CapturedLogs` and `RunFulfilmentPassAsync`, all extended and
  none re-declared; and `ShippingIntegrationEventMapper.RegisteredEvents`,
  which Task 4 keeps and repoints.

**Deliberately left to a later PR.**

- **The chart, the canary row, §13.6's two rules and the runbook they share**
  (PR-7). The delivery-lag and queue-backlog alerts read what this PR starts
  publishing, and a rule naming a chart that does not exist is a gate failure
  rather than an alert.
- **The `carrier` and `jurisdiction` Helm capabilities**, which are
  `docs/secrets.md`'s third place for these two keys and cannot exist before the
  chart.
- **A webhook, a tracking event on the bus, and a second carrier adapter** —
  spec sections 1 and 14 refuse all three, and the port is the seam a second
  adapter would prove.
- **§11.7's erasure consumer.** Spec section 7 names the path — a `DELETE` from
  `DeliveryAddresses` by `CustomerId` — and that extension brings it; the
  retention pass here deletes on a clock and not on a request.
- **The four code comments that restate §15.4's count** — in
  `Catalog.TestSupport`, `Inventory.TestSupport`, `Ordering.TestSupport` and
  `Common.Web.Tests`. Each
  argues why the host it is written about binds nothing, which stays true;
  correcting them is owed by whichever pull request next touches those blocks.

# Notifications PR-4 — the seven consumers, the order record and the templates — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Notifications receive. `notifications-events` binds §3.2's seven
events; each consumer writes its `NotificationLog` intent row in one
transaction, and Ordering's three also write the `OrderRecords` row the other
four wait on for a customer — created by whichever arrives first, its
cancellation set once and never cleared. The values a row stores take a
versioned JSON format whose reader refuses a version it does not know; a value
another service wrote is bounded and checked at the consumer and dropped,
never faulted on. Twenty-one templates and three cancellation maps ship as
embedded resources, a renderer fills them by language, culture and zone, and
`NotificationsJurisdictionOptions` refuses at start a deployment the shipped
files, the runtime or ADR-052's contact numbers cannot serve. Nothing sends:
the worker that renders and sends is PR-5's.

**Architecture:** each of the seven `IIntegrationEventHandler<T>`s maps its
contract to one `RecordNotificationCommand` and dispatches it, so the write
runs inside §6.3's pipeline and `TransactionBehavior`'s one transaction;
`RecordNotificationHandler` is the one writer — order record first, then the
notice, both idempotent, every superseded arrival a logged return and never a
throw. Rendering lives in `Notifications.Application.Rendering` with no
engine: `TemplateSet` parses every file once, refusing an unknown placeholder,
a placeholder in a subject and a malformed name, and splits each body into
literal and placeholder parts so `TemplateRenderer` substitutes and never
parses. A cancellation's phrase map is a file beside its template and
versioned with it. The options class and its validator sit in
`Notifications.Infrastructure.Jurisdiction`, beside the renderer's
registration, and the validator runs the renderer's own refusals so a
deployment is refused at start by the same code that would have failed at
the send. The receive endpoint is §9.5's printed form, bare
`RetryPolicy.Standard` with no redelivery, and the worker suite's live
binding test is the measurement PR-1 deferred: that `notifications-svc`, with
no `Common.Contracts` write, binds all seven.

**Tech Stack:** MassTransit over RabbitMQ, EF Core with SQL Server,
`System.Text.Json` for the stored parameters, ICU cultures and IANA zones
through `CultureInfo` and `TimeZoneInfo`, xUnit v3 with Shouldly and
Testcontainers, stdlib Python 3.12 for the gates.

**Spec:** `docs/superpowers/specs/2026-10-02-notifications-service-design.md`,
sections 1 (how a notice learns its customer, what a cancellation says, one
message per event, the content type, no engine, the languages, the
internationalisation answer), 3 (PR-4's row), 5 (the record's first move), 6
(`OrderRecords`, the `Parameters` format, `NotificationsJurisdictionOptions`
and its windows, `AddOrderRecords`), 7 (templates and rendering), 8 (values
another service wrote), 10 (`notifications-events`, the order record's
writers, `MessagingRegistrationTests`, the narrow account), 11 (the five
`Jurisdiction__*` keys), 13 (the Application and Worker rows this PR owns)
and 14 (§15.4 in PR-4).

**Proven before it was written.** The Application layer below — every type
under `Records/`, `Intake/` and `Rendering/`, the twenty-four resource files
and the Application suite's eight test files — and the Infrastructure types
that need no rendered host — `OrderRecordConfiguration`, the two
repositories, the options class, its validator, the messaging registration
and the registrations Tasks 6 and 7 add — were compiled in a scratch copy under
this repository's `Directory.Build.props`, `Directory.Packages.props` and
`.editorconfig`, against the real `Common.Application`, `Common.Contracts` and
`Common.Infrastructure`: 0 warnings, 155 Application tests and 10 validator
tests green. Two facts came from that run and not from reading, and each is
argued where it lands: a resource named `order-placed.v1.en.txt` is split
into a satellite assembly unless its item says `WithCulture="false"`, and the
Application project's referenced assemblies gain exactly
`Microsoft.Extensions.Logging.Abstractions`, `System.Memory` and
`System.Text.Json`. The parts that need PR-1's render — the context, the
migration, the factory, the fixture and the container suites — are written
against PR-1's plan and Shipping's built code, and are not run here.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Notifications/**`, `tests/Notifications.*`, `deploy/compose/services/notifications.yml`, `docs/backend-architecture/15-cicd-deployment.md`
- Reasons, since the row above is paths only. A is the Notifications slice —
  its Application, Infrastructure and their suites. D is the Compose unit,
  which gains the five `Jurisdiction__*` keys the host now refuses to start
  without, and §15.4, which gains their rows and amends the sentence and the
  callout naming what `Jurisdiction` holds. E is two `*.csproj` files inside
  the slice: `Notifications.Application.csproj` gains the `Common.Contracts`
  reference, the logging package and the embedded resources, and
  `Notifications.Infrastructure.csproj` gains an `InternalsVisibleTo` for the
  worker suite. No repo-wide mutex is held: no `Platform.slnx` line, no
  `Directory.Packages.props` pin, no shared fixture.
- **Depends on PR-1 and PR-3 having merged**, in the spec's order: PR-4
  comes after 1 and 3, since `ContactRetention`'s floor (spec, section 6)
  reads `ContactOptions.StaleCeiling`, which is PR-3's, so this branch is cut
  after PR-3 merges. PR-2 is independent of this one in either order.
  From PR-1: `Notifications.Application.Records`' `Notification`,
  `NotificationStatus`, `NotificationReasons` and `NotificationLimits` (with
  `MaxParametersLength = 2000`); `NotificationsDbContext` with
  `NotificationLog` and default schema `notifications`; the rendered
  `Notifications.Infrastructure.Messaging.DependencyInjection` and
  `RetryPolicy`; `AddNotificationsInfrastructure`; the
  `NotificationsWorkerFactory(string connectionString, string rabbitConnectionString, …)`
  and `ServiceFixture` (`ResetAsync`, `ScalarAsync`, `ExecuteAsync`,
  `InboxAsync(Guid)`, `ColumnsAsync`, the base's protected `BrokerRowsAsync`)
  in `tests/Notifications.TestSupport`; `IntegrationCollection`; the rendered
  `MessagingRegistrationTests`, `IdempotencyOptInTests`, `DatabaseSmokeTests`
  and Application `ArchitectureTests`; the Compose unit; the broker account
  `notifications-svc` with `write` `^(notifications-|MassTransit:)`. From
  PR-3: `Notifications.Application.Contacts.ContactOptions` with its
  `StaleCeiling`, registered as a singleton by `AddNotificationsApplication`;
  `tests/Notifications.Worker.Tests/Unreachable.cs`; the `AddContactRecords`
  migration, so this PR's is the eighth applied. Where an earlier PR spelled
  one of these differently, the spelling moves and nothing else here does.
- **No new pin and no Appendix B row.** `Microsoft.Extensions.Logging.Abstractions`
  is pinned and is already `Common.Application`'s; `System.Text.Json` is in
  the shared framework. Every `PackageReference` added carries no `Version=`.
- **The consumers call nothing outside the service's own database**, so no
  fault they meet is a wait (ADR-052), and nothing here throws a value
  another service wrote into a queue: a value that fails `InboundValues` is
  dropped and the notice recorded without it.
- **Nothing here sends, suppresses or reads a contact.** ADR-049's
  suppression, the order-record wait, the render's stamp on the row and the
  retention passes over the three windows are PR-5's; this PR stores what
  PR-5 reads — the cancellation's origin and reason on the order record —
  and binds the windows the passes will read.
- **No log line holds a value another service wrote.** A dropped value is
  logged by its member's name, with the event's id and the template's key.
- Comments say why and cite the owner — a section, an ADR or a symbol, never
  a pull request, a test or the superpowers spec — a summary is one sentence,
  a `<remarks>` is cited and four lines, a block is five. Prose at 80
  columns, code at 120, British spelling, explicit local types, file-scoped
  namespaces, braces on two statements or more and on one that wraps, one
  space before `=`, `=>` and `{`.
- `py -3.12`, never `python`. Container tests are
  `[Collection(nameof(IntegrationCollection))]` and never skipped.
- Every step that adds behaviour writes its test first.

---

### Task 1: The order record and the two ports

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Records/OrderRecord.cs`
- Create: `src/Services/Notifications/Notifications.Application/Records/OrderRecordLimits.cs`
- Create: `src/Services/Notifications/Notifications.Application/Records/INotificationRepository.cs`
- Create: `src/Services/Notifications/Notifications.Application/Records/IOrderRecordRepository.cs`
- Test: `tests/Notifications.Application.Tests/OrderRecordTests.cs`

**Interfaces:**
- Consumes: PR-1's `Notification`.
- Produces:

```csharp
namespace Notifications.Application.Records;

public sealed class OrderRecord
{
    public Guid OrderId { get; }
    public Guid CustomerId { get; }
    public DateTimeOffset? CancelledAt { get; }
    public string? CancelReason { get; }
    public string? CancelOrigin { get; }
    public DateTimeOffset RecordedAt { get; }
    public static OrderRecord For(Guid orderId, Guid customerId, DateTimeOffset now);
    public bool Cancel(string? reason, string? origin, DateTimeOffset at);
}
public static class OrderRecordLimits { public const int MaxCodeLength = 32; }
public interface INotificationRepository
{
    Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct);
    void Add(Notification notification);
}
public interface IOrderRecordRepository
{
    Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct);
    void Add(OrderRecord record);
}
```

**Three decisions the spec leaves to the code**, each with a test:

- **`CancelledAt` is the event's `OccurredAt`**, and `RecordedAt` the
  consumer's clock. The first is the fact a reader of the record asks about —
  when the order was cancelled, which ADR-049's before-or-after question is
  about — and the second is the instant `OrderRetention` ages the row from,
  which must be this service's own.
- **`Cancel` sets once.** Section 10 says the cancellation columns are "set by
  `OrderCancelled` and never cleared"; a second `OrderCancelled` for one order
  — a republish with a fresh id — is the same superseded arrival, so the
  first stands and the call returns `false`.
- **An absent origin is stored absent.** `OrderCancelled.Origin`'s remark
  allows an older publisher to omit it, and ADR-049's reading of an absent
  origin — `workflow` for the two payment reasons, `user` otherwise — is a
  decision about suppression, which is PR-5's. The record keeps the fact and
  not a reading of it.

Not an `AggregateRoot` and no `IHasDomainEvents`, for PR-1's reason: the
service's architecture suite fails on either. `ExistsAsync` is on the port
because a redelivery the inbox did not see — its row lost to a crash after
the consumer's commit — must find its notice and return, not meet the unique
key and fault to `_error`.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/OrderRecordTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Notifications.Application.Records;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The order record's one move: a cancellation, recorded once and never cleared (ADR-049).</summary>
public class OrderRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_record_names_its_customer_and_no_cancellation()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        OrderRecord record = OrderRecord.For(order, customer, Now);

        record.OrderId.ShouldBe(order);
        record.CustomerId.ShouldBe(customer);
        record.RecordedAt.ShouldBe(Now);
        record.CancelledAt.ShouldBeNull();
        record.CancelReason.ShouldBeNull();
        record.CancelOrigin.ShouldBeNull();
    }

    [Fact]
    public void A_cancellation_records_its_reason_origin_and_instant()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        record.Cancel(CancelReasons.CustomerRequest, CancelOrigins.User, Now.AddMinutes(3)).ShouldBeTrue();

        record.CancelledAt.ShouldBe(Now.AddMinutes(3));
        record.CancelReason.ShouldBe(CancelReasons.CustomerRequest);
        record.CancelOrigin.ShouldBe(CancelOrigins.User);
    }

    [Fact]
    public void A_second_cancellation_keeps_the_first()
    {
        // ADR-049 reads the first: a later one, a republish or a redelivery past the inbox, must not rewrite it.
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        record.Cancel(CancelReasons.PaymentDeclined, CancelOrigins.Workflow, Now);

        record.Cancel(CancelReasons.CustomerRequest, CancelOrigins.User, Now.AddHours(1)).ShouldBeFalse();

        record.CancelReason.ShouldBe(CancelReasons.PaymentDeclined);
        record.CancelOrigin.ShouldBe(CancelOrigins.Workflow);
        record.CancelledAt.ShouldBe(Now);
    }

    [Fact]
    public void A_cancellation_with_no_origin_is_recorded_as_an_older_publisher_sent_it()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        record.Cancel(CancelReasons.PaymentTimeout, origin: null, Now).ShouldBeTrue();

        record.CancelOrigin.ShouldBeNull("ADR-049's reading of an absent origin is the send worker's");
    }

    [Fact]
    public void A_code_the_column_cannot_hold_is_the_caller_s_defect()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        string tooLong = new('r', OrderRecordLimits.MaxCodeLength + 1);

        Should.Throw<ArgumentOutOfRangeException>(() => record.Cancel(tooLong, CancelOrigins.User, Now));
        Should.Throw<ArgumentOutOfRangeException>(() => record.Cancel(CancelReasons.OutOfStock, tooLong, Now));
        record.CancelledAt.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~OrderRecordTests"
```

Expected: compile failure on `OrderRecord`, `OrderRecordLimits` and
`Common.Contracts.Ordering.V1` — the Application project references no
contract yet. The reference is Task 4's; until then this suite's
`using Common.Contracts.Ordering.V1;` and the two codes it names are what
fail, so Step 3 adds the reference now and Task 4 inherits it.

- [ ] **Step 3: The reference, the record, its widths and the ports**

`src/Services/Notifications/Notifications.Application/Notifications.Application.csproj`,
in the `ProjectReference` group after `Common.Application`'s:

```xml
    <!-- §3.2's seven events and CancelReasons' codes, the one assembly §4.3 lets cross a service boundary. -->
    <ProjectReference Include="..\..\..\BuildingBlocks\Common.Contracts\Common.Contracts.csproj" />
```

and the comment PR-1's mode left above the groups, which names
`Common.Application` alone, becomes:

```xml
  <!--
    Common.Application and Common.Contracts, §4.2's second row without the Domain project §4.1 does not give this
    service; Contracts arrives with the seven events §3.2 subscribes it to, not with a mapper it will never have.
  -->
```

`Records/OrderRecord.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>Ordering's word on one order: whose it is, and whether, why and by whom it was cancelled.</summary>
/// <remarks>
/// ADR-017's local projection, which four events naming only an order wait on for their customer. Written by whichever
/// of Ordering's three events arrives first; a cancellation is set once and never cleared, so the late arrivals commute
/// with it and the record stays ADR-049's deciding fact.
/// </remarks>
public sealed class OrderRecord
{
    public Guid OrderId { get; private set; }

    /// <summary>Pseudonymous personal data, so the record has a window and an erasure path (§11.7).</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>The cancellation's own instant, or null while no <c>OrderCancelled</c> has arrived.</summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>A <c>CancelReasons</c> code as published, or null when it failed the intake's check.</summary>
    public string? CancelReason { get; private set; }

    /// <summary>A <c>CancelOrigins</c> code, or null for a publisher that predates it (§9.2).</summary>
    public string? CancelOrigin { get; private set; }

    /// <summary>When this service first heard of the order, the instant <c>OrderRetention</c> ages it from.</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    // EF Core materialisation only (§5.4).
    private OrderRecord() { }

    private OrderRecord(Guid orderId, Guid customerId, DateTimeOffset now)
    {
        OrderId = orderId;
        CustomerId = customerId;
        RecordedAt = now;
    }

    /// <summary>The first of Ordering's events for an order names its customer.</summary>
    public static OrderRecord For(Guid orderId, Guid customerId, DateTimeOffset now) => new(orderId, customerId, now);

    /// <summary>Records the cancellation once; a second one keeps the first and returns false.</summary>
    public bool Cancel(string? reason, string? origin, DateTimeOffset at)
    {
        Bound(reason, nameof(reason));
        Bound(origin, nameof(origin));

        if (CancelledAt is not null)
            return false;

        CancelledAt = at;
        CancelReason = reason;
        CancelOrigin = origin;
        return true;
    }

    // A value the column cannot hold is the caller's defect, since the intake checks every code before this.
    private static void Bound(string? value, string name)
    {
        if (value is not null)
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, OrderRecordLimits.MaxCodeLength, name);
    }
}
```

`Records/OrderRecordLimits.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>The widths an order record's codes are stored at, named once so the record and its columns agree.</summary>
public static class OrderRecordLimits
{
    /// <summary>Twice the longest code <c>CancelReasons</c> or <c>CancelOrigins</c> defines today.</summary>
    public const int MaxCodeLength = 32;
}
```

Thirty-two holds `customer_request`, the longest code either vocabulary
defines, twice over, so a code Ordering adds later fits without a migration.

`Records/INotificationRepository.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>§6.3's repository for the notices this service owes, as its consumers write them.</summary>
public interface INotificationRepository
{
    /// <summary>Whether this event already owes this template's notice, as a redelivery past the inbox finds.</summary>
    Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct);

    void Add(Notification notification);
}
```

`Records/IOrderRecordRepository.cs`:

```csharp
namespace Notifications.Application.Records;

/// <summary>§6.3's repository for <see cref="OrderRecord"/>, keyed by the order as every event names it.</summary>
public interface IOrderRecordRepository
{
    Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct);

    void Add(OrderRecord record);
}
```

- [ ] **Step 4: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
```

Expected: 0 warnings; the five new tests green and every earlier one
unchanged. `Application_references_only_what_the_dependency_table_allows`
stays green: `Common.Contracts` is already on its list, which the render
carried from Catalog's.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): OrderRecord, set once by a cancellation, and the two repository ports"
```

The body argues the three decisions above and why the record is ADR-017's
projection rather than a read of Ordering.

---

### Task 2: `InboundValues` — another service's text, bounded and checked

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Intake/InboundValues.cs`
- Test: `tests/Notifications.Application.Tests/InboundValuesTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Notifications.Application.Intake;

public static class InboundValues
{
    public const int MaxTrackingNumberLength = 64;
    public const int MaxCodeLength = OrderRecordLimits.MaxCodeLength;
    public static string? Text(string? value, int maxLength);
    public static string? Code(string? value);
    public static string? Currency(string? value);
}
```

Spec section 8: rendering is the platform's first output encoding, and four
of a notice's values were written by another service — the tracking number
by a carrier before that. Each check returns the value or `null`, never a
throw, so the consumer records the notice without the value (Task 4).

**What each check refuses, and why that set.**

- **`Text`**, for the tracking number: blank, longer than its bound, or
  holding a C0 or C1 control character, U+2028 or U+2029, a lone surrogate,
  or a bidirectional formatting character — U+061C, U+200E, U+200F,
  U+202A–U+202E and U+2066–U+2069. A pair of surrogates is one character and
  is kept, so a carrier's emoji is not a refusal. The bound is Shipping's own
  `ShipmentLimits.MaxTrackingNumberLength`, restated because §4.3 lets only
  the contracts cross and the contract carries no width.
- **`Code`**, for `OrderCancelled.Reason` and `.Origin`: anything but
  lower-case ASCII letters, digits and underscores, or past the column's
  width. Every code in `CancelReasons` and `CancelOrigins` has that shape, so
  a code Ordering adds later is kept and phrased generically (Task 5), and
  only a value no code could be is dropped.
- **`Currency`**: anything but three capital ASCII letters. The shape of an
  ISO 4217 code and no table of which ones exist — ADR-053's `Money`
  counter-example is a table nobody owns yet.

The code points are written as `(char)0x202E`, never as the character or an
escape sequence, so the source holds nothing that reorders the line it is on.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/InboundValuesTests.cs`:

```csharp
using Notifications.Application.Intake;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>Every value another service wrote is bounded and renders as itself, or is dropped.</summary>
public class InboundValuesTests
{
    [Theory]
    [InlineData("1Z999AA10123456784")]
    [InlineData("KZ-ӘҒҚ-0042")]
    [InlineData("TRK 12 34")]
    public void A_tracking_number_that_renders_as_itself_is_kept(string value)
    {
        InboundValues.Text(value, InboundValues.MaxTrackingNumberLength).ShouldBe(value);
    }

    /// <summary>Code points that would change what a customer reads without being seen.</summary>
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0009)]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x061C)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x202A)]
    [InlineData(0x202E)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void A_tracking_number_holding_a_control_bidi_or_broken_character_is_dropped(int codePoint)
    {
        string value = $"1Z999{(char)codePoint}AA1";

        InboundValues.Text(value, InboundValues.MaxTrackingNumberLength).ShouldBeNull();
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_kept_whole()
    {
        // A surrogate pair is one character, so only a lone half is refused.
        string value = "TRK-" + char.ConvertFromUtf32(0x1F4E6);

        InboundValues.Text(value, InboundValues.MaxTrackingNumberLength).ShouldBe(value);
    }

    [Fact]
    public void A_tracking_number_is_kept_to_its_bound_and_dropped_past_it()
    {
        string atBound = new('9', InboundValues.MaxTrackingNumberLength);

        InboundValues.Text(atBound, InboundValues.MaxTrackingNumberLength).ShouldBe(atBound);
        InboundValues.Text(atBound + "9", InboundValues.MaxTrackingNumberLength).ShouldBeNull();
        InboundValues.Text("   ", InboundValues.MaxTrackingNumberLength).ShouldBeNull();
        InboundValues.Text(null, InboundValues.MaxTrackingNumberLength).ShouldBeNull();
    }

    [Theory]
    [InlineData("KZT", "KZT")]
    [InlineData("GBP", "GBP")]
    [InlineData("XTS", "XTS")]
    [InlineData("kzt", null)]
    [InlineData("KZ", null)]
    [InlineData("KZTT", null)]
    [InlineData("К₸T", null)]
    [InlineData("", null)]
    public void A_currency_is_kept_when_it_has_an_iso_4217_code_s_shape(string value, string? kept)
    {
        InboundValues.Currency(value).ShouldBe(kept);
    }

    [Theory]
    [InlineData("payment_declined", "payment_declined")]
    [InlineData("a_reason_ordering_adds_later_2", "a_reason_ordering_adds_later_2")]
    [InlineData("Payment_Declined", null)]
    [InlineData("payment declined", null)]
    [InlineData("payment-declined", null)]
    [InlineData("", null)]
    public void A_code_is_kept_when_it_has_a_wire_code_s_shape(string value, string? kept)
    {
        InboundValues.Code(value).ShouldBe(kept);
    }

    [Fact]
    public void A_code_past_its_column_s_width_is_dropped()
    {
        InboundValues.Code(new string('r', InboundValues.MaxCodeLength)).ShouldNotBeNull();
        InboundValues.Code(new string('r', InboundValues.MaxCodeLength + 1)).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~InboundValuesTests"
```

Expected: compile failure on `Notifications.Application.Intake`.

- [ ] **Step 3: Write the checks**

`Intake/InboundValues.cs`:

```csharp
using Notifications.Application.Records;

namespace Notifications.Application.Intake;

/// <summary>Checks each value another service wrote before it is stored, and drops one unsafe to render.</summary>
/// <remarks>
/// A kept value is bounded and holds no control, line-separator or bidirectional-formatting character, so a carrier's
/// tracking number cannot reorder or break what a customer reads. A dropped value is absent, never a fault: a throw
/// would carry another service's bytes to <c>_error</c> (§9.8).
/// </remarks>
public static class InboundValues
{
    /// <summary>Shipping's own width for a tracking number, restated because only contracts cross §4.3.</summary>
    public const int MaxTrackingNumberLength = 64;

    /// <summary>A code as <c>CancelReasons</c> and <c>CancelOrigins</c> spell them, at its column's width.</summary>
    public const int MaxCodeLength = OrderRecordLimits.MaxCodeLength;

    /// <summary>Free text, kept when it is bounded and every character renders as itself.</summary>
    public static string? Text(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            return null;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || char.IsControl(c) || IsLineBreaking(c) || IsBidiFormatting(c))
                return null;
        }

        return value;
    }

    /// <summary>A wire code: lower-case ASCII letters, digits and underscores, as the contracts spell one.</summary>
    public static string? Code(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxCodeLength)
            return null;

        foreach (char c in value)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
                return null;
        }

        return value;
    }

    /// <summary>An ISO 4217 code's shape: three capital ASCII letters, and no table of which exist (ADR-053).</summary>
    public static string? Currency(string? value) =>
        value is { Length: 3 } && value.All(char.IsAsciiLetterUpper) ? value : null;

    // U+2028 and U+2029, which break a line wherever a renderer honours them.
    private static bool IsLineBreaking(char c) => c is (char)0x2028 or (char)0x2029;

    // Marks, embeddings, overrides and isolates: each changes the order text displays in without being visible.
    private static bool IsBidiFormatting(char c) =>
        c is (char)0x061C or (char)0x200E or (char)0x200F
            or (>= (char)0x202A and <= (char)0x202E)
            or (>= (char)0x2066 and <= (char)0x2069);
}
```

- [ ] **Step 4: Run the suite**

```bash
dotnet test tests/Notifications.Application.Tests
```

Expected: green, the allow-list unchanged — `Enumerable.All` is
`System.Linq`'s and `char`'s predicates `System.Runtime`'s.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): InboundValues drops a control, bidi or unbounded value another service wrote"
```

The body names the refused set and says a drop is never a fault, because a
fault carries another service's bytes to `_error`.

---

### Task 3: The parameters' versioned format

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Rendering/NotificationParameters.cs`
- Create: `src/Services/Notifications/Notifications.Application/Rendering/ParametersFormat.cs`
- Create: `src/Services/Notifications/Notifications.Application/Rendering/UnreadableParametersException.cs`
- Modify: `tests/Notifications.Application.Tests/ArchitectureTests.cs` — one name on the allow-list
- Test: `tests/Notifications.Application.Tests/ParametersFormatTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Notifications.Application.Rendering;

public sealed record NotificationParameters
{
    public required Guid OrderId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    public string? TrackingNumber { get; init; }
    public string? CancelReason { get; init; }
}
public static class ParametersFormat
{
    public const int Version = 1;
    public static string Write(NotificationParameters parameters);
    public static NotificationParameters Read(string stored);   // throws UnreadableParametersException
}
public sealed class UnreadableParametersException : Exception;   // the three standard constructors
```

**The format, written down once.** A JSON object; `v` first, a number, `1`;
`orderId` a GUID string; `occurredAt` an ISO 8601 instant with its offset;
then `amount` (a JSON number), `currency`, `trackingNumber` and
`cancelReason`, each written only when present. Spec section 6 names the
members — the order's id, an amount and currency, a tracking number, a
cancellation code, the event's instant — and nothing else: no customer, no
mailbox, no provider's decline code. Section 8's "no value reaches a header"
is the renderer's; this format only has to carry each value back exactly.

**Two choices a reviewer would question.**

- **The amount is a JSON number, not a string.** `Utf8JsonWriter` writes a
  `decimal` with its scale and `JsonElement.GetDecimal` reads it back with
  the same scale — measured: `42.10` round-trips as `42.10`, `Scale` 2 — so
  the renderer's "never rounded" holds through the store, and a test pins it.
- **`Read` refuses any `v` but its own, both directions.** Section 6 says the
  worker refuses to read *past* the version it knows; a `0` is no version this
  code wrote either, so it is refused for the same reason, and a `v` that is
  a string or a fraction is a payload this code did not write at all. Every
  refusal is `UnreadableParametersException`, and its message names a
  version, never a stored value, since the payload holds another service's
  bytes.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/ParametersFormatTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Notifications.Application.Intake;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The stored parameters round-trip, carry their version, and refuse any other (ADR-053 rule 4).</summary>
public class ParametersFormatTests
{
    private static readonly NotificationParameters Full = new()
    {
        OrderId = Guid.CreateVersion7(),
        OccurredAt = new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero),
        Amount = 42.10m,
        Currency = "KZT",
        TrackingNumber = "KZ-ӘҒҚ-0042",
        CancelReason = "payment_timeout"
    };

    [Fact]
    public void Every_member_round_trips()
    {
        ParametersFormat.Read(ParametersFormat.Write(Full)).ShouldBe(Full);
    }

    [Fact]
    public void An_amount_keeps_its_scale_through_the_store()
    {
        // The renderer prints the decimal's own places, so a scale lost here is a rounding added there (ADR-053).
        NotificationParameters read = ParametersFormat.Read(ParametersFormat.Write(Full));

        read.Amount.ShouldBe(42.10m);
        read.Amount!.Value.Scale.ShouldBe((byte)2);
    }

    [Fact]
    public void An_absent_member_is_written_as_absent_and_read_back_as_null()
    {
        NotificationParameters bare = new() { OrderId = Guid.CreateVersion7(), OccurredAt = Full.OccurredAt };

        string stored = ParametersFormat.Write(bare);

        JsonNode.Parse(stored)!.AsObject().Select(p => p.Key).ShouldBe(["v", "orderId", "occurredAt"]);
        ParametersFormat.Read(stored).ShouldBe(bare);
    }

    [Fact]
    public void The_stored_object_names_its_version()
    {
        JsonNode.Parse(ParametersFormat.Write(Full))!["v"]!.GetValue<int>().ShouldBe(ParametersFormat.Version);
    }

    [Theory]
    [InlineData("""{"v":2,"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02T09:00Z"}""")]
    [InlineData("""{"v":0,"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02T09:00Z"}""")]
    [InlineData("""{"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02T09:00:00+00:00"}""")]
    [InlineData("""{"v":"1","orderId":"0199a9a0-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"v":1.5}""")]
    [InlineData("""[1]""")]
    public void A_version_it_does_not_know_is_refused_rather_than_guessed(string stored)
    {
        Should.Throw<UnreadableParametersException>(() => ParametersFormat.Read(stored));
    }

    [Theory]
    [InlineData("""{"v":1,"occurredAt":"2026-10-02T09:00:00+00:00"}""")]
    [InlineData("""{"v":1,"orderId":"not-a-guid","occurredAt":"2026-10-02T09:00:00+00:00"}""")]
    [InlineData("""{"v":1,"orderId":"0199a9a0-0000-7000-8000-000000000001","occurredAt":"2026-10-02","amount":"1"}""")]
    [InlineData("""{"v":1,""")]
    public void Version_one_s_shape_broken_is_refused_as_unreadable(string stored)
    {
        Should.Throw<UnreadableParametersException>(() => ParametersFormat.Read(stored));
    }

    [Fact]
    public void The_widest_values_the_intake_keeps_fit_the_column()
    {
        // Every bounded member at its bound, and an amount at decimal's full precision.
        NotificationParameters widest = Full with
        {
            Amount = decimal.MinValue,
            TrackingNumber = new string('Ә', InboundValues.MaxTrackingNumberLength),
            CancelReason = new string('r', InboundValues.MaxCodeLength)
        };

        ParametersFormat.Write(widest).Length.ShouldBeLessThanOrEqualTo(NotificationLimits.MaxParametersLength);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~ParametersFormatTests"
```

Expected: compile failure on `Notifications.Application.Rendering`.

- [ ] **Step 3: The parameters, the format and its refusal**

`Rendering/NotificationParameters.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>The values a notification's placeholders take, which with its version reproduce what was sent.</summary>
/// <remarks>
/// ADR-053 rule 4's evidence, so nothing here names a person. Every text member was written by another
/// service and arrives checked or absent (<c>InboundValues</c>); absent renders <see cref="TemplateRenderer.Absent"/>.
/// </remarks>
public sealed record NotificationParameters
{
    public required Guid OrderId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public decimal? Amount { get; init; }

    /// <summary>An ISO 4217 code as published, three capital letters.</summary>
    public string? Currency { get; init; }

    public string? TrackingNumber { get; init; }

    /// <summary>A <c>CancelReasons</c> code; a code the map does not know renders its generic phrase.</summary>
    public string? CancelReason { get; init; }
}
```

`Rendering/ParametersFormat.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace Notifications.Application.Rendering;

/// <summary>The parameters as a row stores them: a JSON object whose <c>v</c> member is its version.</summary>
/// <remarks>
/// A stored row outlives the code that wrote it, so the reader refuses any version but its own rather than guess, and
/// a new member is a new version beside this one, as §9.2 versions a contract (ADR-053 rule 4).
/// </remarks>
public static class ParametersFormat
{
    /// <summary>The one version this code writes and the only one it reads.</summary>
    public const int Version = 1;

    private const string VersionMember = "v";
    private const string OrderIdMember = "orderId";
    private const string OccurredAtMember = "occurredAt";
    private const string AmountMember = "amount";
    private const string CurrencyMember = "currency";
    private const string TrackingNumberMember = "trackingNumber";
    private const string CancelReasonMember = "cancelReason";

    public static string Write(NotificationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber(VersionMember, Version);
            json.WriteString(OrderIdMember, parameters.OrderId);
            json.WriteString(OccurredAtMember, parameters.OccurredAt);

            // A number, which keeps the decimal's scale through the round trip: 42.10 is read back as 42.10.
            if (parameters.Amount is decimal amount)
                json.WriteNumber(AmountMember, amount);

            WriteIfPresent(json, CurrencyMember, parameters.Currency);
            WriteIfPresent(json, TrackingNumberMember, parameters.TrackingNumber);
            WriteIfPresent(json, CancelReasonMember, parameters.CancelReason);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Reads a stored value, or throws <see cref="UnreadableParametersException"/> quoting none.</summary>
    public static NotificationParameters Read(string stored)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stored);

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored);
            JsonElement root = document.RootElement;

            int version = VersionOf(root);
            if (version != Version)
            {
                throw new UnreadableParametersException(
                    $"The stored parameters are version {version}; this code reads version {Version} alone.");
            }

            return new NotificationParameters
            {
                OrderId = root.GetProperty(OrderIdMember).GetGuid(),
                OccurredAt = root.GetProperty(OccurredAtMember).GetDateTimeOffset(),
                Amount = root.TryGetProperty(AmountMember, out JsonElement amount) ? amount.GetDecimal() : null,
                Currency = StringOrNull(root, CurrencyMember),
                TrackingNumber = StringOrNull(root, TrackingNumberMember),
                CancelReason = StringOrNull(root, CancelReasonMember),
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or FormatException
                                      or InvalidOperationException)
        {
            // The inner exception names a position and never the text, so no stored value reaches a log.
            throw new UnreadableParametersException($"The stored parameters are not version {Version}'s shape.", e);
        }
    }

    private static int VersionOf(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(VersionMember, out JsonElement member) ||
            member.ValueKind != JsonValueKind.Number ||
            !member.TryGetInt32(out int version))
        {
            throw new UnreadableParametersException("The stored parameters carry no version.");
        }

        return version;
    }

    private static void WriteIfPresent(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
            json.WriteString(name, value);
    }

    private static string? StringOrNull(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement member) ? member.GetString() : null;
}
```

`Rendering/UnreadableParametersException.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>A row's parameters are a version this code does not read, or not the shape of the one it does.</summary>
public sealed class UnreadableParametersException : Exception
{
    public UnreadableParametersException()
    {
    }

    public UnreadableParametersException(string message)
        : base(message)
    {
    }

    public UnreadableParametersException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 4: Run the suite and read the allow-list's answer**

```bash
dotnet test tests/Notifications.Application.Tests
```

Expected: `ParametersFormatTests` green and
`Application_references_only_what_the_dependency_table_allows` red, naming
`System.Text.Json` — `Utf8JsonWriter`, `JsonDocument` and `JsonElement`. It is
the shared framework's and no layer §4.2's second row forbids, so it joins
the list. `tests/Notifications.Application.Tests/ArchitectureTests.cs`, in
`allowed`, the last line `"System.Runtime"` becomes:

```csharp
            "System.Runtime",
            "System.Text.Json"
```

and the comment above `allowed` gains its reason. Before:

```csharp
        // §4.2's second row as an allow-list, with no Domain project because §4.1 gives none: Dapper is §6.5's
        // read side and brings System.Data.Common.
```

After:

```csharp
        // §4.2's second row as an allow-list, with no Domain project because §4.1 gives none: Dapper is §6.5's
        // read side and brings System.Data.Common, and System.Text.Json is the stored parameters' format.
```

Run again: green.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): ParametersFormat writes v1 and refuses any version it does not know"
```

The body states the format as above, that it is ADR-053 rule 4's evidence
and so holds nothing personal, and why the amount is a number.

---

### Task 4: The seven handlers and the one writer

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Rendering/TemplateKeys.cs`
- Create: `src/Services/Notifications/Notifications.Application/Rendering/PlaceholderNames.cs`
- Create: `src/Services/Notifications/Notifications.Application/Intake/RecordNotificationCommand.cs`
- Create: `src/Services/Notifications/Notifications.Application/Intake/RecordNotificationHandler.cs`
- Create: `src/Services/Notifications/Notifications.Application/Intake/OrderPlacedHandler.cs`,
  `OrderConfirmedHandler.cs`, `OrderCancelledHandler.cs`,
  `PaymentDeclinedHandler.cs`, `PaymentRefundedHandler.cs`,
  `ShipmentDispatchedHandler.cs` and `ShipmentDeliveredHandler.cs`
- Modify: `src/Services/Notifications/Notifications.Application/Notifications.Application.csproj` — the logging package
- Modify: `tests/Notifications.Application.Tests/ArchitectureTests.cs` — one name on the allow-list
- Modify: `tests/Notifications.Application.Tests/IdempotencyOptInTests.cs` — two coverage floors inverted
- Test: `tests/Notifications.Application.Tests/NotificationIntakeTests.cs`
- Test: `tests/Notifications.Application.Tests/IntakeMappingTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3; `Common.Contracts`' seven events, `CancelReasons` and
  `CancelOrigins`; `Common.Application`'s `IDispatcher`, `ICommand<Result>`,
  `ICommandHandler<,>` and `IIntegrationEventHandler<>`.
- Produces:

```csharp
namespace Notifications.Application.Rendering;
public static class TemplateKeys
{
    public const string OrderPlaced = "order-placed";            // and the six others, kebab case (spec, section 7)
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Placeholders { get; }
    public static IReadOnlyList<string> CancellationCodes { get; }
}
public static class PlaceholderNames
{
    public const string OrderId = "OrderId", Date = "Date", Amount = "Amount", Currency = "Currency",
        TrackingNumber = "TrackingNumber", CancelReason = "CancelReason";
}

namespace Notifications.Application.Intake;
public sealed record RecordNotificationCommand(
    Guid EventId, string TemplateKey, NotificationParameters Parameters, OrderFact? Order) : ICommand<Result>;
public sealed record OrderFact(Guid CustomerId, OrderCancellation? Cancellation);
public sealed record OrderCancellation(string Reason, string? Origin, DateTimeOffset At);
public sealed class RecordNotificationHandler : ICommandHandler<RecordNotificationCommand, Result>;
public sealed class OrderPlacedHandler : IIntegrationEventHandler<OrderPlaced>;    // and six more
```

**The shape, against Shipping's.** Shipping's consumers each dispatch a
command of their own because each moves its aggregate differently. Here the
seven differ only in which members they read, so each event handler is a
mapping and one command handler is the writer: the order record first when
the event is Ordering's, then the notice. Both writes are idempotent — the
record is created if absent and its cancellation set once, the notice added
only when `ExistsAsync` finds none — so a redelivery the inbox did not see
completes whatever the first delivery left, and the two writes commit in the
one transaction `TransactionBehavior` opens.

**The placeholder sets are declared here**, beside the keys, because the
keys are the intake's vocabulary and the sets are what each key promises to
fill; Task 5's files are refused against them.

| Key | Placeholders |
|---|---|
| `order-placed`, `order-confirmed`, `payment-refunded` | `OrderId`, `Date`, `Amount`, `Currency` |
| `order-cancelled` | `OrderId`, `Date`, `CancelReason` |
| `payment-declined` | `OrderId`, `Date` |
| `shipment-dispatched`, `shipment-delivered` | `OrderId`, `Date`, `TrackingNumber` |

**Three choices the spec leaves open.**

- **The notice names no customer at intake, even for Ordering's events.**
  Section 6 says the id is copied from the order record when the worker
  resolves it and is `NULL` until then; one rule for all seven rows keeps
  the worker's resolution the only writer of `CustomerId`.
- **`PaymentDeclined.Reason` is not read at all** — not mapped, not stored —
  and a test puts a provider's code on the event and finds it nowhere in the
  stored parameters (ADR-049).
- **A dropped value is logged by its member's name** at warning, with the
  event's id and the template's key; the value itself never reaches a log
  (spec, section 12).

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/NotificationIntakeTests.cs` — the
writer over hand-rolled fakes, Ordering's three in all six orders:

```csharp
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.Logging;
using Notifications.Application.Intake;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>
/// §3.2's Consumes column at the command: each event writes one pending row and calls nothing, and Ordering's three
/// write the order record in whichever order they arrive, to one final state (ADR-049).
/// </summary>
public class NotificationIntakeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Cancelled = Now.AddMinutes(7);

    private readonly FakeNotifications _notifications = new();
    private readonly FakeOrderRecords _orders = new();
    private readonly CapturingLogger<RecordNotificationHandler> _log = new();

    private RecordNotificationHandler Handler() => new(_notifications, _orders, new FixedClock(Now), _log);

    private static NotificationParameters For(Guid order) => new() { OrderId = order, OccurredAt = Now };

    private static RecordNotificationCommand Placed(Guid order, Guid customer) =>
        new(Guid.CreateVersion7(), TemplateKeys.OrderPlaced, For(order) with { Amount = 42.10m, Currency = "KZT" },
            new OrderFact(customer, Cancellation: null));

    private static RecordNotificationCommand Confirmed(Guid order, Guid customer) =>
        new(Guid.CreateVersion7(), TemplateKeys.OrderConfirmed, For(order) with { Amount = 42.10m, Currency = "KZT" },
            new OrderFact(customer, Cancellation: null));

    private static RecordNotificationCommand CancelledBy(Guid order, Guid customer, string reason, string? origin) =>
        new(Guid.CreateVersion7(), TemplateKeys.OrderCancelled, For(order) with { CancelReason = reason },
            new OrderFact(customer, new OrderCancellation(reason, origin, Cancelled)));

    private static RecordNotificationCommand Declined(Guid order) =>
        new(Guid.CreateVersion7(), TemplateKeys.PaymentDeclined, For(order), Order: null);

    /// <summary>Every order the three of Ordering's events can arrive in, since §9.4 orders none of them.</summary>
    public static TheoryData<string[]> EveryArrivalOrder => new()
    {
        { ["placed", "confirmed", "cancelled"] },
        { ["placed", "cancelled", "confirmed"] },
        { ["confirmed", "placed", "cancelled"] },
        { ["confirmed", "cancelled", "placed"] },
        { ["cancelled", "placed", "confirmed"] },
        { ["cancelled", "confirmed", "placed"] }
    };

    [Fact]
    public async Task An_event_owes_one_pending_notice_naming_its_order_and_nobody_yet()
    {
        Guid order = Guid.CreateVersion7();
        RecordNotificationCommand command = Placed(order, Guid.CreateVersion7());

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        Notification row = _notifications.Added.ShouldHaveSingleItem();
        row.EventId.ShouldBe(command.EventId);
        row.TemplateKey.ShouldBe(TemplateKeys.OrderPlaced);
        row.OrderId.ShouldBe(order);
        row.Status.ShouldBe(NotificationStatus.Pending);
        row.CustomerId.ShouldBeNull("the worker copies the customer from the order record (ADR-053 rule 4)");
        row.CreatedAt.ShouldBe(Now);
        ParametersFormat.Read(row.Parameters).ShouldBe(command.Parameters);
    }

    [Fact]
    public async Task A_redelivery_past_the_inbox_records_no_second_notice()
    {
        RecordNotificationCommand command = Placed(Guid.CreateVersion7(), Guid.CreateVersion7());

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);
        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        _notifications.Added.Count.ShouldBe(1, "the unique key is the line behind §9.5's inbox, and it is not reached");
        _orders.Added.Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(EveryArrivalOrder))]
    public async Task Ordering_s_three_events_leave_one_record_in_every_arrival_order(string[] arrivals)
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        foreach (string arrival in arrivals)
        {
            RecordNotificationCommand command = arrival switch
            {
                "placed" => Placed(order, customer),
                "confirmed" => Confirmed(order, customer),
                _ => CancelledBy(order, customer, CancelReasons.PaymentDeclined, CancelOrigins.Workflow)
            };

            await Handler().HandleAsync(command, TestContext.Current.CancellationToken);
        }

        OrderRecord record = _orders.Added.ShouldHaveSingleItem("whichever arrives first creates it, alone");
        record.CustomerId.ShouldBe(customer);
        record.CancelledAt.ShouldBe(Cancelled, "a cancellation is never cleared by a later Placed or Confirmed");
        record.CancelReason.ShouldBe(CancelReasons.PaymentDeclined);
        record.CancelOrigin.ShouldBe(CancelOrigins.Workflow);
        _notifications.Added.Select(n => n.TemplateKey).ShouldBe(
            [TemplateKeys.OrderPlaced, TemplateKeys.OrderConfirmed, TemplateKeys.OrderCancelled],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_cancellation_first_is_a_tombstone_the_late_placement_leaves_alone()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await Handler().HandleAsync(
            CancelledBy(order, customer, CancelReasons.CustomerRequest, CancelOrigins.User),
            TestContext.Current.CancellationToken);

        OrderRecord tombstone = _orders.Added.ShouldHaveSingleItem();
        tombstone.CancelOrigin.ShouldBe(CancelOrigins.User);

        await Handler().HandleAsync(Placed(order, customer), TestContext.Current.CancellationToken);

        _orders.Added.ShouldHaveSingleItem().ShouldBeSameAs(tombstone);
        tombstone.CancelledAt.ShouldBe(Cancelled);
        tombstone.CancelOrigin.ShouldBe(CancelOrigins.User);
    }

    [Fact]
    public async Task A_second_cancellation_keeps_the_first_and_says_so()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        await Handler().HandleAsync(
            CancelledBy(order, customer, CancelReasons.PaymentDeclined, CancelOrigins.Workflow),
            TestContext.Current.CancellationToken);

        await Handler().HandleAsync(
            CancelledBy(order, customer, CancelReasons.CustomerRequest, CancelOrigins.User),
            TestContext.Current.CancellationToken);

        _orders.Added.ShouldHaveSingleItem().CancelOrigin.ShouldBe(CancelOrigins.Workflow);
        _log.Entries.ShouldContain(e => e.EventId.Name == "CancellationKept");
    }

    [Fact]
    public async Task An_event_naming_no_customer_writes_no_order_record()
    {
        await Handler().HandleAsync(Declined(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        _notifications.Added.ShouldHaveSingleItem().TemplateKey.ShouldBe(TemplateKeys.PaymentDeclined);
        _orders.Added.ShouldBeEmpty("four events name an order alone, and the row waits for Ordering's to name one");
    }

    [Fact]
    public async Task A_tracking_number_that_fails_the_check_is_dropped_and_the_notice_still_recorded()
    {
        // A right-to-left override, which would display a stranger's text in an order nobody wrote.
        string spoofed = $"1Z999{(char)0x202E}AA1";
        RecordNotificationCommand command = new(
            Guid.CreateVersion7(),
            TemplateKeys.ShipmentDispatched,
            For(Guid.CreateVersion7()) with { TrackingNumber = spoofed },
            Order: null);

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        Notification row = _notifications.Added.ShouldHaveSingleItem();
        ParametersFormat.Read(row.Parameters).TrackingNumber.ShouldBeNull();
        row.Parameters.ShouldNotContain("1Z999");

        (LogLevel level, EventId id, string message) = _log.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Warning);
        id.Name.ShouldBe("Dropped");
        message.ShouldContain(nameof(NotificationParameters.TrackingNumber));
        message.ShouldNotContain("1Z999", Case.Sensitive, "the log names the member, never another service's value");
    }

    [Fact]
    public async Task A_currency_and_a_cancellation_s_codes_that_fail_the_check_are_dropped()
    {
        Guid order = Guid.CreateVersion7();
        RecordNotificationCommand command = new(
            Guid.CreateVersion7(),
            TemplateKeys.OrderCancelled,
            For(order) with { Currency = "kzt", CancelReason = "Out Of Stock" },
            new OrderFact(Guid.CreateVersion7(), new OrderCancellation("Out Of Stock", "SYSTEM", Cancelled)));

        await Handler().HandleAsync(command, TestContext.Current.CancellationToken);

        NotificationParameters stored = ParametersFormat.Read(_notifications.Added.ShouldHaveSingleItem().Parameters);
        stored.Currency.ShouldBeNull();
        stored.CancelReason.ShouldBeNull("the renderer phrases an absent code with its map's generic phrase");

        OrderRecord record = _orders.Added.ShouldHaveSingleItem();
        record.CancelledAt.ShouldBe(Cancelled, "the cancellation is the fact; a malformed reason does not unmake it");
        record.CancelReason.ShouldBeNull();
        record.CancelOrigin.ShouldBeNull();
    }

    private sealed class FakeNotifications : INotificationRepository
    {
        public List<Notification> Added { get; } = [];

        public Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct) =>
            Task.FromResult(Added.Exists(n => n.EventId == eventId && n.TemplateKey == templateKey));

        public void Add(Notification notification) => Added.Add(notification);
    }

    private sealed class FakeOrderRecords : IOrderRecordRepository
    {
        public List<OrderRecord> Added { get; } = [];

        public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
            Task.FromResult(Added.Find(r => r.OrderId == orderId));

        public void Add(OrderRecord record) => Added.Add(record);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // The formatted message rather than the raw state, so an assertion can read what a log store would hold.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, eventId, formatter(state, exception)));
    }
}
```

`tests/Notifications.Application.Tests/IntakeMappingTests.cs` — each
handler's mapping, one contract each:

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Notifications.Application.Intake;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>
/// Each of §3.2's seven handlers maps its contract to the command and nothing else: the intake tests build commands by
/// hand, so a handler reading the wrong member, of which three are Guids, would pass every one of them.
/// </summary>
public class IntakeMappingTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly RecordingDispatcher _dispatcher = new();

    [Fact]
    public async Task OrderPlaced_owes_order_placed_with_its_total_and_names_the_customer()
    {
        OrderPlaced placed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 12345.60m,
            Currency = "KZT",
            Lines = []
        };

        await new OrderPlacedHandler(_dispatcher).HandleAsync(placed, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordNotificationCommand(
            placed.MessageId,
            TemplateKeys.OrderPlaced,
            new NotificationParameters
            {
                OrderId = placed.OrderId,
                OccurredAt = At,
                Amount = 12345.60m,
                Currency = "KZT"
            },
            new OrderFact(placed.CustomerId, Cancellation: null)));
    }

    [Fact]
    public async Task OrderConfirmed_owes_order_confirmed_with_its_total_and_names_the_customer()
    {
        OrderConfirmed confirmed = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            CustomerId = Guid.CreateVersion7(),
            TotalAmount = 99.5m,
            Currency = "GBP",
            Lines = []
        };

        await new OrderConfirmedHandler(_dispatcher).HandleAsync(confirmed, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordNotificationCommand(
            confirmed.MessageId,
            TemplateKeys.OrderConfirmed,
            new NotificationParameters
            {
                OrderId = confirmed.OrderId,
                OccurredAt = At,
                Amount = 99.5m,
                Currency = "GBP"
            },
            new OrderFact(confirmed.CustomerId, Cancellation: null)));
    }

    [Fact]
    public async Task OrderCancelled_owes_order_cancelled_and_carries_its_reason_and_origin_to_the_record()
    {
        OrderCancelled cancelled = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            CustomerId = Guid.CreateVersion7(),
            Reason = CancelReasons.StockTimeout,
            Origin = CancelOrigins.Workflow
        };

        await new OrderCancelledHandler(_dispatcher).HandleAsync(cancelled, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordNotificationCommand(
            cancelled.MessageId,
            TemplateKeys.OrderCancelled,
            new NotificationParameters
            {
                OrderId = cancelled.OrderId,
                OccurredAt = At,
                CancelReason = CancelReasons.StockTimeout
            },
            new OrderFact(
                cancelled.CustomerId,
                new OrderCancellation(CancelReasons.StockTimeout, CancelOrigins.Workflow, At))));
    }

    [Fact]
    public async Task PaymentDeclined_owes_payment_declined_and_never_reads_the_provider_s_reason()
    {
        PaymentDeclined declined = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            Reason = "do_not_honour_51"
        };

        await new PaymentDeclinedHandler(_dispatcher).HandleAsync(declined, TestContext.Current.CancellationToken);

        RecordNotificationCommand sent =
            _dispatcher.Sent.ShouldHaveSingleItem().ShouldBeOfType<RecordNotificationCommand>();
        sent.ShouldBe(new RecordNotificationCommand(
            declined.MessageId,
            TemplateKeys.PaymentDeclined,
            new NotificationParameters { OrderId = declined.OrderId, OccurredAt = At },
            Order: null));

        // ADR-049: a provider's code is no message for a customer, and nothing here may branch on it.
        ParametersFormat.Write(sent.Parameters).ShouldNotContain("do_not_honour_51");
    }

    [Fact]
    public async Task PaymentRefunded_owes_payment_refunded_with_its_amount()
    {
        PaymentRefunded refunded = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            Reference = "pay_ref_1",
            Amount = 10.125m,
            Currency = "KWD"
        };

        await new PaymentRefundedHandler(_dispatcher).HandleAsync(refunded, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordNotificationCommand(
            refunded.MessageId,
            TemplateKeys.PaymentRefunded,
            new NotificationParameters
            {
                OrderId = refunded.OrderId,
                OccurredAt = At,
                Amount = 10.125m,
                Currency = "KWD"
            },
            Order: null));
    }

    [Fact]
    public async Task ShipmentDispatched_owes_shipment_dispatched_with_its_tracking_number()
    {
        ShipmentDispatched dispatched = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            TrackingNumber = "TRK-1"
        };

        await new ShipmentDispatchedHandler(_dispatcher).HandleAsync(dispatched, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordNotificationCommand(
            dispatched.MessageId,
            TemplateKeys.ShipmentDispatched,
            new NotificationParameters { OrderId = dispatched.OrderId, OccurredAt = At, TrackingNumber = "TRK-1" },
            Order: null));
    }

    [Fact]
    public async Task ShipmentDelivered_owes_shipment_delivered_with_its_tracking_number()
    {
        ShipmentDelivered delivered = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = At,
            OrderId = Guid.CreateVersion7(),
            TrackingNumber = "TRK-2"
        };

        await new ShipmentDeliveredHandler(_dispatcher).HandleAsync(delivered, TestContext.Current.CancellationToken);

        _dispatcher.Sent.ShouldHaveSingleItem().ShouldBe(new RecordNotificationCommand(
            delivered.MessageId,
            TemplateKeys.ShipmentDelivered,
            new NotificationParameters { OrderId = delivered.OrderId, OccurredAt = At, TrackingNumber = "TRK-2" },
            Order: null));
    }

    [Fact]
    public void Every_key_has_exactly_one_handler()
    {
        // §3.2's seven, one each: a handler added without a key, or a key with none, fails here.
        Type[] handlers =
        [
            .. typeof(OrderPlacedHandler).Assembly.GetTypes()
                .Where(t => t.GetInterfaces().Any(i =>
                    i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)))
        ];

        handlers.Length.ShouldBe(TemplateKeys.Placeholders.Count);
    }

    private sealed class RecordingDispatcher : IDispatcher
    {
        public List<object> Sent { get; } = [];

        public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct)
        {
            Sent.Add(command);
            return Task.FromResult((TResult)(object)Result.Success());
        }

        public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
```

The fakes, the clock, the logger and the dispatcher are file-local and
copied rather than shared, on the rule Shipping's and Payments' suites
follow.

`tests/Notifications.Application.Tests/IdempotencyOptInTests.cs`: the
render's two coverage floors were written to fail the day the service gains
its first command and its first handler, which is this task. Replace

```csharp
    [Fact]
    public void This_service_has_no_commands_for_the_gate_above_to_look_at_yet()
    {
        // The gate-coverage rule: the offender list above is as green when the selector matches nothing.
        Commands().ShouldBeEmpty(
            "This service declares no commands yet, so the gate above is vacuous. The day it "
            + "gains its first command this test fails — replace it with the ShouldNotBeEmpty "
            + "form, which is what keeps a vacuous gate from quietly becoming a permanent one.");
    }
```

with Shipping's form:

```csharp
    [Fact]
    public void The_opt_in_gate_is_looking_at_this_service_s_commands()
    {
        // The gate-coverage rule: the offender list above is as green when the selector matches nothing.
        Commands().ShouldNotBeEmpty();
    }
```

and in `The_nested_dispatch_gate_is_looking_at_this_service_s_handlers`
replace the `CommandHandlers().ShouldBeEmpty(…);` statement, message and all,
with `CommandHandlers().ShouldNotBeEmpty();`. The three floors about
`IIdempotentCommand` stay inverted: `RecordNotificationCommand` carries no
`CommandId`, since the inbox and the unique key are this service's
idempotency (spec, section 5).

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests
```

Expected: compile failure on `RecordNotificationCommand`, `TemplateKeys`
and the seven handlers.

- [ ] **Step 3: The keys and the names**

`Rendering/TemplateKeys.cs`:

```csharp
using Common.Contracts.Ordering.V1;

namespace Notifications.Application.Rendering;

/// <summary>§3.2's seven subscriptions as template keys, each with the closed set of names it may use.</summary>
public static class TemplateKeys
{
    public const string OrderPlaced = "order-placed";

    public const string OrderConfirmed = "order-confirmed";

    public const string OrderCancelled = "order-cancelled";

    public const string PaymentDeclined = "payment-declined";

    public const string PaymentRefunded = "payment-refunded";

    public const string ShipmentDispatched = "shipment-dispatched";

    public const string ShipmentDelivered = "shipment-delivered";

    /// <summary>What each key's template may name; a name outside its key's set refuses the host at start.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Placeholders { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [OrderPlaced] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date, PlaceholderNames.Amount,
                PlaceholderNames.Currency),
            [OrderConfirmed] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date, PlaceholderNames.Amount,
                PlaceholderNames.Currency),
            [OrderCancelled] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date, PlaceholderNames.CancelReason),
            [PaymentDeclined] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date),
            [PaymentRefunded] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date, PlaceholderNames.Amount,
                PlaceholderNames.Currency),
            [ShipmentDispatched] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date,
                PlaceholderNames.TrackingNumber),
            [ShipmentDelivered] = Set(PlaceholderNames.OrderId, PlaceholderNames.Date,
                PlaceholderNames.TrackingNumber),
        };

    /// <summary>The codes a cancellation's map phrases: <see cref="CancelReasons"/>' five, Ordering's.</summary>
    public static IReadOnlyList<string> CancellationCodes { get; } =
    [
        CancelReasons.OutOfStock,
        CancelReasons.StockTimeout,
        CancelReasons.PaymentDeclined,
        CancelReasons.PaymentTimeout,
        CancelReasons.CustomerRequest,
    ];

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);
}
```

`Rendering/PlaceholderNames.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>Every name a template may write as <c>{Name}</c>; <see cref="TemplateKeys"/> says which.</summary>
public static class PlaceholderNames
{
    public const string OrderId = "OrderId";

    /// <summary>The event's instant, as a date in the deployment's zone and the language's culture (ADR-053).</summary>
    public const string Date = "Date";

    /// <summary>The event's decimal in the language's culture at its own scale, never rounded (ADR-053).</summary>
    public const string Amount = "Amount";

    public const string Currency = "Currency";

    public const string TrackingNumber = "TrackingNumber";

    /// <summary>The cancellation's phrase from its version's map, never the code itself.</summary>
    public const string CancelReason = "CancelReason";
}
```

- [ ] **Step 4: The command and its writer**

`Intake/RecordNotificationCommand.cs`:

```csharp
using Common.Application;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>An event owes a notice; one of Ordering's also says what the order record keeps.</summary>
/// <remarks>Its values are as published; <see cref="RecordNotificationHandler"/> checks each one.</remarks>
public sealed record RecordNotificationCommand(
    Guid EventId,
    string TemplateKey,
    NotificationParameters Parameters,
    OrderFact? Order) : ICommand<Result>;

/// <summary>What one of Ordering's events says of its order: whose it is, and whether it was cancelled.</summary>
public sealed record OrderFact(Guid CustomerId, OrderCancellation? Cancellation);

/// <summary>An <c>OrderCancelled</c>'s reason and origin as published, and its instant.</summary>
public sealed record OrderCancellation(string Reason, string? Origin, DateTimeOffset At);
```

`Intake/RecordNotificationHandler.cs`:

```csharp
using Common.Application;
using Microsoft.Extensions.Logging;
using Notifications.Application.Records;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>Writes the notice an event owes and, for Ordering's, the order record, in one transaction (§6.3).</summary>
/// <remarks>
/// It calls nothing outside this service's database, so no fault it can meet is a wait (ADR-052). Every superseded or
/// repeated arrival returns rather than throws, and a value that fails <see cref="InboundValues"/> is dropped and named
/// in the log by its member, never by its content (§13.4).
/// </remarks>
public sealed class RecordNotificationHandler(
    INotificationRepository notifications,
    IOrderRecordRepository orders,
    TimeProvider clock,
    ILogger<RecordNotificationHandler> log)
    : ICommandHandler<RecordNotificationCommand, Result>
{
    // CA1848 (ADR-019); ids and member names only, never a value another service wrote.
    private static readonly Action<ILogger, string, Guid, string, Exception?> Dropped =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Warning,
            new EventId(1, nameof(Dropped)),
            "Dropped {Member} of event {EventId} for {TemplateKey}: it failed the intake's check and renders absent.");

    private static readonly Action<ILogger, Guid, string, Exception?> AlreadyOwed =
        LoggerMessage.Define<Guid, string>(
            LogLevel.Information,
            new EventId(2, nameof(AlreadyOwed)),
            "Event {EventId} already owes {TemplateKey}; a redelivery past the inbox records nothing.");

    private static readonly Action<ILogger, Guid, Exception?> CancellationKept =
        LoggerMessage.Define<Guid>(
            LogLevel.Information,
            new EventId(3, nameof(CancellationKept)),
            "Order {OrderId} is already recorded cancelled; the first cancellation stands.");

    public async Task<Result> HandleAsync(RecordNotificationCommand command, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        Guid orderId = command.Parameters.OrderId;

        // Before the notice, and idempotent, so a redelivery that finds the notice still completes the record.
        if (command.Order is OrderFact order)
            await RecordOrderAsync(command, orderId, order, now, ct);

        if (await notifications.ExistsAsync(command.EventId, command.TemplateKey, ct))
        {
            AlreadyOwed(log, command.EventId, command.TemplateKey, null);
            return Result.Success();
        }

        notifications.Add(Notification.Pending(
            command.EventId,
            command.TemplateKey,
            orderId,
            ParametersFormat.Write(Checked(command)),
            now));

        return Result.Success();
    }

    private async Task RecordOrderAsync(
        RecordNotificationCommand command,
        Guid orderId,
        OrderFact order,
        DateTimeOffset now,
        CancellationToken ct)
    {
        OrderRecord? record = await orders.GetAsync(orderId, ct);

        if (record is null)
        {
            record = OrderRecord.For(orderId, order.CustomerId, now);
            orders.Add(record);
        }

        if (order.Cancellation is not OrderCancellation cancellation)
            return;

        string? reason = Kept(command, nameof(OrderCancellation.Reason), cancellation.Reason, InboundValues.Code);
        string? origin = Kept(command, nameof(OrderCancellation.Origin), cancellation.Origin, InboundValues.Code);

        // Set once and never cleared: the tombstone a late OrderPlaced finds and leaves alone (ADR-049).
        if (!record.Cancel(reason, origin, cancellation.At))
            CancellationKept(log, orderId, null);
    }

    private NotificationParameters Checked(RecordNotificationCommand command)
    {
        NotificationParameters given = command.Parameters;

        return given with
        {
            Currency = Kept(command, nameof(given.Currency), given.Currency, InboundValues.Currency),
            TrackingNumber = Kept(
                command,
                nameof(given.TrackingNumber),
                given.TrackingNumber,
                v => InboundValues.Text(v, InboundValues.MaxTrackingNumberLength)),
            CancelReason = Kept(command, nameof(given.CancelReason), given.CancelReason, InboundValues.Code),
        };
    }

    private string? Kept(RecordNotificationCommand command, string member, string? given, Func<string?, string?> check)
    {
        string? kept = check(given);

        if (given is not null && kept is null)
            Dropped(log, member, command.EventId, command.TemplateKey, null);

        return kept;
    }
}
```

`Notifications.Application.csproj`, in the package group after
`FluentValidation.DependencyInjectionExtensions`:

```xml
    <!-- RecordNotificationHandler's LoggerMessage.Define, which CA1848 requires under ADR-019. -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
```

- [ ] **Step 5: The seven handlers**

`Intake/OrderPlacedHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; the order record it may create names the customer four events wait on.</summary>
public sealed class OrderPlacedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderPlaced>
{
    public async Task HandleAsync(OrderPlaced integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.OrderPlaced,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    Amount = integrationEvent.TotalAmount,
                    Currency = integrationEvent.Currency
                },
                new OrderFact(integrationEvent.CustomerId, Cancellation: null)),
            ct);
}
```

`Intake/OrderConfirmedHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; it creates the order record only when it is Ordering's first to arrive.</summary>
public sealed class OrderConfirmedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderConfirmed>
{
    public async Task HandleAsync(OrderConfirmed integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.OrderConfirmed,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    Amount = integrationEvent.TotalAmount,
                    Currency = integrationEvent.Currency
                },
                new OrderFact(integrationEvent.CustomerId, Cancellation: null)),
            ct);
}
```

`Intake/OrderCancelledHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription, and the one that writes ADR-049's deciding fact onto the order record.</summary>
public sealed class OrderCancelledHandler(IDispatcher dispatcher) : IIntegrationEventHandler<OrderCancelled>
{
    public async Task HandleAsync(OrderCancelled integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.OrderCancelled,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    CancelReason = integrationEvent.Reason
                },
                new OrderFact(
                    integrationEvent.CustomerId,
                    new OrderCancellation(
                        integrationEvent.Reason,
                        integrationEvent.Origin,
                        integrationEvent.OccurredAt))),
            ct);
}
```

`Intake/PaymentDeclinedHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Payments.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; <c>Reason</c> is never read, since ADR-049 forbids branching on it.</summary>
public sealed class PaymentDeclinedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<PaymentDeclined>
{
    public async Task HandleAsync(PaymentDeclined integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.PaymentDeclined,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt
                },
                Order: null),
            ct);
}
```

`Intake/PaymentRefundedHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Payments.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; it names an order and no customer, so the row waits on the order record.</summary>
public sealed class PaymentRefundedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<PaymentRefunded>
{
    public async Task HandleAsync(PaymentRefunded integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.PaymentRefunded,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    Amount = integrationEvent.Amount,
                    Currency = integrationEvent.Currency
                },
                Order: null),
            ct);
}
```

`Intake/ShipmentDispatchedHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Shipping.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription; the tracking number is a carrier's text and is checked before it is stored.</summary>
public sealed class ShipmentDispatchedHandler(IDispatcher dispatcher) : IIntegrationEventHandler<ShipmentDispatched>
{
    public async Task HandleAsync(ShipmentDispatched integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.ShipmentDispatched,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    TrackingNumber = integrationEvent.TrackingNumber
                },
                Order: null),
            ct);
}
```

`Intake/ShipmentDeliveredHandler.cs`:

```csharp
using Common.Application;
using Common.Contracts.Shipping.V1;
using Notifications.Application.Rendering;

namespace Notifications.Application.Intake;

/// <summary>§3.2's subscription, and Notifications' alone: the saga finalised on despatch (§9.6).</summary>
public sealed class ShipmentDeliveredHandler(IDispatcher dispatcher) : IIntegrationEventHandler<ShipmentDelivered>
{
    public async Task HandleAsync(ShipmentDelivered integrationEvent, CancellationToken ct) =>
        await dispatcher.SendAsync(
            new RecordNotificationCommand(
                integrationEvent.MessageId,
                TemplateKeys.ShipmentDelivered,
                new NotificationParameters
                {
                    OrderId = integrationEvent.OrderId,
                    OccurredAt = integrationEvent.OccurredAt,
                    TrackingNumber = integrationEvent.TrackingNumber
                },
                Order: null),
            ct);
}
```

The rendered `AddNotificationsApplication` already scans the assembly for
`ICommandHandler<,>` and `IIntegrationEventHandler<>` (§6.2), so no
registration line is owed.

- [ ] **Step 6: Run the suite and read the allow-list's answer**

```bash
dotnet test tests/Notifications.Application.Tests
```

Expected: every new test green and
`Application_references_only_what_the_dependency_table_allows` red, naming
`Microsoft.Extensions.Logging.Abstractions` — `ILogger<T>` and
`LoggerMessage`. Shipping's list carries the same name for the same reason.
In `allowed`, after `"Microsoft.Extensions.DependencyInjection.Abstractions",`:

```csharp
            "Microsoft.Extensions.Logging.Abstractions",
```

Run again: green.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): seven handlers map §3.2's events to RecordNotificationCommand, which writes the notice and the order record"
```

The body argues one writer for seven mappings, the order record's
commutation in all six arrival orders, and that a dropped value is a
warning naming the member and never a fault.

---

### Task 5: Twenty-one templates, three maps and the renderer

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Rendering/TemplateFile.cs`,
  `Template.cs`, `TemplatePart.cs`, `TemplateSet.cs`, `TemplateSetException.cs`,
  `RenderedMessage.cs`, `TemplateRenderer.cs`
- Create: `src/Services/Notifications/Notifications.Application/Templates/{key}.v1.{language}.txt`
  — seven keys in `en`, `kk` and `ru`, twenty-one files
- Create: `src/Services/Notifications/Notifications.Application/Templates/order-cancelled.v1.{language}.reasons.txt`
  — three files
- Modify: `src/Services/Notifications/Notifications.Application/Notifications.Application.csproj` — the resources
- Modify: `tests/Notifications.Application.Tests/ArchitectureTests.cs` — one name on the allow-list
- Test: `tests/Notifications.Application.Tests/TemplateSetTests.cs`
- Test: `tests/Notifications.Application.Tests/TemplateRendererTests.cs`

**Interfaces:**
- Consumes: Tasks 3 and 4.
- Produces:

```csharp
namespace Notifications.Application.Rendering;

public sealed record TemplateFile(string Name, string Text);
public sealed record Template(
    string Key, int Version, string Language, string Subject, IReadOnlyList<TemplatePart> Body);
public sealed record TemplatePart(string Text, bool IsPlaceholder);
public sealed class TemplateSetException : Exception { public IReadOnlyList<string> Refusals { get; } }
public sealed class TemplateSet
{
    public const string Folder = "Templates/";
    public const string OtherReason = "*";
    public static TemplateSet Embedded { get; }                         // throws TemplateSetException
    public static TemplateSet Parse(IEnumerable<TemplateFile> files);   // throws TemplateSetException
    public IReadOnlySet<string> Languages { get; }
    public int CurrentVersion(string key);
    public Template? Find(string key, int version, string language);
    public IReadOnlyDictionary<string, string>? Reasons(int version, string language);
    public IReadOnlyList<string> MissingFor(IEnumerable<string> languages);
}
public sealed record RenderedMessage(string Subject, string Body, int TemplateVersion, IReadOnlyList<string> Languages)
{
    public string LanguageList { get; }   // "kk,en", the row's Languages column
}
public sealed class TemplateRenderer
{
    public const string SubjectSeparator = " / ";
    public const string LanguageRule = "\n\n----------------------------------------\n\n";
    public const string Absent = "—";
    public IReadOnlyList<string> Languages { get; }
    public static TemplateRenderer Create(TemplateSet templates, IReadOnlyList<string> languages, string timeZone);
    public static IReadOnlyList<string> Refusals(
        TemplateSet templates, IReadOnlyList<string> languages, string timeZone);
    public RenderedMessage Render(string templateKey, NotificationParameters parameters, string? locale);
}
```

PR-5's worker calls `Render` with a row's key, its `ParametersFormat.Read`
parameters and the contact's locale, and stamps `TemplateVersion` and
`LanguageList` on the row with the intent (spec, section 4, step 4).

**The file format, and the one place this plan adds to the spec's.** A
template is `Templates/{key}.v{version}.{language}.txt`, its first line
`Subject: ` and the subject, the rest its body (spec, section 7). The body
is text and `{Name}` and nothing else: every brace opens a placeholder of
ASCII letters, so a stray brace is a refusal and there is no escape to
learn. **A cancellation's phrase map is a fourth kind of file the spec does
not count**: `Templates/order-cancelled.v{version}.{language}.reasons.txt`,
one `code: phrase` line per `CancelReasons` code and one for `*`, the
generic phrase a code the map does not know renders. It is a file and not a
dictionary in code because a third language must be files alone (ADR-053
rule 2), and it carries its template's version because a row's version
must reproduce the reason it gave as well as the text around it (rule 4).
So twenty-one templates and three maps, twenty-four resources.

**Every refusal is at start and names the file**, collected rather than
thrown one at a time, so one failed start lists them all:

| Refused | Where |
|---|---|
| a name not of the form, or one no key takes | `TemplateSet.Parse` |
| a first line that is not `Subject: ` and a subject | `TemplateSet.Parse` |
| a subject naming any placeholder, so no value another service wrote reaches a header (spec, section 8) | `TemplateSet.Parse` |
| a body naming a placeholder outside its key's set, or a brace opening none | `TemplateSet.Parse` |
| a map missing a code, naming an unknown one, or phrasing one twice | `TemplateSet.Parse` |
| a key with no template at any version | `TemplateSet.Parse` |
| a required language with no file for some key at its current version, or no map | `TemplateRenderer.Refusals` |
| an empty or repeated language set | `TemplateRenderer.Refusals` |
| a language the runtime has no culture for — every language, on an image without ICU | `TemplateRenderer.Refusals` |
| a zone that is not an IANA id this runtime knows | `TemplateRenderer.Refusals` |

The current version is the highest *named* for a key, so a broken newest
file is refused rather than quietly passed over for the one before it.

**Rendering.** A locale whose primary subtag the deployment's set holds —
`ru`, `ru-RU`, `RU` — renders that language alone; anything else, or none,
renders every language of the set in the set's order, subjects joined by
` / ` and bodies by a fixed rule (spec, section 7). The primary subtag is a
reading the spec leaves open: Keycloak's locales are language tags, and a
region on one says nothing a deployment's language set distinguishes. A
date is the event's instant converted to the deployment's zone and written
in the language's long-date pattern; an amount is the decimal at its own
scale in the language's number format, so `10.125` shows three places and
nothing rounds (ADR-053's `Money` counter-example); a value the intake
dropped renders as `—`, the same in every language, so no phrase is owed for
it. Measured on ICU: `kk` writes `12 345,60` with a no-break space and
`2026 ж. 2 қазан, жұма`; `ru` `пятница, 2 октября 2026 г.`; `en`
`Friday, October 2, 2026`.

**Why `WithCulture="false"`.** MSBuild reads `order-placed.v1.en.txt` as a
culture-specific resource — `en` is a culture name — and compiles it into an
`en/Notifications.Application.resources.dll` satellite, where
`GetManifestResourceNames` never finds it; `kk` and `ru` go the same way.
Measured in the scratch build: three satellite folders and an empty set.
The item's `WithCulture="false"` keeps every file in the main assembly, and
`LogicalName` makes its manifest name its path, which is the name a
refusal prints.

**Every template is a service message about the customer's order** (ADR-053
rule 4), and the review of each file below checks it against that sentence:
each says what happened to the order, names it by `{OrderId}`, and closes
with the same service-message line; none promotes, recommends or links. A
test holds the two mechanical halves — every body names `{OrderId}` and no
file holds `http:`, `https:`, `www.` or `://` — and the rest is the
reviewer's. The Kazakh and Russian text wants a native speaker's read
before merge; labelled lines (`Заказ:`, `Тапсырыс:`) carry each value so no
interpolated value has to agree in case with the sentence around it.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/TemplateSetTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The shipped files, and each refusal that fails the host at start, naming the file (ADR-053).</summary>
public class TemplateSetTests
{
    private static readonly string[] Shipped = ["en", "kk", "ru"];

    /// <summary>A minimal valid set in one language, which each refusal test breaks in one place.</summary>
    private static List<TemplateFile> Minimal(string language = "en") =>
    [
        .. TemplateKeys.Placeholders.Keys.Select(key =>
            new TemplateFile($"Templates/{key}.v1.{language}.txt", "Subject: A notice\n\nOrder {OrderId}.\n")),
        new TemplateFile(
            $"Templates/order-cancelled.v1.{language}.reasons.txt",
            string.Concat(TemplateKeys.CancellationCodes.Append(TemplateSet.OtherReason).Select(c => $"{c}: Why.\n")))
    ];

    private static List<TemplateFile> With(List<TemplateFile> files, TemplateFile replacement)
    {
        files.RemoveAll(f => f.Name == replacement.Name);
        files.Add(replacement);
        return files;
    }

    [Fact]
    public void The_shipped_set_is_seven_keys_in_three_languages_at_version_one()
    {
        TemplateSet shipped = TemplateSet.Embedded;

        shipped.Languages.ShouldBe(Shipped, ignoreOrder: true);
        foreach (string key in TemplateKeys.Placeholders.Keys)
        {
            shipped.CurrentVersion(key).ShouldBe(1, key);
            foreach (string language in Shipped)
                shipped.Find(key, 1, language).ShouldNotBeNull($"{key} in {language}");
        }

        shipped.MissingFor(Shipped).ShouldBeEmpty();
    }

    [Fact]
    public void Every_shipped_cancellation_map_phrases_each_code_and_the_generic_case()
    {
        foreach (string language in Shipped)
        {
            IReadOnlyDictionary<string, string> reasons = TemplateSet.Embedded.Reasons(1, language).ShouldNotBeNull();

            reasons.Keys.ShouldBe(
                [CancelReasons.OutOfStock, CancelReasons.StockTimeout, CancelReasons.PaymentDeclined,
                    CancelReasons.PaymentTimeout, CancelReasons.CustomerRequest, TemplateSet.OtherReason],
                ignoreOrder: true);
        }
    }

    [Fact]
    public void Every_shipped_template_is_about_the_customer_s_order_and_links_nowhere()
    {
        // ADR-053 rule 4 is the review's to judge; what a test can hold is that each names the order and no link.
        foreach (string key in TemplateKeys.Placeholders.Keys)
        {
            foreach (string language in Shipped)
            {
                Template template = TemplateSet.Embedded.Find(key, 1, language)!;
                string body = string.Concat(template.Body.Select(p => p.IsPlaceholder ? $"{{{p.Text}}}" : p.Text));

                body.ShouldContain("{OrderId}", Case.Sensitive, $"{key} in {language}");
                foreach (string link in new[] { "http:", "https:", "www.", "://" })
                    (template.Subject + body).ShouldNotContain(link, Case.Insensitive, $"{key} in {language}");
            }
        }
    }

    [Fact]
    public void A_placeholder_outside_its_key_s_set_is_refused_naming_the_file()
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile("Templates/payment-declined.v1.en.txt", "Subject: No\n\nOrder {OrderId} {Amount}.\n"));

        TemplateSetException refused = Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files));

        refused.Refusals.ShouldHaveSingleItem().ShouldBe(
            "Templates/payment-declined.v1.en.txt: names {Amount}, " +
            "which is not among payment-declined's placeholders.");
    }

    [Fact]
    public void A_subject_naming_a_placeholder_is_refused()
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile("Templates/order-placed.v1.en.txt", "Subject: Order {OrderId}\n\nOrder {OrderId}.\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem()
            .ShouldBe(
                "Templates/order-placed.v1.en.txt: its subject names a placeholder, and a subject may name none.");
    }

    [Theory]
    [InlineData("Order {OrderId")]
    [InlineData("Order OrderId}")]
    [InlineData("Order {}")]
    [InlineData("Order {Order Id}")]
    [InlineData("Order {OrderId.ToString()}")]
    public void A_brace_that_opens_no_placeholder_is_refused(string body)
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile("Templates/order-placed.v1.en.txt", $"Subject: Placed\n\n{body}\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem().ShouldStartWith("Templates/order-placed.v1.en.txt: the brace at offset");
    }

    [Theory]
    [InlineData("Order {OrderId}.\n")]
    [InlineData("subject: Placed\n\nOrder {OrderId}.\n")]
    [InlineData("Subject: \n\nOrder {OrderId}.\n")]
    public void A_first_line_that_is_not_a_subject_is_refused(string text)
    {
        List<TemplateFile> files = With(Minimal(), new TemplateFile("Templates/order-placed.v1.en.txt", text));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem()
            .ShouldBe("Templates/order-placed.v1.en.txt: its first line is not 'Subject: ' and a subject.");
    }

    [Theory]
    [InlineData("Templates/order-placed.en.txt")]
    [InlineData("Templates/order-placed.v01.en.txt")]
    [InlineData("Templates/order-placed.v1.EN.txt")]
    [InlineData("Templates/order-placed.v1.english.txt")]
    [InlineData("order-placed.v1.en.txt")]
    public void A_name_not_of_the_form_is_refused(string name)
    {
        List<TemplateFile> files = [.. Minimal(), new TemplateFile(name, "Subject: Placed\n\nOrder {OrderId}.\n")];

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem().ShouldStartWith($"{name}: not Templates/");
    }

    [Theory]
    [InlineData("Templates/order-shipped.v1.en.txt")]
    [InlineData("Templates/order-placed.v1.en.reasons.txt")]
    public void A_file_no_key_takes_is_refused(string name)
    {
        List<TemplateFile> files = [.. Minimal(), new TemplateFile(name, "Subject: Shipped\n\nOrder {OrderId}.\n")];

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem().ShouldBe($"{name}: names no template key that takes this file.");
    }

    [Fact]
    public void A_key_with_no_template_at_all_is_refused()
    {
        List<TemplateFile> files = Minimal();
        files.RemoveAll(f => f.Name == "Templates/shipment-delivered.v1.en.txt");

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files))
            .Refusals.ShouldHaveSingleItem()
            .ShouldBe("Templates/shipment-delivered: no template is shipped for this key at any version.");
    }

    [Fact]
    public void A_cancellation_map_missing_a_code_or_naming_an_unknown_one_is_refused()
    {
        List<TemplateFile> files = With(
            Minimal(),
            new TemplateFile(
                "Templates/order-cancelled.v1.en.reasons.txt",
                "out_of_stock: Gone.\nstock_timeout: Late.\npayment_declined: No.\npayment_timeout: Slow.\n" +
                "fraud: Suspect.\n*: Why.\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files)).Refusals.ShouldBe(
        [
            "Templates/order-cancelled.v1.en.reasons.txt: 'fraud' is not a CancelReasons code or '*'.",
            "Templates/order-cancelled.v1.en.reasons.txt: 'customer_request' has no phrase."
        ]);
    }

    [Fact]
    public void The_highest_version_present_is_the_current_one_and_a_language_without_it_is_missing()
    {
        // A new version is a new file beside the old (ADR-053 rule 4), so v1 stays for the rows that name it.
        List<TemplateFile> files =
        [
            .. Minimal("en"),
            .. Minimal("kk"),
            new TemplateFile("Templates/order-placed.v2.en.txt", "Subject: Placed again\n\nOrder {OrderId}.\n")
        ];

        TemplateSet set = TemplateSet.Parse(files);

        set.CurrentVersion(TemplateKeys.OrderPlaced).ShouldBe(2);
        set.Find(TemplateKeys.OrderPlaced, 1, "en").ShouldNotBeNull();
        set.MissingFor(["en"]).ShouldBeEmpty();
        set.MissingFor(["en", "kk"]).ShouldBe(["Templates/order-placed.v2.kk.txt"]);
    }

    [Fact]
    public void A_required_language_with_no_cancellation_map_is_missing_its_map()
    {
        List<TemplateFile> files = Minimal();
        files.RemoveAll(f => f.Name.EndsWith(".reasons.txt", StringComparison.Ordinal));

        TemplateSet.Parse(files).MissingFor(["en"]).ShouldBe(["Templates/order-cancelled.v1.en.reasons.txt"]);
    }

    [Fact]
    public void A_checkout_s_line_endings_change_nothing_a_customer_reads()
    {
        // .editorconfig checks text out CRLF; the body a customer reads is the same either way.
        TemplateSet lf = TemplateSet.Parse(Minimal());
        TemplateSet crlf = TemplateSet.Parse(
            [.. Minimal().Select(f => f with { Text = f.Text.Replace("\n", "\r\n", StringComparison.Ordinal) })]);

        Template a = lf.Find(TemplateKeys.OrderPlaced, 1, "en")!;
        Template b = crlf.Find(TemplateKeys.OrderPlaced, 1, "en")!;

        b.Subject.ShouldBe(a.Subject);
        b.Body.ShouldBe(a.Body);
    }

    [Fact]
    public void Every_refusal_in_a_set_is_reported_together()
    {
        List<TemplateFile> files = With(
            With(
                Minimal(),
                new TemplateFile("Templates/order-placed.v1.en.txt", "Subject: {OrderId}\n\nOrder {OrderId}.\n")),
            new TemplateFile("Templates/payment-declined.v1.en.txt", "Subject: No\n\n{Amount}\n"));

        Should.Throw<TemplateSetException>(() => TemplateSet.Parse(files)).Refusals.Count.ShouldBe(2);
    }
}
```

`tests/Notifications.Application.Tests/TemplateRendererTests.cs`:

```csharp
using System.Globalization;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The renderer over the shipped files: language by locale, value by culture, date by zone.</summary>
public class TemplateRendererTests
{
    /// <summary>One hour before midnight in Almaty, and so the next day in a zone twelve hours ahead.</summary>
    private static readonly DateTimeOffset LateEvening = new(2026, 10, 2, 11, 0, 0, TimeSpan.Zero);

    private static readonly NotificationParameters Everything = new()
    {
        OrderId = Guid.Parse("0199a9a0-0000-7000-8000-000000000042"),
        OccurredAt = LateEvening,
        Amount = 12345.60m,
        Currency = "KZT",
        TrackingNumber = "KZ-0042",
        CancelReason = CancelReasons.OutOfStock
    };

    private static TemplateRenderer Renderer(string[] languages, string zone = "Asia/Almaty") =>
        TemplateRenderer.Create(TemplateSet.Embedded, languages, zone);

    public static TheoryData<string, string> EveryKeyInEveryLanguage()
    {
        TheoryData<string, string> rows = [];
        foreach (string key in TemplateKeys.Placeholders.Keys)
        {
            foreach (string language in new[] { "en", "kk", "ru" })
                rows.Add(key, language);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(EveryKeyInEveryLanguage))]
    public void Every_key_renders_in_every_shipped_language_with_every_placeholder_filled(string key, string language)
    {
        RenderedMessage message = Renderer([language]).Render(key, Everything, locale: language);

        message.Subject.ShouldNotBeNullOrWhiteSpace();
        message.Body.ShouldNotContain("{");
        message.Body.ShouldNotContain("}");
        message.Body.ShouldContain(Everything.OrderId.ToString());

        IReadOnlySet<string> placeholders = TemplateKeys.Placeholders[key];
        if (placeholders.Contains(PlaceholderNames.Currency))
            message.Body.ShouldContain("KZT");
        if (placeholders.Contains(PlaceholderNames.TrackingNumber))
            message.Body.ShouldContain("KZ-0042");
        message.TemplateVersion.ShouldBe(1);
        message.Languages.ShouldBe([language]);
    }

    [Theory]
    [InlineData("ru", "ru")]
    [InlineData("ru-RU", "ru")]
    [InlineData("KK", "kk")]
    [InlineData("kk_KZ", "kk")]
    public void A_locale_the_set_holds_is_sent_its_language_alone(string locale, string language)
    {
        RenderedMessage message = Renderer(["kk", "ru", "en"]).Render(TemplateKeys.OrderPlaced, Everything, locale);

        message.Languages.ShouldBe([language]);
        message.Subject.ShouldNotContain(TemplateRenderer.SubjectSeparator);
        message.Body.ShouldNotContain(TemplateRenderer.LanguageRule);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public void Any_other_locale_or_none_is_sent_every_language_of_the_set_in_the_set_s_order(string? locale)
    {
        TemplateRenderer renderer = Renderer(["kk", "en"]);

        RenderedMessage message = renderer.Render(TemplateKeys.OrderConfirmed, Everything, locale);

        message.Languages.ShouldBe(["kk", "en"]);
        message.LanguageList.ShouldBe("kk,en");
        string kazakh = Renderer(["kk"]).Render(TemplateKeys.OrderConfirmed, Everything, "kk").Subject;
        string english = Renderer(["en"]).Render(TemplateKeys.OrderConfirmed, Everything, "en").Subject;
        message.Subject.ShouldBe(kazakh + TemplateRenderer.SubjectSeparator + english);
        message.Body.Split(TemplateRenderer.LanguageRule).Length.ShouldBe(2);
    }

    [Fact]
    public void A_kazakh_date_and_amount_differ_from_the_invariant_culture_s()
    {
        // Both need ICU and tzdata, which the -chiseled-extra image carries and the plain one does not (§15.2).
        string body = Renderer(["kk"], "Asia/Almaty").Render(TemplateKeys.OrderPlaced, Everything, "kk").Body;

        body.ShouldContain($"12{(char)0x00A0}345,60", Case.Sensitive, "a no-break space groups, a comma separates");
        body.ShouldNotContain(12345.60m.ToString("N2", CultureInfo.InvariantCulture));
        body.ShouldContain("қазан", Case.Sensitive, "October in Kazakh, from the culture's month names");
        body.ShouldNotContain(LateEvening.ToString("D", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_date_at_the_edge_of_a_day_lands_on_the_zone_s_side_of_it()
    {
        // 11:00 UTC is 17:00 in Almaty and 00:45 the next morning in Chatham.
        string almaty = Renderer(["en"], "Asia/Almaty").Render(TemplateKeys.OrderPlaced, Everything, "en").Body;
        string chatham = Renderer(["en"], "Pacific/Chatham").Render(TemplateKeys.OrderPlaced, Everything, "en").Body;

        almaty.ShouldContain("October 2, 2026");
        chatham.ShouldContain("October 3, 2026");
        chatham.ShouldNotContain("October 2, 2026");
    }

    [Theory]
    [InlineData("10.125", "10.125")]
    [InlineData("42.10", "42.10")]
    [InlineData("7", "7")]
    [InlineData("1234567.5", "1,234,567.5")]
    public void An_amount_is_shown_at_its_own_scale_and_never_rounded(string amount, string shown)
    {
        decimal value = decimal.Parse(amount, CultureInfo.InvariantCulture);

        string body = Renderer(["en"]).Render(
            TemplateKeys.PaymentRefunded,
            Everything with { Amount = value, Currency = "KWD" },
            "en").Body;

        body.ShouldContain($"{shown} KWD");
    }

    [Fact]
    public void A_value_the_intake_dropped_renders_as_the_absent_mark()
    {
        string body = Renderer(["ru"]).Render(
            TemplateKeys.ShipmentDispatched,
            Everything with { TrackingNumber = null },
            "ru").Body;

        body.ShouldContain($"Трек-номер: {TemplateRenderer.Absent}");
    }

    [Theory]
    [InlineData(CancelReasons.OutOfStock, "Some of the items in it were out of stock.")]
    [InlineData(CancelReasons.StockTimeout, "We could not reserve the items in it in time.")]
    [InlineData(CancelReasons.PaymentDeclined, "The payment for it was not accepted.")]
    [InlineData(CancelReasons.PaymentTimeout, "We did not receive confirmation of the payment in time.")]
    [InlineData(CancelReasons.CustomerRequest, "It was cancelled at your request.")]
    [InlineData("fraud_suspected", "We were unable to complete it.")]
    [InlineData(null, "We were unable to complete it.")]
    public void A_cancellation_says_why_from_its_map_and_an_unknown_code_says_so_generically(
        string? code,
        string phrase)
    {
        string body = Renderer(["en"]).Render(
            TemplateKeys.OrderCancelled,
            Everything with { CancelReason = code },
            "en").Body;

        body.ShouldContain($"has been cancelled. {phrase}");
        if (code is not null)
            body.ShouldNotContain(code);
    }

    [Fact]
    public void An_empty_or_repeated_language_set_is_refused()
    {
        TemplateRenderer.Refusals(TemplateSet.Embedded, [], "UTC")
            .ShouldBe(["The language set is empty; ADR-053 makes it a set of at least one."]);
        TemplateRenderer.Refusals(TemplateSet.Embedded, ["en", "en"], "UTC")
            .ShouldBe(["The language set names 'en' twice."]);
    }

    [Fact]
    public void A_language_with_no_templates_is_refused_naming_every_file_it_lacks()
    {
        IReadOnlyList<string> refusals = TemplateRenderer.Refusals(TemplateSet.Embedded, ["en", "de"], "UTC");

        refusals.Count.ShouldBe(TemplateKeys.Placeholders.Count + 1, "seven templates and the cancellation's map");
        refusals.ShouldContain("Templates/order-placed.v1.de.txt is missing, and the language set requires it.");
        refusals.ShouldContain(
            "Templates/order-cancelled.v1.de.reasons.txt is missing, and the language set requires it.");
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("Central Asia Standard Time")]
    [InlineData("")]
    public void A_zone_that_is_not_an_iana_id_this_runtime_knows_is_refused(string zone)
    {
        TemplateRenderer.Refusals(TemplateSet.Embedded, ["en"], zone)
            .ShouldBe([$"'{zone}' is not an IANA time zone this runtime knows."]);
    }

    [Fact]
    public void Create_refuses_with_every_refusal_at_once()
    {
        TemplateSetException refused = Should.Throw<TemplateSetException>(
            () => TemplateRenderer.Create(TemplateSet.Embedded, ["en", "en"], "Mars/Olympus_Mons"));

        refused.Refusals.Count.ShouldBe(2);
    }

    [Fact]
    public void The_highest_version_present_is_rendered_and_stamped()
    {
        List<TemplateFile> files =
        [
            .. TemplateKeys.Placeholders.Keys.Select(key =>
                new TemplateFile($"Templates/{key}.v1.en.txt", "Subject: One\n\nOrder {OrderId}.\n")),
            new TemplateFile("Templates/order-placed.v2.en.txt", "Subject: Two\n\nOrder {OrderId}, again.\n"),
            new TemplateFile(
                "Templates/order-cancelled.v1.en.reasons.txt",
                string.Concat(
                    TemplateKeys.CancellationCodes.Append(TemplateSet.OtherReason).Select(c => $"{c}: Why.\n")))
        ];

        RenderedMessage message = TemplateRenderer.Create(TemplateSet.Parse(files), ["en"], "UTC")
            .Render(TemplateKeys.OrderPlaced, Everything, "en");

        message.TemplateVersion.ShouldBe(2);
        message.Subject.ShouldBe("Two");
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~TemplateSetTests|FullyQualifiedName~TemplateRendererTests"
```

Expected: compile failure on `TemplateSet`, `TemplateFile` and
`TemplateRenderer`.

- [ ] **Step 3: The parsed types and the refusal**

`Rendering/TemplateFile.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>One shipped file as read, before <see cref="TemplateSet.Parse"/> checks it.</summary>
public sealed record TemplateFile(string Name, string Text);
```

`Rendering/Template.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>One parsed template: a subject naming no placeholder, and a body naming only its key's.</summary>
/// <remarks>The body is split once, at start, so rendering substitutes and never parses (ADR-053 rule 1).</remarks>
public sealed record Template(
    string Key,
    int Version,
    string Language,
    string Subject,
    IReadOnlyList<TemplatePart> Body);
```

`Rendering/TemplatePart.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>A run of a template's body: literal text, or the name of the placeholder that stands there.</summary>
public sealed record TemplatePart(string Text, bool IsPlaceholder);
```

`Rendering/TemplateSetException.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>The shipped templates cannot serve this deployment; each refusal names the file or value to fix.</summary>
public sealed class TemplateSetException : Exception
{
    public TemplateSetException()
    {
        Refusals = [];
    }

    public TemplateSetException(string message)
        : base(message)
    {
        Refusals = [message];
    }

    public TemplateSetException(string message, Exception innerException)
        : base(message, innerException)
    {
        Refusals = [message];
    }

    public TemplateSetException(IReadOnlyList<string> refusals)
        : base(string.Join(Environment.NewLine, refusals))
    {
        Refusals = refusals;
    }

    /// <summary>Every refusal found, so one start names them all rather than one per deploy.</summary>
    public IReadOnlyList<string> Refusals { get; }
}
```

- [ ] **Step 4: The set**

`Rendering/TemplateSet.cs`:

```csharp
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Notifications.Application.Rendering;

/// <summary>Every template the service ships, parsed and checked once, so a defect fails the host at start.</summary>
/// <remarks>
/// A cancellation's map is a file beside its template and versioned with it, so a row's version reproduces the reason
/// it gave as well as the text (ADR-053 rule 4), and a third language is files alone (ADR-053 rule 2).
/// </remarks>
public sealed class TemplateSet
{
    /// <summary>The prefix of every shipped resource, whose manifest name is its path under the project.</summary>
    public const string Folder = "Templates/";

    /// <summary>The entry for a code the map does not know, since Ordering adds codes on its own schedule.</summary>
    public const string OtherReason = "*";

    private const string Extension = ".txt";
    private const string ReasonsSuffix = "reasons";
    private const string SubjectPrefix = "Subject: ";

    private static readonly Lazy<TemplateSet> Shipped = new(() => Parse(EmbeddedFiles()));

    private readonly Dictionary<(string Key, int Version, string Language), Template> _templates;
    private readonly Dictionary<(int Version, string Language), IReadOnlyDictionary<string, string>> _reasons;
    private readonly Dictionary<string, int> _current;

    private TemplateSet(
        Dictionary<(string Key, int Version, string Language), Template> templates,
        Dictionary<(int Version, string Language), IReadOnlyDictionary<string, string>> reasons,
        Dictionary<string, int> current)
    {
        _templates = templates;
        _reasons = reasons;
        _current = current;
        Languages = new HashSet<string>(templates.Keys.Select(k => k.Language), StringComparer.Ordinal);
    }

    /// <summary>The compiled-in files, or a <see cref="TemplateSetException"/> naming each fault.</summary>
    public static TemplateSet Embedded => Shipped.Value;

    /// <summary>Every language some template is written in.</summary>
    public IReadOnlySet<string> Languages { get; }

    /// <summary>The highest version present for a key, which is the one sent (ADR-053 rule 4).</summary>
    public int CurrentVersion(string key) =>
        _current.TryGetValue(key, out int version)
            ? version
            : throw new ArgumentOutOfRangeException(nameof(key), key, "Not one of TemplateKeys.");

    public Template? Find(string key, int version, string language) =>
        _templates.GetValueOrDefault((key, version, language));

    /// <summary>A cancellation's map for one version and language, by code and <see cref="OtherReason"/>.</summary>
    public IReadOnlyDictionary<string, string>? Reasons(int version, string language) =>
        _reasons.GetValueOrDefault((version, language));

    /// <summary>The files these languages need at each key's current version, by the name each ships as.</summary>
    public IReadOnlyList<string> MissingFor(IEnumerable<string> languages)
    {
        ArgumentNullException.ThrowIfNull(languages);

        List<string> missing = [];
        foreach (string language in languages)
        {
            foreach (string key in TemplateKeys.Placeholders.Keys.Order(StringComparer.Ordinal))
            {
                int version = _current[key];

                if (!_templates.ContainsKey((key, version, language)))
                    missing.Add(NameOf(key, version, language, reasons: false));

                if (key == TemplateKeys.OrderCancelled && !_reasons.ContainsKey((version, language)))
                    missing.Add(NameOf(key, version, language, reasons: true));
            }
        }

        return missing;
    }

    /// <summary>Checks every file and throws one <see cref="TemplateSetException"/> naming each that fails.</summary>
    public static TemplateSet Parse(IEnumerable<TemplateFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        List<string> refusals = [];
        Dictionary<(string Key, int Version, string Language), Template> templates = [];
        Dictionary<(int Version, string Language), IReadOnlyDictionary<string, string>> reasons = [];

        // By name rather than by what parsed, so a broken newest file is refused and never quietly passed over.
        Dictionary<string, int> current = new(StringComparer.Ordinal);

        foreach (TemplateFile file in files)
        {
            FileName? name = FileName.Read(file.Name);
            if (name is null)
            {
                refusals.Add($"{file.Name}: not {Folder}{{key}}.v{{version}}.{{language}}{Extension}.");
                continue;
            }

            if (!TemplateKeys.Placeholders.ContainsKey(name.Key) ||
                (name.IsReasons && name.Key != TemplateKeys.OrderCancelled))
            {
                refusals.Add($"{file.Name}: names no template key that takes this file.");
                continue;
            }

            if (!name.IsReasons)
                current[name.Key] = Math.Max(current.GetValueOrDefault(name.Key), name.Version);

            // A checkout's line endings are no part of a message.
            string text = file.Text.Replace("\r\n", "\n", StringComparison.Ordinal);

            if (name.IsReasons)
            {
                if (ReasonsIn(file.Name, text, refusals) is Dictionary<string, string> map)
                    reasons[(name.Version, name.Language)] = map;
            }
            else if (TemplateIn(file.Name, name, text, refusals) is Template template)
            {
                templates[(name.Key, name.Version, name.Language)] = template;
            }
        }

        foreach (string key in TemplateKeys.Placeholders.Keys.Where(k => !current.ContainsKey(k)))
            refusals.Add($"{Folder}{key}: no template is shipped for this key at any version.");

        if (refusals.Count > 0)
            throw new TemplateSetException(refusals);

        return new TemplateSet(templates, reasons, current);
    }

    private static string NameOf(string key, int version, string language, bool reasons) =>
        reasons
            ? $"{Folder}{key}.v{version.ToString(CultureInfo.InvariantCulture)}.{language}.{ReasonsSuffix}{Extension}"
            : $"{Folder}{key}.v{version.ToString(CultureInfo.InvariantCulture)}.{language}{Extension}";

    private static Template? TemplateIn(string file, FileName name, string text, List<string> refusals)
    {
        int before = refusals.Count;
        int end = text.IndexOf('\n', StringComparison.Ordinal);
        string first = end < 0 ? text : text[..end];
        string body = end < 0 ? "" : text[(end + 1)..].Trim('\n');

        if (!first.StartsWith(SubjectPrefix, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(first[SubjectPrefix.Length..]))
        {
            refusals.Add($"{file}: its first line is not '{SubjectPrefix}' and a subject.");
            return null;
        }

        string subject = first[SubjectPrefix.Length..].Trim();

        // No value another service wrote may reach a header, so a subject names no placeholder at all.
        if (subject.AsSpan().IndexOfAny('{', '}') >= 0)
            refusals.Add($"{file}: its subject names a placeholder, and a subject may name none.");

        if (string.IsNullOrWhiteSpace(body))
            refusals.Add($"{file}: it has no body.");

        List<TemplatePart>? parts = PartsOf(file, body, refusals);
        foreach (TemplatePart part in parts?.Where(p => p.IsPlaceholder) ?? [])
        {
            if (!TemplateKeys.Placeholders[name.Key].Contains(part.Text))
                refusals.Add($"{file}: names {{{part.Text}}}, which is not among {name.Key}'s placeholders.");
        }

        return refusals.Count == before && parts is not null
            ? new Template(name.Key, name.Version, name.Language, subject, parts)
            : null;
    }

    // Every brace opens a placeholder of letters alone: a template is text and {Name}, never an expression.
    private static List<TemplatePart>? PartsOf(string file, string body, List<string> refusals)
    {
        List<TemplatePart> parts = [];
        int position = 0;

        while (true)
        {
            int open = body.IndexOfAny(['{', '}'], position);
            if (open < 0)
                break;

            int close = body.IndexOf('}', open);
            if (body[open] == '}' || close < 0 || !IsName(body.AsSpan(open + 1, close - open - 1)))
            {
                refusals.Add(
                    $"{file}: the brace at offset {open.ToString(CultureInfo.InvariantCulture)} opens no placeholder.");
                return null;
            }

            if (open > position)
                parts.Add(new TemplatePart(body[position..open], IsPlaceholder: false));

            parts.Add(new TemplatePart(body[(open + 1)..close], IsPlaceholder: true));
            position = close + 1;
        }

        if (position < body.Length)
            parts.Add(new TemplatePart(body[position..], IsPlaceholder: false));

        return parts;
    }

    private static bool IsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
            return false;

        foreach (char c in name)
        {
            if (!char.IsAsciiLetter(c))
                return false;
        }

        return true;
    }

    private static Dictionary<string, string>? ReasonsIn(string file, string text, List<string> refusals)
    {
        int before = refusals.Count;
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (string line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            string code = colon < 0 ? line.Trim() : line[..colon];
            string phrase = colon < 0 ? "" : line[(colon + 2)..].Trim();

            if (code != OtherReason && !TemplateKeys.CancellationCodes.Contains(code))
                refusals.Add($"{file}: '{code}' is not a CancelReasons code or '{OtherReason}'.");
            else if (phrase.Length == 0 || phrase.AsSpan().IndexOfAny('{', '}') >= 0)
                refusals.Add($"{file}: '{code}' has no phrase, or a phrase naming a placeholder.");
            else if (!map.TryAdd(code, phrase))
                refusals.Add($"{file}: '{code}' is phrased twice.");
        }

        foreach (string code in TemplateKeys.CancellationCodes.Append(OtherReason))
        {
            if (!map.ContainsKey(code))
                refusals.Add($"{file}: '{code}' has no phrase.");
        }

        return refusals.Count == before ? map : null;
    }

    private static IEnumerable<TemplateFile> EmbeddedFiles()
    {
        Assembly assembly = typeof(TemplateSet).Assembly;

        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(Folder, StringComparison.Ordinal)))
        {
            using Stream stream = assembly.GetManifestResourceStream(name)!;
            using StreamReader reader = new(stream, Encoding.UTF8);

            yield return new TemplateFile(name, reader.ReadToEnd());
        }
    }

    /// <summary>A resource name read as its parts, or null when it is not one this set can hold.</summary>
    private sealed record FileName(string Key, int Version, string Language, bool IsReasons)
    {
        public static FileName? Read(string name)
        {
            if (!name.StartsWith(Folder, StringComparison.Ordinal) ||
                !name.EndsWith(Extension, StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts = name[Folder.Length..^Extension.Length].Split('.');
            bool reasons = parts.Length == 4 && parts[3] == ReasonsSuffix;

            if (parts.Length != (reasons ? 4 : 3))
                return null;

            string version = parts[1];
            string language = parts[2];

            bool versionShaped = version.Length > 1 && version[0] == 'v' && version[1] != '0' &&
                int.TryParse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
            bool languageShaped = language.Length is 2 or 3 && language.All(char.IsAsciiLetterLower);

            return versionShaped && languageShaped && parts[0].Length > 0
                ? new FileName(
                    parts[0],
                    int.Parse(version.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture),
                    language,
                    reasons)
                : null;
        }
    }
}
```

- [ ] **Step 5: The renderer and what it returns**

`Rendering/RenderedMessage.cs`:

```csharp
namespace Notifications.Application.Rendering;

/// <summary>A rendered notice, with the version and languages its row stamps before the send (ADR-053).</summary>
public sealed record RenderedMessage(string Subject, string Body, int TemplateVersion, IReadOnlyList<string> Languages)
{
    /// <summary>The languages as the row's <c>Languages</c> column holds them, in rendering order.</summary>
    public string LanguageList => string.Join(',', Languages);
}
```

`Rendering/TemplateRenderer.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace Notifications.Application.Rendering;

/// <summary>Renders a notice in the deployment's languages and zone, substituting and never evaluating.</summary>
/// <remarks>
/// A customer whose locale the deployment's set holds is sent that language alone; any other, or none, is sent every
/// language of the set in one message, never a default the build agent chose (ADR-053).
/// </remarks>
public sealed class TemplateRenderer
{
    /// <summary>What joins the subjects of a message rendered in more than one language.</summary>
    public const string SubjectSeparator = " / ";

    /// <summary>The fixed rule between the bodies of a message rendered in more than one language.</summary>
    public const string LanguageRule = "\n\n----------------------------------------\n\n";

    /// <summary>What a dropped value renders as: one mark in every language, so no phrase is owed.</summary>
    public const string Absent = "—";

    private readonly TemplateSet _templates;
    private readonly string[] _languages;
    private readonly Dictionary<string, CultureInfo> _cultures;
    private readonly TimeZoneInfo _zone;

    private TemplateRenderer(TemplateSet templates, string[] languages, TimeZoneInfo zone)
    {
        _templates = templates;
        _languages = languages;
        _zone = zone;
        _cultures = languages.ToDictionary(l => l, CultureInfo.GetCultureInfo, StringComparer.Ordinal);
    }

    /// <summary>The deployment's languages, in the order a message in all of them shows them.</summary>
    public IReadOnlyList<string> Languages => _languages;

    /// <summary>A renderer for one deployment, or a <see cref="TemplateSetException"/> naming every refusal.</summary>
    public static TemplateRenderer Create(TemplateSet templates, IReadOnlyList<string> languages, string timeZone)
    {
        IReadOnlyList<string> refusals = Refusals(templates, languages, timeZone);
        if (refusals.Count > 0)
            throw new TemplateSetException(refusals);

        return new TemplateRenderer(templates, [.. languages], TimeZoneInfo.FindSystemTimeZoneById(timeZone));
    }

    /// <summary>Why the templates cannot serve these values, each refusal naming the file or value at fault.</summary>
    public static IReadOnlyList<string> Refusals(
        TemplateSet templates,
        IReadOnlyList<string> languages,
        string timeZone)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(languages);

        List<string> refusals = [];

        if (languages.Count == 0)
            refusals.Add("The language set is empty; ADR-053 makes it a set of at least one.");

        foreach (string language in languages.GroupBy(l => l, StringComparer.Ordinal).Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            refusals.Add($"The language set names '{language}' twice.");
        }

        foreach (string language in languages.Distinct(StringComparer.Ordinal))
        {
            if (!KnowsCulture(language))
                refusals.Add($"The runtime has no culture for '{language}', so its dates and amounts cannot be read.");
        }

        refusals.AddRange(templates.MissingFor(languages.Distinct(StringComparer.Ordinal))
            .Select(file => $"{file} is missing, and the language set requires it."));

        // An IANA id, so a Windows name that converts on one host and not another is refused everywhere.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out TimeZoneInfo? zone) || !zone.HasIanaId)
            refusals.Add($"'{timeZone}' is not an IANA time zone this runtime knows.");

        return refusals;
    }

    /// <summary>One row's notice, in the locale's language when the set holds it, else in all of them.</summary>
    public RenderedMessage Render(string templateKey, NotificationParameters parameters, string? locale)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        int version = _templates.CurrentVersion(templateKey);
        string[] languages = LanguagesFor(locale);
        List<string> subjects = [];
        List<string> bodies = [];

        foreach (string language in languages)
        {
            // Create refused every gap at start, so a miss here is a set that changed beneath a running host.
            Template template = _templates.Find(templateKey, version, language) ??
                throw new InvalidOperationException($"{templateKey} v{version} has no {language} template.");

            subjects.Add(template.Subject);
            bodies.Add(Fill(template, parameters));
        }

        return new RenderedMessage(
            string.Join(SubjectSeparator, subjects),
            string.Join(LanguageRule, bodies),
            version,
            languages);
    }

    private static bool KnowsCulture(string language)
    {
        try
        {
            return !CultureInfo.GetCultureInfo(language).Equals(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            // An image in invariant globalisation mode lands here for every language but the invariant one.
            return false;
        }
    }

    // The locale's primary subtag, so a realm's "ru-RU" selects a deployment's "ru".
    private string[] LanguagesFor(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return _languages;

        int end = locale.IndexOfAny(['-', '_']);
        string language = (end < 0 ? locale : locale[..end]).ToLowerInvariant();

        return Array.IndexOf(_languages, language) >= 0 ? [language] : _languages;
    }

    private string Fill(Template template, NotificationParameters parameters)
    {
        StringBuilder text = new();

        foreach (TemplatePart part in template.Body)
            text.Append(part.IsPlaceholder ? Value(part.Text, template, parameters) : part.Text);

        return text.ToString();
    }

    private string Value(string placeholder, Template template, NotificationParameters parameters)
    {
        CultureInfo culture = _cultures[template.Language];

        return placeholder switch
        {
            PlaceholderNames.OrderId => parameters.OrderId.ToString("D"),
            PlaceholderNames.Date => TimeZoneInfo.ConvertTime(parameters.OccurredAt, _zone).ToString("D", culture),

            // The decimal's own scale, so 10.125 renders three places and 42.10 two: nothing here rounds (ADR-053).
            PlaceholderNames.Amount => parameters.Amount is decimal amount
                ? amount.ToString("N" + amount.Scale.ToString(CultureInfo.InvariantCulture), culture)
                : Absent,
            PlaceholderNames.Currency => parameters.Currency ?? Absent,
            PlaceholderNames.TrackingNumber => parameters.TrackingNumber ?? Absent,
            PlaceholderNames.CancelReason => Phrase(template, parameters.CancelReason),
            _ => throw new InvalidOperationException($"{template.Key} names {placeholder}, which no value fills."),
        };
    }

    private string Phrase(Template template, string? code)
    {
        IReadOnlyDictionary<string, string> reasons = _templates.Reasons(template.Version, template.Language) ??
            throw new InvalidOperationException($"{template.Key} v{template.Version} has no {template.Language} map.");

        return code is not null && reasons.TryGetValue(code, out string? phrase)
            ? phrase
            : reasons[TemplateSet.OtherReason];
    }
}
```

- [ ] **Step 6: The resources**

`Notifications.Application.csproj`, a group of its own after the package
group:

```xml
  <ItemGroup>
    <!-- Named by path, so a refusal prints the file; WithCulture, since MSBuild reads ".en." as a satellite. -->
    <EmbeddedResource Include="Templates\*.txt" LogicalName="Templates/%(Filename)%(Extension)" WithCulture="false" />
  </ItemGroup>
```

The twenty-four files, each exactly as below, UTF-8 without a byte-order
mark and ending in a newline; `.editorconfig` checks them out CRLF and
`TemplateSet` reads either ending as LF, which
`A_checkout_s_line_endings_change_nothing_a_customer_reads` pins.

**English.**

`Templates/order-placed.v1.en.txt`:

```text
Subject: We have received your order

Hello,

Thank you for your order. We are now checking that everything is in stock and taking your payment, and we will write again when the order is confirmed.

Order: {OrderId}
Placed: {Date}
Total: {Amount} {Currency}

This is a service message about an order you placed with us.
```

`Templates/order-confirmed.v1.en.txt`:

```text
Subject: Your order is confirmed

Hello,

Your order is confirmed and your payment has been accepted. We will write again when it has been despatched.

Order: {OrderId}
Confirmed: {Date}
Total: {Amount} {Currency}

This is a service message about an order you placed with us.
```

`Templates/order-cancelled.v1.en.txt`:

```text
Subject: Your order has been cancelled

Hello,

We are sorry to tell you that your order has been cancelled. {CancelReason}

Order: {OrderId}
Cancelled: {Date}

If a payment was taken for this order, we will write to you separately when it has been refunded.

This is a service message about an order you placed with us.
```

`Templates/payment-declined.v1.en.txt`:

```text
Subject: Your payment was not accepted

Hello,

The payment for your order was not accepted. We will write to you separately about what this means for the order.

Order: {OrderId}
Date: {Date}

This is a service message about an order you placed with us.
```

`Templates/payment-refunded.v1.en.txt`:

```text
Subject: Your refund has been issued

Hello,

We have refunded your payment for this order. Depending on your bank, it may take a few working days to reach your account.

Order: {OrderId}
Refunded: {Date}
Amount: {Amount} {Currency}

This is a service message about an order you placed with us.
```

`Templates/shipment-dispatched.v1.en.txt`:

```text
Subject: Your order is on its way

Hello,

Your order has been despatched. You can follow its delivery with the carrier using the tracking number below.

Order: {OrderId}
Despatched: {Date}
Tracking number: {TrackingNumber}

This is a service message about an order you placed with us.
```

`Templates/shipment-delivered.v1.en.txt`:

```text
Subject: Your order has been delivered

Hello,

Your order has been delivered.

Order: {OrderId}
Delivered: {Date}
Tracking number: {TrackingNumber}

This is a service message about an order you placed with us.
```

`Templates/order-cancelled.v1.en.reasons.txt`:

```text
out_of_stock: Some of the items in it were out of stock.
stock_timeout: We could not reserve the items in it in time.
payment_declined: The payment for it was not accepted.
payment_timeout: We did not receive confirmation of the payment in time.
customer_request: It was cancelled at your request.
*: We were unable to complete it.
```

**Kazakh.**

`Templates/order-placed.v1.kk.txt`:

```text
Subject: Тапсырысыңызды қабылдадық

Сәлеметсіз бе!

Тапсырысыңыз үшін рақмет. Қазір тауарлардың бар-жоғын тексеріп, төлемді өңдеп жатырмыз. Тапсырыс расталғанда сізге қайта жазамыз.

Тапсырыс: {OrderId}
Рәсімделген күні: {Date}
Сомасы: {Amount} {Currency}

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/order-confirmed.v1.kk.txt`:

```text
Subject: Тапсырысыңыз расталды

Сәлеметсіз бе!

Тапсырысыңыз расталды, төлеміңіз қабылданды. Тапсырыс жөнелтілгенде сізге қайта жазамыз.

Тапсырыс: {OrderId}
Расталған күні: {Date}
Сомасы: {Amount} {Currency}

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/order-cancelled.v1.kk.txt`:

```text
Subject: Тапсырысыңыз жойылды

Сәлеметсіз бе!

Өкінішке қарай, тапсырысыңыз жойылды. {CancelReason}

Тапсырыс: {OrderId}
Жойылған күні: {Date}

Егер бұл тапсырыс үшін төлем алынған болса, ақша қайтарылғанда сізге бөлек хабарлаймыз.

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/payment-declined.v1.kk.txt`:

```text
Subject: Төлеміңіз қабылданбады

Сәлеметсіз бе!

Тапсырысыңыз үшін төлем қабылданбады. Бұның тапсырысқа қатысты нені білдіретінін сізге бөлек жазамыз.

Тапсырыс: {OrderId}
Күні: {Date}

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/payment-refunded.v1.kk.txt`:

```text
Subject: Тапсырыс үшін ақшаңыз қайтарылды

Сәлеметсіз бе!

Осы тапсырыс үшін төлемді қайтардық. Банкіңізге байланысты ақша шотыңызға бірнеше жұмыс күні ішінде түсуі мүмкін.

Тапсырыс: {OrderId}
Қайтарылған күні: {Date}
Сомасы: {Amount} {Currency}

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/shipment-dispatched.v1.kk.txt`:

```text
Subject: Тапсырысыңыз жөнелтілді

Сәлеметсіз бе!

Тапсырысыңыз жөнелтілді. Жеткізуді төмендегі трек-нөмір бойынша тасымалдаушыдан бақылай аласыз.

Тапсырыс: {OrderId}
Жөнелтілген күні: {Date}
Трек-нөмір: {TrackingNumber}

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/shipment-delivered.v1.kk.txt`:

```text
Subject: Тапсырысыңыз жеткізілді

Сәлеметсіз бе!

Тапсырысыңыз жеткізілді.

Тапсырыс: {OrderId}
Жеткізілген күні: {Date}
Трек-нөмір: {TrackingNumber}

Бұл — бізде рәсімдеген тапсырысыңыз туралы қызметтік хабарлама.
```

`Templates/order-cancelled.v1.kk.reasons.txt`:

```text
out_of_stock: Тапсырыстағы кейбір тауарлар қоймада қалмады.
stock_timeout: Тапсырыстағы тауарларды уақытында брондай алмадық.
payment_declined: Тапсырыс үшін төлем қабылданбады.
payment_timeout: Төлемнің расталғаны туралы хабарды уақытында алмадық.
customer_request: Тапсырыс сіздің өтінішіңіз бойынша жойылды.
*: Тапсырысты орындай алмадық.
```

**Russian.**

`Templates/order-placed.v1.ru.txt`:

```text
Subject: Мы получили ваш заказ

Здравствуйте!

Спасибо за заказ. Сейчас мы проверяем наличие товаров и проводим оплату. Мы напишем вам снова, когда заказ будет подтверждён.

Заказ: {OrderId}
Дата оформления: {Date}
Сумма: {Amount} {Currency}

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/order-confirmed.v1.ru.txt`:

```text
Subject: Ваш заказ подтверждён

Здравствуйте!

Ваш заказ подтверждён, оплата принята. Мы напишем вам снова, когда заказ будет отправлен.

Заказ: {OrderId}
Дата подтверждения: {Date}
Сумма: {Amount} {Currency}

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/order-cancelled.v1.ru.txt`:

```text
Subject: Ваш заказ отменён

Здравствуйте!

К сожалению, ваш заказ отменён. {CancelReason}

Заказ: {OrderId}
Дата отмены: {Date}

Если за этот заказ была списана оплата, мы отдельно сообщим вам, когда деньги будут возвращены.

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/payment-declined.v1.ru.txt`:

```text
Subject: Оплата не принята

Здравствуйте!

Оплата вашего заказа не была принята. Мы отдельно напишем вам о том, что это означает для заказа.

Заказ: {OrderId}
Дата: {Date}

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/payment-refunded.v1.ru.txt`:

```text
Subject: Деньги за заказ возвращены

Здравствуйте!

Мы вернули оплату за этот заказ. В зависимости от вашего банка деньги могут поступить на счёт в течение нескольких рабочих дней.

Заказ: {OrderId}
Дата возврата: {Date}
Сумма: {Amount} {Currency}

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/shipment-dispatched.v1.ru.txt`:

```text
Subject: Ваш заказ отправлен

Здравствуйте!

Ваш заказ отправлен. Отслеживать доставку можно у перевозчика по трек-номеру ниже.

Заказ: {OrderId}
Дата отправки: {Date}
Трек-номер: {TrackingNumber}

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/shipment-delivered.v1.ru.txt`:

```text
Subject: Ваш заказ доставлен

Здравствуйте!

Ваш заказ доставлен.

Заказ: {OrderId}
Дата доставки: {Date}
Трек-номер: {TrackingNumber}

Это служебное сообщение о заказе, который вы оформили у нас.
```

`Templates/order-cancelled.v1.ru.reasons.txt`:

```text
out_of_stock: Некоторых товаров из заказа не оказалось в наличии.
stock_timeout: Нам не удалось вовремя зарезервировать товары из заказа.
payment_declined: Оплата заказа не была принята.
payment_timeout: Мы не получили подтверждение оплаты вовремя.
customer_request: Заказ отменён по вашей просьбе.
*: Мы не смогли выполнить заказ.
```


- [ ] **Step 7: Run the suite and read the allow-list's answer**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
```

Expected: every new test green — the twenty-one renders among them — and
`Application_references_only_what_the_dependency_table_allows` red, naming
`System.Memory`: `TemplateSet`'s span searches reach `MemoryExtensions`,
whose reference assembly is that one. In `allowed`, after
`"System.Linq.Expressions",`:

```csharp
            "System.Memory",
```

Run again: green, 0 warnings. The scratch run of this layer counted 155
Application tests over Tasks 1–5 and the suite's tests from PR-1 and PR-3
come on top.

- [ ] **Step 8: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): twenty-one templates, three cancellation maps and TemplateRenderer, refused at start by file"
```

The body argues the cancellation map as a versioned file rather than code,
`WithCulture="false"` with the satellite measurement, the primary-subtag
reading of a locale and the absent mark, and lists the refusals table.

---

### Task 6: `OrderRecords`, the repositories and `AddOrderRecords`

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/OrderRecordConfiguration.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/NotificationRepository.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/OrderRecordRepository.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Persistence/NotificationsDbContext.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs`
- Create (generated): `Notifications.Infrastructure/Persistence/Migrations/<id>_AddOrderRecords.cs`
  and its designer, and the rewritten `NotificationsDbContextModelSnapshot.cs`
- Test: `tests/Notifications.Worker.Tests/OrderRecordsSchemaTests.cs`
- Modify: `tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs` — the applied list

**Interfaces:**
- Consumes: Task 1; PR-1's `NotificationsDbContext` and
  `ServiceFixture.ColumnsAsync`.
- Produces: `notifications.OrderRecords(OrderId uniqueidentifier PK,
  CustomerId uniqueidentifier, CancelledAt datetimeoffset(7) NULL,
  CancelReason nvarchar(32) NULL, CancelOrigin nvarchar(32) NULL,
  RecordedAt datetimeoffset(7))`; `NotificationsDbContext.OrderRecords`;
  `INotificationRepository` and `IOrderRecordRepository` resolvable, scoped.

**The key is the order's id, and that is the race's answer.** Two of
Ordering's events for one order consumed at once both find no record and
both insert; the second insert fails on the primary key, the delivery is
retried by `RetryPolicy.Standard` from the top of the pipeline — the inbox
filter included, so nothing was recorded — and the retry finds the record
and moves it as a second arrival would. No `RowVersion` is owed: the record
has one mover, the cancellation, and it moves once.

No index beyond the key: PR-5's retention pass selects by `RecordedAt`, and
its index arrives with the query that needs it, as PR-3 left
`ContactRecords`'.

- [ ] **Step 1: Write the failing schema tests**

`tests/Notifications.Worker.Tests/OrderRecordsSchemaTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Infrastructure.Persistence;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The order record's table against the engine the migrator ran on.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderRecordsSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_table_holds_the_customer_s_id_the_cancellation_and_nothing_else()
    {
        string[] columns = await fixture.ColumnsAsync("notifications", "OrderRecords");

        // Pseudonymous personal data, so the list is exact: a column added here owes §11.7's erasure path.
        columns.ShouldBe(
            ["CancelOrigin", "CancelReason", "CancelledAt", "CustomerId", "OrderId", "RecordedAt"],
            ignoreOrder: true);
    }

    [Fact]
    public async Task One_record_per_order_is_the_database_s_rule()
    {
        Guid order = Guid.CreateVersion7();

        await SaveAsync(OrderRecord.For(order, Guid.CreateVersion7(), Now));

        // The second of two first arrivals loses here, and its retried delivery finds the winner's row.
        await Should.ThrowAsync<DbUpdateException>(() => SaveAsync(OrderRecord.For(order, Guid.CreateVersion7(), Now)));
    }

    [Fact]
    public async Task A_cancelled_record_round_trips_with_its_codes()
    {
        OrderRecord record = OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        record.Cancel(CancelReasons.CustomerRequest, CancelOrigins.User, Now.AddMinutes(2));

        await SaveAsync(record);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IOrderRecordRepository records = scope.ServiceProvider.GetRequiredService<IOrderRecordRepository>();

        OrderRecord read = (await records.GetAsync(record.OrderId, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
        read.CustomerId.ShouldBe(record.CustomerId);
        read.CancelledAt.ShouldBe(Now.AddMinutes(2));
        read.CancelReason.ShouldBe(CancelReasons.CustomerRequest);
        read.CancelOrigin.ShouldBe(CancelOrigins.User);
        read.RecordedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task The_notice_repository_finds_a_notice_by_its_unique_key()
    {
        Guid eventId = Guid.CreateVersion7();

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            db.NotificationLog.Add(
                Notification.Pending(eventId, "order-placed", Guid.CreateVersion7(), """{"v":1}""", Now));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using AsyncServiceScope reading = fixture.Factory.Services.CreateAsyncScope();
        INotificationRepository notifications = reading.ServiceProvider.GetRequiredService<INotificationRepository>();

        (await notifications.ExistsAsync(eventId, "order-placed", TestContext.Current.CancellationToken))
            .ShouldBeTrue();
        (await notifications.ExistsAsync(eventId, "order-confirmed", TestContext.Current.CancellationToken))
            .ShouldBeFalse("the key is the event and the template together");
    }

    private async Task SaveAsync(OrderRecord record)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        db.OrderRecords.Add(record);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
```

`Common.Contracts` reaches this suite through the worker's project closure,
as it reached Shipping's.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~OrderRecordsSchemaTests"
```

Expected: compile failure on `db.OrderRecords`; once Step 3's `DbSet` is in
and before the migration, `Invalid object name 'notifications.OrderRecords'`;
and the two repositories unresolvable until Step 3's registration.

- [ ] **Step 3: The configuration, the set, the repositories, the registration**

`Persistence/OrderRecordConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

/// <summary>The <c>OrderRecords</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class OrderRecordConfiguration : IEntityTypeConfiguration<OrderRecord>
{
    public void Configure(EntityTypeBuilder<OrderRecord> builder)
    {
        builder.ToTable("OrderRecords", "notifications");

        // The order's own id, the commuting writers' meeting point: two first arrivals cannot both insert.
        builder.HasKey(r => r.OrderId);
        builder.Property(r => r.OrderId).ValueGeneratedNever();

        builder.Property(r => r.CancelReason).HasMaxLength(OrderRecordLimits.MaxCodeLength);
        builder.Property(r => r.CancelOrigin).HasMaxLength(OrderRecordLimits.MaxCodeLength);
    }
}
```

`NotificationsDbContext.cs`, after the `NotificationLog` set PR-1 added:

```csharp
    /// <summary>ADR-017's local projection of Ordering's events, which four notices wait on for a customer.</summary>
    public DbSet<OrderRecord> OrderRecords => Set<OrderRecord>();

```

`Persistence/NotificationRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class NotificationRepository(NotificationsDbContext db) : INotificationRepository
{
    // The unique key's own columns, so the read is the index's seek (§7.2).
    public Task<bool> ExistsAsync(Guid eventId, string templateKey, CancellationToken ct) =>
        db.NotificationLog.AnyAsync(n => n.EventId == eventId && n.TemplateKey == templateKey, ct);

    public void Add(Notification notification) => db.NotificationLog.Add(notification);
}
```

`Persistence/OrderRecordRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

internal sealed class OrderRecordRepository(NotificationsDbContext db) : IOrderRecordRepository
{
    public Task<OrderRecord?> GetAsync(Guid orderId, CancellationToken ct) =>
        db.OrderRecords.SingleOrDefaultAsync(r => r.OrderId == orderId, ct);

    public void Add(OrderRecord record) => db.OrderRecords.Add(record);
}
```

`DependencyInjection.cs`: `using Notifications.Application.Records;` in
sorted position, and the rendered placeholder line

```csharp
        // §5.6's repository registrations join with the first aggregate.
```

becomes

```csharp
        // §6.3's repositories: the notices owed, and the order record four of them wait on for a customer.
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IOrderRecordRepository, OrderRecordRepository>();
```

If PR-3 already replaced that line with its contact store's registration,
the two lines go beside it with this comment.

- [ ] **Step 4: Generate the migration**

```bash
dotnet tool restore
dotnet ef migrations add AddOrderRecords \
    --project src/Services/Notifications/Notifications.Infrastructure \
    --startup-project src/Services/Notifications/Notifications.Migrator \
    --output-dir Persistence/Migrations
```

Open it. It holds exactly one `CreateTable` for `notifications.OrderRecords`
and no `CreateIndex`, and `git diff -- '*NotificationsDbContextModelSnapshot.cs'`
adds the `Notifications.Application.Records.OrderRecord` entity block and
changes no other line — a snapshot that drifted under PR-3 would show here
as a second operation. Give the hand-authored file the house dress PR-1
gave `AddNotificationLog`, leaving the designer and the snapshot as the
tool wrote them:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>ADR-017's order record, generated from <see cref="OrderRecordConfiguration"/>.</summary>
public partial class AddOrderRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderRecords",
            schema: "notifications",
            columns: table => new
            {
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancelReason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                CancelOrigin = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderRecords", x => x.OrderId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "OrderRecords",
            schema: "notifications");
    }
}
```

The generated id must sort after `AddContactRecords`'; if it does not, the
branch was cut before PR-3 merged, and the migration is regenerated on the
rebased branch rather than renamed.

- [ ] **Step 5: The applied list**

`tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs`:
`applied.Length.ShouldBe(7);` becomes `applied.Length.ShouldBe(8);`, and after
`applied[6].ShouldEndWith("_AddContactRecords");`:

```csharp
        applied[7].ShouldEndWith("_AddOrderRecords");
```

- [ ] **Step 6: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~OrderRecordsSchemaTests|FullyQualifiedName~DatabaseSmokeTests"
```

Expected: 0 warnings; green. A migration rewritten in review is followed by
`docker compose down -v` before the migrator's answer is believed (spec,
section 6).

- [ ] **Step 7: Commit**

```bash
git add src/Services/Notifications tests/Notifications.Worker.Tests
git commit -m "feat(notifications): the OrderRecords table, its repositories and AddOrderRecords"
```

The body says the primary key is the race's answer and why no row version
is owed.

---

### Task 7: `NotificationsJurisdictionOptions`, refused at start

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Jurisdiction/NotificationsJurisdictionOptions.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Jurisdiction/NotificationsJurisdictionValidator.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs` — the binding and the renderer
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Notifications.Infrastructure.csproj` — `InternalsVisibleTo`
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` — five parameters and the invented values
- Test: `tests/Notifications.Worker.Tests/JurisdictionOptionsTests.cs`

**Interfaces:**
- Consumes: Task 5's `TemplateRenderer.Refusals`, `TemplateRenderer.Create`
  and `TemplateSet.Embedded`; PR-3's `ContactOptions.StaleCeiling`.
- Produces:

```csharp
namespace Notifications.Infrastructure.Jurisdiction;

public sealed class NotificationsJurisdictionOptions
{
    public const string SectionName = "Jurisdiction";
    public const string MinimumWindow = "00:00:01";
    public const string MaximumWindow = "3650.00:00:00";
    public IReadOnlyList<string>? Languages { get; init; }    // Jurisdiction:Languages:0…n
    public string? TimeZone { get; init; }
    public TimeSpan? LogRetention { get; init; }
    public TimeSpan? ContactRetention { get; init; }
    public TimeSpan? OrderRetention { get; init; }
}
internal sealed class NotificationsJurisdictionValidator(ContactOptions contacts)
    : IValidateOptions<NotificationsJurisdictionOptions>;
```

and `TemplateRenderer` resolvable from the host as a singleton;
`NotificationsWorkerFactory`'s `languages`, `timeZone`, `logRetention`,
`contactRetention` and `orderRetention` parameters with `InventedLanguages`,
`InventedTimeZone`, `InventedLogRetention`, `InventedContactRetention` and
`InventedOrderRetention`.

**`ShippingJurisdictionOptions`' form, with two members it never needed.**
Each window is a nullable `TimeSpan` under `[Required]` and a stated
`[Range]` — one second to ten years, the bounds Shipping's class names — so a
missing key and an impossible value are §15.4's failure at start and never a
clamp (ADR-053 rule 1). `Languages` is `[Required]` and `[MinLength(1)]`,
ADR-053's "a non-empty bound on the language set", and binds as a list from
`Jurisdiction:Languages:0…n`, the indexed spelling §15.4 already uses for
`Ingress__TrustedNetworks__0…n`. `TimeZone` is `[Required]`. What no
annotation can state is the validator's, and it runs only once the
annotations pass:

- **every rule Task 5 states** — each language's templates and map at each
  key's current version, a culture for each language, no language twice, an
  IANA zone the runtime knows — by calling `TemplateRenderer.Refusals`, the
  code that would otherwise fail the first send, so a deployment and a send
  cannot disagree about what is renderable;
- **a template file that fails to parse**, which surfaces here because
  `TemplateSet.Embedded` throws and the validator folds its refusals in;
- **`ContactRetention` below `ContactOptions.StaleCeiling`**, since a
  contact row deleted before its ceiling makes the ceiling a number nothing
  reaches (spec, section 6). Equal passes: the row is served to the last
  instant the ceiling allows and then deleted.

**One validator, not `ValidateDataAnnotations` plus a second.** Shipping's
`AnnotatedOptionsValidator` exists so a project carries no
`Microsoft.Extensions.Options.DataAnnotations`; this validator runs the same
`Validator.TryValidateObject` and its own rules after, in one
`ValidateOptionsResult`, so one failed start names every refusal.

**The renderer is a singleton built from the bound value**, by
`TemplateRenderer.Create`; `ValidateOnStart` has refused the host before
anything resolves it, so `Create` cannot throw in a running host.

**The fixture's values are ADR-053 rule 2's made-up jurisdiction**: `kk`
then `en`, so a multi-language assertion reads the set's order and not the
shipped order; `Pacific/Chatham`, twelve and three-quarter hours ahead in
October, so a date at the edge of a day shows which side it landed on;
windows of 1013, 17 and 71 days, which no real deployment and not Compose
(Task 10) uses.

- [ ] **Step 1: The factory's invented jurisdiction**

`tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` gains five
parameters after its last, each defaulted, so every existing caller
compiles unchanged and a caller names the one it overrides:

```csharp
    IReadOnlyList<string>? languages = null,
    string timeZone = NotificationsWorkerFactory.InventedTimeZone,
    string logRetention = NotificationsWorkerFactory.InventedLogRetention,
    string contactRetention = NotificationsWorkerFactory.InventedContactRetention,
    string orderRetention = NotificationsWorkerFactory.InventedOrderRetention
```

the members, beside `UnreachableAuthority`:

```csharp
    /// <summary>ADR-053 rule 2's made-up language set, Kazakh first so a test reads the set's own order.</summary>
    public static readonly IReadOnlyList<string> InventedLanguages = ["kk", "en"];

    /// <summary>Twelve and three-quarter hours ahead in October, so a date shows its side of midnight.</summary>
    public const string InventedTimeZone = "Pacific/Chatham";

    /// <summary>A window no deployment would choose, so a test passing under it read its configuration.</summary>
    public const string InventedLogRetention = "1013.00:00:00";

    /// <inheritdoc cref="InventedLogRetention"/>
    public const string InventedContactRetention = "17.00:00:00";

    /// <inheritdoc cref="InventedLogRetention"/>
    public const string InventedOrderRetention = "71.00:00:00";
```

and in `ConfigureWebHost`, after the last `UseSetting`, with
`using Microsoft.Extensions.Configuration;`,
`using Notifications.Infrastructure.Jurisdiction;` and
`using System.Globalization;` in sorted position where absent:

```csharp
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:TimeZone", timeZone)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:LogRetention", logRetention)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:ContactRetention", contactRetention)
            .UseSetting($"{NotificationsJurisdictionOptions.SectionName}:OrderRetention", orderRetention)
            // A list binds by index, and an empty one sets no key at all, which is the refusal a test asks for.
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                (languages ?? InventedLanguages).Select((language, index) => new KeyValuePair<string, string?>(
                    $"{NotificationsJurisdictionOptions.SectionName}:Languages:" +
                    index.ToString(CultureInfo.InvariantCulture),
                    language))))
```

- [ ] **Step 2: Write the failing tests**

`tests/Notifications.Worker.Tests/JurisdictionOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Jurisdiction;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-053 rule 1: a jurisdiction the files, the runtime or ADR-052 cannot serve fails the start.</summary>
public sealed class JurisdictionOptionsTests
{
    private static readonly Dictionary<string, string?> Invented = new()
    {
        ["Jurisdiction:Languages:0"] = "kk",
        ["Jurisdiction:Languages:1"] = "en",
        ["Jurisdiction:TimeZone"] = NotificationsWorkerFactory.InventedTimeZone,
        ["Jurisdiction:LogRetention"] = NotificationsWorkerFactory.InventedLogRetention,
        ["Jurisdiction:ContactRetention"] = NotificationsWorkerFactory.InventedContactRetention,
        ["Jurisdiction:OrderRetention"] = NotificationsWorkerFactory.InventedOrderRetention
    };

    /// <summary>The production binding and validator over a configuration of the test's own.</summary>
    private static ServiceProvider Validating(Dictionary<string, string?> values)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton(new ContactOptions());
        services
            .AddOptions<NotificationsJurisdictionOptions>()
            .BindConfiguration(NotificationsJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<NotificationsJurisdictionOptions>, NotificationsJurisdictionValidator>();

        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> With(string key, string? value)
    {
        Dictionary<string, string?> values = new(Invented) { [key] = value };
        if (value is null)
            values.Remove(key);

        return values;
    }

    [Fact]
    public void An_invented_jurisdiction_binds_its_language_set_in_its_own_order()
    {
        // ADR-053 rule 2's control, without which the refusals below could pass against a class nothing satisfies.
        using ServiceProvider provider = Validating(Invented);

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        NotificationsJurisdictionOptions bound =
            provider.GetRequiredService<IOptions<NotificationsJurisdictionOptions>>().Value;
        bound.Languages.ShouldBe(["kk", "en"]);
        bound.ContactRetention.ShouldBe(TimeSpan.FromDays(17));
    }

    [Theory]
    [InlineData("Jurisdiction:LogRetention", "", "LogRetention")]
    [InlineData("Jurisdiction:ContactRetention", "00:00:00", "ContactRetention")]
    [InlineData("Jurisdiction:OrderRetention", "3651.00:00:00", "OrderRetention")]
    [InlineData("Jurisdiction:TimeZone", null, "TimeZone")]
    [InlineData("Jurisdiction:TimeZone", "Mars/Olympus_Mons", "Mars/Olympus_Mons")]
    [InlineData("Jurisdiction:TimeZone", "Central Asia Standard Time", "Central Asia Standard Time")]
    [InlineData("Jurisdiction:Languages:1", "de", "Templates/order-placed.v1.de.txt")]
    [InlineData("Jurisdiction:Languages:1", "kk", "'kk' twice")]
    [InlineData("Jurisdiction:ContactRetention", "23:59:59", "StaleCeiling")]
    public void Each_refusal_names_what_to_fix(string key, string? value, string named)
    {
        using ServiceProvider provider = Validating(With(key, value));

        OptionsValidationException thrown = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate());

        thrown.Message.ShouldContain(named);
    }

    [Fact]
    public void An_empty_language_set_is_refused()
    {
        Dictionary<string, string?> values = new(Invented);
        values.Remove("Jurisdiction:Languages:0");
        values.Remove("Jurisdiction:Languages:1");
        using ServiceProvider provider = Validating(values);

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Languages");
    }

    [Fact]
    public void A_contact_window_equal_to_the_stale_ceiling_passes()
    {
        // The floor is the ceiling itself: a row is served to the last instant ADR-052 allows, then deleted.
        using ServiceProvider provider = Validating(With("Jurisdiction:ContactRetention", "1.00:00:00"));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_host_starts_with_the_invented_jurisdiction_and_renders_in_its_order()
    {
        // The control for the host-level refusals below: the same unreachable hosts start.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit);

        Should.NotThrow(() => factory.CreateClient());

        factory.Services.GetRequiredService<TemplateRenderer>().Languages.ShouldBe(["kk", "en"]);
    }

    [Theory]
    [InlineData("LogRetention", "00:00:00")]
    [InlineData("ContactRetention", "23:59:59")]
    [InlineData("OrderRetention", "")]
    [InlineData("TimeZone", "Mars/Olympus_Mons")]
    public void The_host_refuses_to_start_on_a_window_or_zone_it_cannot_use(string member, string value)
    {
        using NotificationsWorkerFactory factory = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            timeZone: member == "TimeZone" ? value : NotificationsWorkerFactory.InventedTimeZone,
            logRetention: member == "LogRetention" ? value : NotificationsWorkerFactory.InventedLogRetention,
            contactRetention:
                member == "ContactRetention" ? value : NotificationsWorkerFactory.InventedContactRetention,
            orderRetention: member == "OrderRetention" ? value : NotificationsWorkerFactory.InventedOrderRetention);

        // The factory builds the host on first use, so the throw arrives here rather than at construction.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_host_refuses_to_start_on_a_language_the_templates_do_not_ship()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, languages: ["kk", "de"]);

        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_host_refuses_to_start_on_an_empty_language_set()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, languages: []);

        Should.Throw<Exception>(() => factory.CreateClient());
    }
}
```

The message assertions are the unit half's, over the production binding and
validator: a host that refuses to start reports through
`WebApplicationFactory`, which can surface the refusal as an
`ObjectDisposedException` carrying nothing to read, so the host half asserts
the refusal and the unit half what it says.

- [ ] **Step 3: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~JurisdictionOptionsTests"
```

Expected: compile failure on `Notifications.Infrastructure.Jurisdiction`.

- [ ] **Step 4: The options and the validator**

`Jurisdiction/NotificationsJurisdictionOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Jurisdiction;

/// <summary>ADR-053's one options class for this service: the language set, the zone and three windows.</summary>
/// <remarks>
/// Every member is a value the deployment is given and is refused at start rather than clamped (ADR-053 rule 1);
/// <see cref="NotificationsJurisdictionValidator"/> holds the rules no annotation can state.
/// </remarks>
public sealed class NotificationsJurisdictionOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Jurisdiction";

    /// <summary>One second rather than zero, which reads on a values file as "not configured".</summary>
    public const string MinimumWindow = "00:00:01";

    /// <summary>Ten years, past which a value is a typo the purge's subtraction could throw on.</summary>
    public const string MaximumWindow = "3650.00:00:00";

    /// <summary>The languages a customer is owed, in the order a message in all of them shows them.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>The IANA zone dates are rendered in, resolved at start so an unknown one fails the host.</summary>
    [Required]
    public string? TimeZone { get; init; }

    /// <summary>A terminal notice's age at deletion: statutory, as the row is the evidence (ADR-053).</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? LogRetention { get; init; }

    /// <summary>A contact row not refreshed for this long is deleted; never shorter than its stale ceiling.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? ContactRetention { get; init; }

    /// <summary>An order record this old is deleted, once no pending notice names its order.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? OrderRetention { get; init; }
}
```

`Jurisdiction/NotificationsJurisdictionValidator.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Rendering;

namespace Notifications.Infrastructure.Jurisdiction;

/// <summary>Refuses at start a jurisdiction the templates, the runtime or the contact rules cannot serve.</summary>
/// <remarks>
/// The annotations first, then the rules between members and the files: every template the language set needs, a
/// culture for each language, an IANA zone, and a contact window no shorter than its stale ceiling (ADR-052, ADR-053).
/// </remarks>
internal sealed class NotificationsJurisdictionValidator(ContactOptions contacts)
    : IValidateOptions<NotificationsJurisdictionOptions>
{
    public ValidateOptionsResult Validate(string? name, NotificationsJurisdictionOptions options)
    {
        List<ValidationResult> annotations = [];
        if (!Validator.TryValidateObject(options, new ValidationContext(options), annotations, true))
            return ValidateOptionsResult.Fail(annotations.Select(a => a.ErrorMessage ?? "Invalid."));

        List<string> failures = [];

        try
        {
            failures.AddRange(TemplateRenderer.Refusals(TemplateSet.Embedded, options.Languages!, options.TimeZone!));
        }
        catch (TemplateSetException refused)
        {
            failures.AddRange(refused.Refusals);
        }

        // A row deleted before its ceiling makes the ceiling a number nothing reaches (ADR-052).
        if (options.ContactRetention < contacts.StaleCeiling)
        {
            failures.Add(
                $"{NotificationsJurisdictionOptions.SectionName}:{nameof(options.ContactRetention)} is shorter " +
                $"than {nameof(ContactOptions)}.{nameof(ContactOptions.StaleCeiling)}, {contacts.StaleCeiling}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
```

`Notifications.Infrastructure.csproj`, unless an earlier PR added it, a
group of its own after the project references:

```xml
  <ItemGroup>
    <!-- The worker suite drives the production validator over a binding of its own (§15.4). -->
    <InternalsVisibleTo Include="Notifications.Worker.Tests" />
  </ItemGroup>
```

- [ ] **Step 5: The binding and the renderer, beside each other**

`DependencyInjection.cs`, with `using Microsoft.Extensions.Options;`,
`using Notifications.Application.Rendering;` and
`using Notifications.Infrastructure.Jurisdiction;` in sorted position, after
the bus's registration:

```csharp
        // ADR-053's one options class, bound beside the renderer that reads its language set and zone (§15.4).
        services
            .AddOptions<NotificationsJurisdictionOptions>()
            .BindConfiguration(NotificationsJurisdictionOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<NotificationsJurisdictionOptions>,
            NotificationsJurisdictionValidator>();

        // Built from options the validator passed at start, so a refusal is the host's and never a send's.
        services.AddSingleton(sp =>
        {
            NotificationsJurisdictionOptions jurisdiction =
                sp.GetRequiredService<IOptions<NotificationsJurisdictionOptions>>().Value;

            return TemplateRenderer.Create(TemplateSet.Embedded, jurisdiction.Languages!, jurisdiction.TimeZone!);
        });
```

The windows have no reader yet: PR-5's retention pass reads all three and
its order-record purge holds `OrderRetention`'s floor (spec, section 6).
They are bound now because the spec's section 11 gives PR-4 all five keys
and because an options class bound in two halves is one a deployment is
asked for twice.

- [ ] **Step 6: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests
```

Expected: 0 warnings; the new tests green, and every host the suite builds
over `NotificationsWorkerFactory` still starting, because the factory
supplies the invented jurisdiction.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "feat(notifications): NotificationsJurisdictionOptions refuses at start what the templates, the runtime or ContactOptions cannot serve"
```

The body lists the refusals, argues the one validator, and says why
`ContactRetention` is floored at `StaleCeiling` and not above it.

---

### Task 8: `notifications-events`

**Files:**
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Messaging/DependencyInjection.cs`
- Modify: `tests/Notifications.Worker.Tests/MessagingRegistrationTests.cs`

**Interfaces:**
- Consumes: Task 4's seven handlers, through `Common.Infrastructure`'s
  `IntegrationEventConsumer<T>`; the rendered `RetryPolicy`.
- Produces: `Notifications.Infrastructure.Messaging.DependencyInjection.EventsQueue`
  = `"notifications-events"`.

**§9.5's printed form, bare.** The inbox filter outside the in-memory
outbox, and `UseMessageRetry(RetryPolicy.Standard)` with no `Ignore<>` and no
`UseDelayedRedelivery` — Shipping's endpoint exactly, and the spec's section
10 form: §9.8 gives the mapping exceptions' exclusion to a command endpoint
alone, and no handler here maps a command, so `ContractMappingException`
cannot reach this queue. No redelivery and no second ladder, because no
consumer meets a fault that is a wait (spec, section 10). One queue rather
than seven, for the spec's reason: one database, no calls, one failure mode,
one backlog series.

**The binding is not provable here**, as Shipping's and Payments' suites say:
the harness replaces the `UsingRabbitMq` callback where the endpoint is
declared, so this suite asserts the seven registrations and Task 9 asserts
the seven bindings against the broker's own list.

- [ ] **Step 1: Write the failing test**

`tests/Notifications.Worker.Tests/MessagingRegistrationTests.cs`, with
`using Common.Contracts.Ordering.V1;`, `using Common.Contracts.Payments.V1;`,
`using Common.Contracts.Shipping.V1;` and
`using Common.Infrastructure.Messaging;` in sorted position, in the gap the
render left after `Registration_adds_the_bus_and_its_hosted_service` —
replacing the two blank lines there with one, the test, and one:

```csharp
    [Fact]
    public void Every_event_in_the_consumes_column_is_registered()
    {
        // §3.2's Consumes column; the binding is a separate claim, provable only against a real queue.
        ServiceCollection services = new();

        services.AddMassTransitMessaging(Configuration());

        foreach (Type consumer in new[]
                 {
                     typeof(IntegrationEventConsumer<OrderPlaced>),
                     typeof(IntegrationEventConsumer<OrderConfirmed>),
                     typeof(IntegrationEventConsumer<OrderCancelled>),
                     typeof(IntegrationEventConsumer<PaymentDeclined>),
                     typeof(IntegrationEventConsumer<PaymentRefunded>),
                     typeof(IntegrationEventConsumer<ShipmentDispatched>),
                     typeof(IntegrationEventConsumer<ShipmentDelivered>)
                 })
        {
            services.ShouldContain(
                d => d.ImplementationType == consumer || d.ServiceType == consumer,
                $"{consumer.Name} is in §3.2's Consumes column and has no AddConsumer");
        }
    }
```

- [ ] **Step 2: Run it to see it fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MessagingRegistrationTests"
```

Expected: the new test red on `IntegrationEventConsumer`1 is in §3.2's
Consumes column and has no AddConsumer`, the rest green.

- [ ] **Step 3: Declare the receive endpoint**

`Messaging/DependencyInjection.cs`, the whole file:

```csharp
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Notifications.Infrastructure.Messaging;

/// <summary>§9's bus, per service because its consumers and receive endpoints are its own (§9.6).</summary>
public static class DependencyInjection
{
    /// <summary>§3.2's Consumes column; one queue, as every event writes rows here and calls nothing.</summary>
    public const string EventsQueue = "notifications-events";

    public static IServiceCollection AddMassTransitMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Eager, so a host with no broker configured does not start; an empty environment variable counts as none.
        string? connectionString = configuration.GetConnectionString("RabbitMq");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:RabbitMq is not configured. The bus cannot start without it (§13.5).");
        }

        services.AddMassTransit(x =>
        {
            // On by default; §13.2 owns this platform's telemetry, and none of it leaves silently.
            x.DisableUsageTelemetry();

            // §3.2's Consumes column. Registering and binding are two statements and both
            // are needed; a consumer registered and never bound receives nothing.
            x.AddConsumer<IntegrationEventConsumer<OrderPlaced>>();
            x.AddConsumer<IntegrationEventConsumer<OrderConfirmed>>();
            x.AddConsumer<IntegrationEventConsumer<OrderCancelled>>();
            x.AddConsumer<IntegrationEventConsumer<PaymentDeclined>>();
            x.AddConsumer<IntegrationEventConsumer<PaymentRefunded>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDispatched>>();
            x.AddConsumer<IntegrationEventConsumer<ShipmentDelivered>>();

            x.UsingRabbitMq((context, cfg) =>
            {
                cfg.Host(new Uri(connectionString));

                cfg.ReceiveEndpoint(
                    EventsQueue,
                    e =>
                    {
                        // Bare, with no redelivery: no consumer meets a wait, and none maps a command (§9.8).
                        e.UseMessageRetry(RetryPolicy.Standard);

                        // Inbox outside the in-memory outbox (§9.8): the other nesting commits
                        // the inbox row before the buffered sends have flushed.
                        e.UseConsumeFilter(typeof(InboxFilter<>), context);
                        e.UseInMemoryOutbox(context);

                        e.ConfigureConsumer<IntegrationEventConsumer<OrderPlaced>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderConfirmed>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<OrderCancelled>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentDeclined>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<PaymentRefunded>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDispatched>>(context);
                        e.ConfigureConsumer<IntegrationEventConsumer<ShipmentDelivered>>(context);
                    });

                // No ConfigureEndpoints(context): it would give a consumer a queue with neither the inbox filter
                // nor the retry policy, and §9.8 admits no endpoint without InboxFilter<>.
            });
        });

        // No readiness line: AddMassTransit registers "masstransit-bus", tagged ready (§13.5). WaitUntilStarted
        // stays false, so a broker outage fails readiness rather than boot.
        return services;
    }
}
```

The `ConfigureEndpoints` comment and the readiness comment are the render's,
unchanged.

- [ ] **Step 4: Run and check the broker gate**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MessagingRegistrationTests"
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
```

Expected: green, and both Python commands exit 0 — the queue is
`notifications-`, inside the grant PR-1's mode rendered, so no grant moves.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.Worker.Tests
git commit -m "feat(notifications): notifications-events binds §3.2's seven events behind the inbox"
```

The body says the bare retry is the spec's section 10 form, with §9.8's
reason, and argues one queue rather than seven.

---

### Task 9: The consumers over a live broker, and the made-up deployment

**Files:**
- Modify: `tests/Notifications.TestSupport/ServiceFixture.cs`
- Create: `tests/Notifications.Worker.Tests/OrderEvents.cs`
- Test: `tests/Notifications.Worker.Tests/NotificationsEventsTests.cs`
- Test: `tests/Notifications.Worker.Tests/MadeUpDeploymentTests.cs`

**Interfaces:**
- Consumes: Tasks 4–8; PR-1's `ServiceFixture` and its base's protected
  `BrokerRowsAsync`; the account `notifications-svc` as `definitions.json`
  grants it.
- Produces: `ServiceFixture.DeliverAsync<T>`, `NotificationsAsync`,
  `OrderRecordAsync`, `BindingsAsync`, `BrokerPermissionsAsync`,
  `QueueDepthAsync` and `WaitUntilAsync`; `OrderEvents`.

**This is the measurement PR-1's plan deferred, and the reading behind it.**
`notifications-svc` may configure and read `^(notifications-|Common\.Contracts|MassTransit:)`
and write `^(notifications-|MassTransit:)` — no contract exchange at all.
MassTransit binds a consumed message type by declaring its exchange
(`Common.Contracts.Ordering.V1:OrderPlaced` — configure) and binding that
exchange to the endpoint's exchange (`notifications-events`); RabbitMQ asks
`exchange.bind` for *read* on the source and *write* on the destination,
and both are granted. The `IIntegrationEvent` exchange every publisher's
grant writes is the *publish* topology's, which binds a concrete exchange to
its interfaces' and so writes the interface exchange; a consumer never
applies it. A fault publishes `Fault<T>` to `MassTransit:Fault--…` exchanges,
inside `MassTransit:`; `_error` and `_skipped` are `notifications-`. So the
reading says the grant suffices, and the test below says whether it does: a
refused `exchange.bind` closes the channel, the endpoint never starts, the
bus never reports healthy and the broker lists no binding.

**Two things keep the measurement honest.** The fixture never widens the
account — PR-1's `ServiceFixture` overrides no `HarnessWrite`, and a test
reads the broker's own `list_permissions` so a later override cannot
quietly turn the measurement into a measurement of something wider. And
the suite never publishes to a contract exchange, which the account could
not: `DeliverAsync` sends to `queue:notifications-events`, which the grant
covers, and the consumer is chosen by the message's type, not by the
exchange it came through. The route from Ordering's, Payments' and
Shipping's real publishers is PR-5's cross-service test.

- [ ] **Step 1: The fixture's helpers**

`tests/Notifications.TestSupport/ServiceFixture.cs`, with
`using Common.Contracts;`, `using MassTransit;`,
`using Microsoft.EntityFrameworkCore;`, `using Notifications.Application.Records;`,
`using Xunit;` and
`using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;`
in sorted position where absent, after `ColumnsAsync`:

```csharp
    /// <summary>How long a staged step may take; a deadline, not a sleep, so a prompt step costs nothing.</summary>
    public static readonly TimeSpan StepDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Sends an event to <c>notifications-events</c> as the account may, then awaits its inbox row.</summary>
    /// <remarks>
    /// To the queue, never a contract's exchange: <c>notifications-svc</c> writes none (ADR-036), and nothing here
    /// widens it, so what the endpoint binds under this account is what the shipped grant allows.
    /// </remarks>
    public async Task DeliverAsync<T>(T message)
        where T : class, IIntegrationEvent
    {
        // Bounded, because a send the broker refuses is retried rather than failed.
        using CancellationTokenSource bounded =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(StepDeadline);

        ISendEndpoint endpoint = await Factory.Services.GetRequiredService<IBus>()
            .GetSendEndpoint(new Uri($"queue:{MessagingRegistration.EventsQueue}"));

        await endpoint.Send(
            message,
            c =>
            {
                c.MessageId = message.MessageId;
                c.CorrelationId = message.CorrelationId;
            },
            bounded.Token);

        // The inbox row is written after the consumer's command commits (§9.5), so its rows are there to read.
        await WaitUntilAsync(async () => (await InboxAsync(message.MessageId)).Count == 1);
    }

    /// <summary>Every notice owed for one order, untracked.</summary>
    public async Task<IReadOnlyList<Notification>> NotificationsAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.NotificationLog
            .AsNoTracking()
            .Where(n => n.OrderId == orderId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The order record for one order, untracked, or null.</summary>
    public async Task<OrderRecord?> OrderRecordAsync(Guid orderId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.OrderRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.OrderId == orderId, TestContext.Current.CancellationToken);
    }

    /// <summary>The exchanges bound to one queue, read from the broker itself.</summary>
    public async Task<string[]> BindingsAsync(string queue) =>
    [
        .. (await BrokerRowsAsync(["list_bindings", "source_name", "destination_name"]))
            .Where(columns => columns.Length == 2 && columns[1] == queue)
            .Select(columns => columns[0])
    ];

    /// <summary>One account's grant on the default vhost, as the broker holds it rather than as a file says.</summary>
    public async Task<(string Configure, string Write, string Read)> BrokerPermissionsAsync(string user)
    {
        string[] row = (await BrokerRowsAsync(["list_permissions"]))
            .Single(columns => columns.Length == 4 && columns[0] == user);

        return (row[1], row[2], row[3]);
    }

    /// <summary>The messages waiting in one queue, or zero when the broker has never declared it.</summary>
    public async Task<int> QueueDepthAsync(string queue)
    {
        string[]? row = (await BrokerRowsAsync(["list_queues", "name", "messages"]))
            .SingleOrDefault(columns => columns.Length == 2 && columns[0] == queue);

        return row is null ? 0 : int.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Polls to <see cref="StepDeadline"/> and throws when it lapses.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + StepDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"The staged condition did not hold within {StepDeadline}.");
    }
```

`BindingsAsync` and `WaitUntilAsync` are Shipping's fixture's, copied, as
each service's fixture keeps its own (ADR-056). `IBus` rather than
`ISendEndpointProvider`, which MassTransit registers scoped and the root
provider refuses under `ValidateScopes`.

`tests/Notifications.Worker.Tests/OrderEvents.cs` — the seven contracts,
built once:

```csharp
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Notifications.Worker.Tests;

/// <summary>§3.2's seven events for one order, each with a fresh message id, as their publishers write them.</summary>
internal static class OrderEvents
{
    public static OrderPlaced Placed(Guid order, Guid customer, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = 12345.60m,
        Currency = "KZT",
        Lines = [new PlacedLine(Guid.CreateVersion7(), 1, 12345.60m)]
    };

    public static OrderConfirmed Confirmed(Guid order, Guid customer, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        CustomerId = customer,
        TotalAmount = 12345.60m,
        Currency = "KZT",
        Lines = [new ConfirmedLine(Guid.CreateVersion7(), 1, 12345.60m)]
    };

    public static OrderCancelled Cancelled(
        Guid order, Guid customer, DateTimeOffset at, string reason, string? origin) =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            CustomerId = customer,
            Reason = reason,
            Origin = origin
        };

    public static PaymentDeclined Declined(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reason = "do_not_honour_51"
    };

    public static PaymentRefunded Refunded(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        Reference = "pay_ref_zz",
        Amount = 12345.60m,
        Currency = "KZT"
    };

    public static ShipmentDispatched Dispatched(Guid order, DateTimeOffset at, string trackingNumber = "ZZ-0042") =>
        new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = order,
            OccurredAt = at,
            OrderId = order,
            TrackingNumber = trackingNumber
        };

    public static ShipmentDelivered Delivered(Guid order, DateTimeOffset at) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = order,
        OccurredAt = at,
        OrderId = order,
        TrackingNumber = "ZZ-0042"
    };
}
```

- [ ] **Step 2: Write the tests**

`tests/Notifications.Worker.Tests/NotificationsEventsTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>§3.2's Consumes column over a real broker and the real tables, as the narrow account runs them.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationsEventsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_narrow_account_binds_every_event_in_the_consumes_column_to_the_queue()
    {
        // Healthy first, since an endpoint declares its bindings as it starts, and a refused bind never starts (§13.5).
        BusHealthStatus health = await fixture.Factory.Services.GetRequiredService<IBusControl>()
            .WaitForHealthStatus(BusHealthStatus.Healthy, ServiceFixture.StepDeadline);
        health.ShouldBe(BusHealthStatus.Healthy, "a refused exchange.bind closes the channel and the endpoint with it");

        string[] bound = await fixture.BindingsAsync(MessagingRegistration.EventsQueue);

        foreach (string exchange in new[]
                 {
                     "Common.Contracts.Ordering.V1:OrderPlaced",
                     "Common.Contracts.Ordering.V1:OrderConfirmed",
                     "Common.Contracts.Ordering.V1:OrderCancelled",
                     "Common.Contracts.Payments.V1:PaymentDeclined",
                     "Common.Contracts.Payments.V1:PaymentRefunded",
                     "Common.Contracts.Shipping.V1:ShipmentDispatched",
                     "Common.Contracts.Shipping.V1:ShipmentDelivered"
                 })
        {
            bound.ShouldContain(exchange, $"{exchange} is in §3.2's Consumes column and is not bound");
        }
    }

    [Fact]
    public async Task The_account_holds_exactly_the_grant_definitions_json_ships()
    {
        // The test above measures the shipped grant only if nothing widened it, so the broker's own answer is read.
        (string configure, string write, string read) = await fixture.BrokerPermissionsAsync("notifications-svc");

        write.ShouldBe("^(notifications-|MassTransit:)", "ADR-036: nothing to publish, so no contract to write");
        configure.ShouldBe(@"^(notifications-|Common\.Contracts|MassTransit:)");
        read.ShouldBe(@"^(notifications-|Common\.Contracts|MassTransit:)");
    }

    [Fact]
    public async Task Each_of_the_seven_events_owes_one_pending_notice_under_its_key()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.PaymentDeclined, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Refunded(order, At));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At));

        IReadOnlyList<Notification> owed = await fixture.NotificationsAsync(order);

        owed.Select(n => n.TemplateKey).ShouldBe(TemplateKeys.Placeholders.Keys, ignoreOrder: true);
        owed.ShouldAllBe(n => n.Status == NotificationStatus.Pending && n.CustomerId == null);
        ParametersFormat.Read(owed.Single(n => n.TemplateKey == TemplateKeys.PaymentDeclined).Parameters)
            .ShouldBe(new NotificationParameters { OrderId = order, OccurredAt = At });
    }

    [Fact]
    public async Task A_cancellation_first_is_a_tombstone_the_late_placement_leaves_alone()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.CustomerRequest, CancelOrigins.User));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At.AddMinutes(1)));

        OrderRecord record = (await fixture.OrderRecordAsync(order)).ShouldNotBeNull();
        record.CustomerId.ShouldBe(customer);
        record.CancelledAt.ShouldBe(At);
        record.CancelReason.ShouldBe(CancelReasons.CustomerRequest);
        record.CancelOrigin.ShouldBe(CancelOrigins.User);
        (await fixture.NotificationsAsync(order)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_placement_first_is_cancelled_by_the_late_cancellation()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At.AddMinutes(1), CancelReasons.PaymentTimeout, origin: null));

        OrderRecord record = (await fixture.OrderRecordAsync(order)).ShouldNotBeNull();
        record.RecordedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
        record.CancelledAt.ShouldBe(At.AddMinutes(1));
        record.CancelReason.ShouldBe(CancelReasons.PaymentTimeout);
        record.CancelOrigin.ShouldBeNull("an older publisher's cancellation, which ADR-049's reader interprets");
    }

    [Fact]
    public async Task A_tracking_number_with_a_bidi_override_is_dropped_and_nothing_reaches_error()
    {
        Guid order = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At, trackingNumber: $"ZZ{(char)0x202E}0042"));

        Notification owed = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        ParametersFormat.Read(owed.Parameters).TrackingNumber.ShouldBeNull();
        (await fixture.QueueDepthAsync($"{MessagingRegistration.EventsQueue}_error"))
            .ShouldBe(0, "a value another service wrote is dropped, never faulted to _error");
    }
}
```

`tests/Notifications.Worker.Tests/MadeUpDeploymentTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-053 rule 2: all seven notices render under the fixture's invented languages and zone.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class MadeUpDeploymentTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>17:00 in Almaty and 00:45 the next morning in the fixture's Chatham.</summary>
    private static readonly DateTimeOffset LateInTheDay = new(2026, 10, 2, 11, 0, 0, TimeSpan.Zero);

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Every_notice_renders_in_the_invented_languages_in_their_order_and_in_the_invented_zone()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, LateInTheDay, CancelReasons.OutOfStock, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Declined(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Refunded(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, LateInTheDay));

        TemplateRenderer renderer = fixture.Factory.Services.GetRequiredService<TemplateRenderer>();
        IReadOnlyList<Notification> owed = await fixture.NotificationsAsync(order);
        owed.Count.ShouldBe(7);

        foreach (Notification notice in owed)
        {
            // No locale, which is what a contact without one gets: every language of the set (ADR-053).
            RenderedMessage message =
                renderer.Render(notice.TemplateKey, ParametersFormat.Read(notice.Parameters), locale: null);

            message.Languages.ShouldBe(NotificationsWorkerFactory.InventedLanguages, notice.TemplateKey);
            message.Subject.Split(TemplateRenderer.SubjectSeparator).Length.ShouldBe(2, notice.TemplateKey);

            string[] bodies = message.Body.Split(TemplateRenderer.LanguageRule);
            bodies.Length.ShouldBe(2, notice.TemplateKey);
            bodies[0].ShouldContain("3 қазан", Case.Sensitive, $"{notice.TemplateKey}: Kazakh first, in Chatham's day");
            bodies[1].ShouldContain("October 3, 2026", Case.Sensitive, $"{notice.TemplateKey}: English second");
            bodies[1].ShouldNotContain("October 2", Case.Sensitive, "UTC's day, which the zone has already left");
        }
    }

    [Fact]
    public void The_host_bound_the_invented_jurisdiction_and_not_a_default()
    {
        fixture.Factory.Services.GetRequiredService<TemplateRenderer>().Languages
            .ShouldBe(NotificationsWorkerFactory.InventedLanguages);
    }
}
```

The zero-window and missing-language refusals the spec's section 13 puts
under this heading are Task 7's host tests, over the same factory.

- [ ] **Step 3: Run them**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NotificationsEventsTests|FullyQualifiedName~MadeUpDeploymentTests"
```

Expected: green, Docker running. **If
`The_narrow_account_binds_every_event_in_the_consumes_column_to_the_queue`
is red on health, or the binding list is short, the reading above is
wrong**: the broker's log names the refused operation, and the remedy is
the selector in `check_permissions.py` and the mode's grant (spec, section
2), never a widening of this fixture — report it, and stop before Task 10.
A red `The_account_holds_exactly_the_grant_definitions_json_ships` means a
fixture widened the account and the first test measured nothing.

These tests are written first and run once against Tasks 4–8's code rather
than watched red: the behaviour they exercise is already pinned by the
Application suite, and what they add is the broker, the tables and the
grant.

- [ ] **Step 4: Commit**

```bash
git add tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "test(notifications): the seven events bind under notifications-svc's narrow grant and render under ZZ's jurisdiction"
```

The body records the measurement — that the account with no
`Common.Contracts` write binds all seven — with the reading behind it, and
that the fixture's grant is asserted unwidened.

---

### Task 10: The Compose unit's five keys and §15.4

**Files:**
- Modify: `deploy/compose/services/notifications.yml`
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's inventory, sentence and callout

The host now refuses to start without the five `Jurisdiction__*` keys, so
Compose owes them in the PR that makes them required (spec, section 11).
They are configuration, not credentials, so `docs/secrets.md` gains no row
and the secret scan's allow-list no entry.

**Compose's values are invented too, and not the fixture's.** ADR-053 rule
1 says a developer's stack is no jurisdiction and that no member should be
the same in Compose, in the fixture and in production. Compose takes every
shipped language, so a developer reading Mailpit sees all three, in
`en`, `kk`, `ru` order; `Asia/Kathmandu`, a quarter-hour offset no target
deployment uses; and windows of 1001, 13 and 97 days. The contact window
clears `ContactOptions.StaleCeiling`'s day, or the stack would not start.

- [ ] **Step 1: The Compose unit**

`deploy/compose/services/notifications.yml`, in `notifications-worker`'s
`environment:`, after `OTEL_EXPORTER_OTLP_ENDPOINT`'s line:

```yaml
      # Made-up values: a developer's stack is no jurisdiction, and a real one
      # belongs in a values file (ADR-053 rule 1). Every shipped language, so a
      # local message shows all three; a list binds by index (§15.4).
      Jurisdiction__Languages__0: "en"
      Jurisdiction__Languages__1: "kk"
      Jurisdiction__Languages__2: "ru"
      Jurisdiction__TimeZone: "Asia/Kathmandu"
      Jurisdiction__LogRetention: "1001.00:00:00"
      Jurisdiction__ContactRetention: "13.00:00:00"
      Jurisdiction__OrderRetention: "97.00:00:00"
```

- [ ] **Step 2: §15.4's inventory**

`docs/backend-architecture/15-cicd-deployment.md`, the table under §15.4's
"The rule for the Kind column", after the `Jurisdiction__TrackingRetention`
row:

```markdown
| `Jurisdiction__Languages__0…n` | Config | Helm `jurisdiction.languages` → ConfigMap | ✓ — **Notifications only**; ADR-053's language set, in the order a message in every language shows them; the host refuses to start unless every language has every template and a culture |
| `Jurisdiction__TimeZone` | Config | Helm `jurisdiction.timeZone` → ConfigMap | ✓ — **Notifications only**; the IANA zone a customer's dates are rendered in, resolved at start, so a zone the image does not know refuses the host |
| `Jurisdiction__LogRetention` | Config | Helm `jurisdiction.logRetention` → ConfigMap | ✓ — **Notifications only**; ADR-053 rule 4's statutory window for the record of a send, and the host refuses to start without it |
| `Jurisdiction__ContactRetention` | Config | Helm `jurisdiction.contactRetention` → ConfigMap | ✓ — **Notifications only**; ADR-052's contact row's window, refused at start when shorter than `ContactOptions.StaleCeiling` |
| `Jurisdiction__OrderRetention` | Config | Helm `jurisdiction.orderRetention` → ConfigMap | ✓ — **Notifications only**; the order record's window, and the host refuses to start without it |
```

The Helm names are the ones Notifications' chart binds when it lands, as
Shipping's rows name `jurisdiction.addressRetention`.

- [ ] **Step 3: The sentence and the callout**

The sentence after the options sample. Before:

> `Identity:Client` holds a secret that differs per environment, `Jurisdiction`
> holds the statutory windows
> [ADR-053](adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
> makes values a deployment is given, and `Fulfilment` holds the give-up age

After:

> `Identity:Client` holds a secret that differs per environment, `Jurisdiction`
> holds what
> [ADR-053](adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
> makes values a deployment is given — the statutory windows, and for the
> service that writes to customers the language set and the time zone too —
> and `Fulfilment` holds the give-up age

The callout. Before:

> per environment, and `Jurisdiction` earns one because a statutory window is a
> fact about where a deployment runs: a deployment is given its windows
> (ADR-053 rule 1), so a developer's stack, which is no jurisdiction, carries
> invented ones, and a real window belongs in a values file. `Fulfilment` passes

After:

> per environment, and `Jurisdiction` earns one because a statutory window, the
> languages a customer is owed and the zone their dates are read in are facts
> about where a deployment runs: a deployment is given them (ADR-053 rule 1),
> so a developer's stack, which is no jurisdiction, carries invented ones, and
> real ones belong in a values file. `Fulfilment` passes

If PR-2's `Mail` has already joined the sentence, the edit lands on its
text; only the `Jurisdiction` clause moves.

- [ ] **Step 4: Check the documents and the unit**

```bash
docker compose -f deploy/compose/docker-compose.yml config --quiet
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

Then `/check-links` and `/validate-blueprint`: `docs/change-locality.md`
owes the audit after a chapter edit, and a finding is fixed here.

- [ ] **Step 5: Commit**

```bash
git add deploy/compose/services/notifications.yml docs/backend-architecture/15-cicd-deployment.md
git commit -m "docs(15.4): Notifications' five Jurisdiction keys, and Compose's invented values for them"
```

The body says the keys are configuration, why Compose's values differ from
the fixture's, and that the chart's place is the chart's PR.

---

### Task 11: The platform up, everything run, and the PR

- [ ] **Step 1: Bring the platform up and watch one event land**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build --wait
```

A RabbitMQ service on the host holding 5672 or 15672 refuses the broker's
published ports; bring the stack up with a scratchpad-only override that
moves them, never an edit to the committed files. Expected:
`notifications-migrator` exited 0 and `notifications-worker` running; its
log names no options failure. Place an order through the BFF as §14.1's
walkthrough does, then:

```bash
docker compose -f deploy/compose/docker-compose.yml exec sql sh -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT TemplateKey, Status FROM Notifications.notifications.NotificationLog; SELECT OrderId, CancelledAt FROM Notifications.notifications.OrderRecords"'
```

Expected: an `order-placed` row `Pending` and one order record — the first
time a real publisher's event reaches this service — and `rabbitmqctl
list_bindings` in the broker's container showing the seven contract
exchanges bound to `notifications-events`. Then
`docker compose -f deploy/compose/docker-compose.yml down -v`.

- [ ] **Step 2: Build and test everything, then every gate**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 deploy/observability/check.py
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 -m unittest discover -s .github/comment-gate
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: 0 warnings, every suite green, every gate exit 0. The comment
gate judges `HEAD`, so it runs after the last commit; every block added here
is five lines or fewer and names no pull request or test. **This plan's own
text** quotes `Unreachable.Sql` by name and no connection string; if §15.1's
scan names this file, add the entry to `.github/secret-scan/allowed/docs.txt`
with the fingerprint the scanner computed.

- [ ] **Step 3: Open the PR**

The body carries `| Class | A+D+E |` and the touch-set row from Global
Constraints verbatim, with the reasons under the table; the dependency on
PR-3 and why; the broker measurement — the binding test green under the
unwidened grant, with the reading behind it; the satellite-assembly
measurement behind `WithCulture="false"`; the three spec readings this plan
took (the cancellation maps as versioned files, the primary-subtag locale,
the absent mark). Then `/ship`.

## Self-review

**Spec coverage.**

- Section 1, how a notice learns its customer: Tasks 1, 4 and 6 — the order
  record written by Ordering's three, read by nobody yet. What a decline
  tells a customer: the cancellation's origin and reason stored (Tasks 1,
  4); the suppression itself is PR-5's. Whether a cancellation says why:
  Task 5's maps, five codes and `*`. One message per event, seven templates:
  Tasks 4 and 5. `text/plain` and no link: Task 5's files and their
  no-link test. No template engine: Task 5's parts. The languages shipped:
  Task 5. No realm internationalisation, and a locale in the set renders
  alone: Task 5's `Render`.
- Section 3, PR-4's row: `notifications-events` (Task 8), the seven
  consumers writing intent rows (Task 4), `OrderRecords` and its migration
  (Tasks 1, 6), the twenty-one templates (Task 5), the renderer and its
  start-time checks (Tasks 5, 7), `NotificationsJurisdictionOptions` (Task
  7), §15.4's rows (Task 10).
- Section 5, the first row of the table — an event, by its consumer, to
  `Pending`: Task 4.
- Section 6, `OrderRecords`' columns, the versioned `Parameters` with its
  `v` refusal, the options class's five members with stated bounds, the
  zone resolved at start, `ContactRetention` refused below the stale
  ceiling, `AddOrderRecords`: Tasks 1, 3, 6 and 7.
- Section 7, every rule: Task 5; the `-chiseled-extra` dependence held by
  the Kazakh-date test and by the culture refusal at start.
- Section 8, values bounded and control and bidi characters refused at the
  consumer, recorded without the value: Tasks 2 and 4, and over the broker
  in Task 9; no placeholder in a subject: Task 5; no URL: Task 5.
- Section 10, the endpoint (Task 8), the writers and their commutation in
  both orders (Task 4 in all six, Task 9 over the broker in two),
  `MessagingRegistrationTests` (Task 8), the live binding test under the
  narrow account (Task 9).
- Section 11, the five `Jurisdiction__*` keys: Tasks 7 and 10.
- Section 13's rows this PR owns: Tasks 1–9; the `ZZ` fixture renders all
  seven (Task 9) and refuses a zero window and a missing language at start
  (Task 7).
- Section 14, §15.4 in PR-4: Task 10.

**Beyond the spec, each with the reason.** The three cancellation maps
(Task 5: a phrase map in code would make a third language a code change and
leave a row's version unable to reproduce its reason); `WithCulture="false"`
(measured); `InboundValues` checking the currency and the two codes as well
as the tracking number (each is another service's text and each reaches a
body or a column); the absent mark (a dropped value needs some rendering,
and a mark owes no phrase per language); the primary-subtag locale reading;
the `IIntegrationEvent`-free delivery in the fixture and the asserted grant
(both are what make Task 9 a measurement).

**Deliberately left.** ADR-049's suppression, the order-record wait, the
contact read, the render's stamp and the send — PR-5's. The retention
passes over `LogRetention`, `ContactRetention` and `OrderRetention`, with
`OrderRetention`'s pending-row floor — PR-5's. The `InboxWindow` refusal
below `DeliveryOptions.GiveUpAge` — PR-5's, which binds the age. The
chart's `jurisdiction` capability — PR-6's. §11.7's erasure of the order
record — owed with that extension (spec, section 6).

**Type consistency.** `OrderRecord`, `OrderRecordLimits`,
`INotificationRepository`, `IOrderRecordRepository` (Task 1);
`InboundValues` (Task 2); `NotificationParameters`, `ParametersFormat`,
`UnreadableParametersException` (Task 3); `TemplateKeys`,
`PlaceholderNames`, `RecordNotificationCommand`, `OrderFact`,
`OrderCancellation`, `RecordNotificationHandler` and the seven handlers
(Task 4); `TemplateFile`, `Template`, `TemplatePart`, `TemplateSet`,
`TemplateSetException`, `RenderedMessage`, `TemplateRenderer` (Task 5);
`OrderRecordConfiguration`, `NotificationRepository`,
`OrderRecordRepository`, `NotificationsDbContext.OrderRecords` (Task 6);
`NotificationsJurisdictionOptions`, `NotificationsJurisdictionValidator`
and the factory's five parameters and `Invented*` members (Task 7);
`EventsQueue` (Task 8); the fixture's seven helpers and `OrderEvents`
(Task 9). PR-5 consumes `TemplateRenderer.Render`, `RenderedMessage`'s
`TemplateVersion` and `LanguageList`, `ParametersFormat.Read`,
`IOrderRecordRepository` and `OrderRecord`'s cancellation members under
these spellings.

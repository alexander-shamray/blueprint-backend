# Notifications PR-5 — the send worker — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Notifications send. One `BackgroundService`, `SendWorker`,
claims `Pending` rows of `NotificationLog` under a lease, waits for the order
record a row needs, suppresses the decline ADR-049 forbids, reads the contact
by ADR-052's five outcomes, renders in the contact's language or every
language of the deployment's set, commits the intent before the send, sends
under a `Message-ID` built from the row's event id and template key, and
commits the outcome. A row that cannot finish backs off on its own
`Attempts` and `NextAttemptAt`, and every wait ends at
`DeliveryOptions.GiveUpAge` as `Undeliverable: gave_up`. While the relay's
breaker is open the worker claims nothing. Beside it, a retention pass
applies `NotificationsJurisdictionOptions`' three windows, and the
`Notifications.Outbound` meter gains `notifications.mail.resent`,
`notifications.waiting` and `notifications.overdue`.

**Architecture:** `SendWorker` is `FulfilmentWorker`'s shape and
`TrackingWorker`'s concurrency: one statement claims up to
`SendWorker.ClaimBatchSize` rows under `UPDLOCK, READPAST, ROWLOCK` and stamps
`LockedUntil`, then each claimed row runs in a scope of its own, all of them
at once, so a pass lasts as long as its slowest row — one contact read and one
send, which `ContactHop.TotalRequestTimeout` and `MailHop.TotalTimeout` bound,
and which the lease and the host's thirty-second drain are both held above by a
test. Every decision the pass takes is a pure function in
`Notifications.Application.Delivery.SendRules`, driven row by row by the
Application suite, and every move is PR-1's `Notification` method committed
through `IUnitOfWork`; the worker fetches, decides by calling, and commits. A
fault backs the row off in one place — the per-row catch, filtered on the
token — and the outer loop's catch is filtered the same way, so nothing a
dependency does stops the host. The retention pass is a second hosted
service in `ShippingRetentionService`'s shape. The gauges read the tables
through a cached, bounded reader in `ShipmentStats`' shape.

**Tech Stack:** .NET at `global.json`'s pin, EF Core and Dapper over SQL
Server, Polly.Core's circuit-breaker callbacks (PR-2's pin), MassTransit over
RabbitMQ, `System.Diagnostics.Metrics`, xUnit v3 with Shouldly, Testcontainers
(SQL Server, RabbitMQ, Mailpit), an in-process WireMock.Net standing in for
Keycloak's admin API, and `Microsoft.Extensions.TimeProvider.Testing`.

**Spec:** `docs/superpowers/specs/2026-10-02-notifications-service-design.md`,
sections 1 (where the outbound calls sit, how a notification learns its
customer, what a decline tells a customer, the locale's primary subtag), 3
(PR-5's row), 4 (the send worker whole: the claim and its lease, the pass in
its seven steps, at-least-once and the `Message-ID`, the three-way table, the
breaker's park, the give-up age), 5 (the moves the worker makes), 6
(`OrderRetention`'s floor, the three windows applied, the `InboxWindow`
refusal), 9 (the dependency table's three rows), 11 (`Delivery__GiveUpAge`,
a day in Compose), 12 (the three instruments, no mailbox in a log), 13 (the
worker suite's rows and the `ZZ` fixture sending) and 14 (§15.4 in PR-5), read
with
[ADR-049](../../backend-architecture/adr/ADR-049-a-cancellation-payments-has-recorded-declines-the-authorisation-that-follows.md)
and
[ADR-052](../../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
in full.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Notifications/**`, `tests/Notifications.*`, `deploy/compose/services/notifications.yml`, `docs/backend-architecture/15-cicd-deployment.md`

  Why each, since the row is paths only: the service's code and its three
  test projects are A. The Compose unit, which gains `Delivery__GiveUpAge`
  because the host now refuses to start without it, and chapter 15, whose
  §15.4 gains that key's row and whose options-type paragraph and callout
  gain `Delivery`, are D. E is three package references, none with a
  `Version=` and each already pinned in `Directory.Packages.props`:
  `Microsoft.Extensions.Caching.Memory` back on
  `Notifications.Infrastructure.csproj` for the gauges' cache, `WireMock.Net`
  on `Notifications.TestSupport.csproj` for the fixture's stub Keycloak, and
  `Microsoft.Extensions.TimeProvider.Testing` on
  `Notifications.Worker.Tests.csproj` for the breaker's clock. No
  `Directory.Packages.props` line, no Appendix B row, no `Platform.slnx`
  line. `.github/locality-gate/locality_gate.py` admits `A+D+E` as the one
  three-member cell, so the row is spelled exactly that way.
- **`tests/Platform.IntegrationTests` is not in the touch set**, as the spec's
  section 13 says: the order journey is in PR-5's worker suite, "Not in
  `Platform.IntegrationTests`", on §12.1's and §12.6's grounds. Task 11 holds
  it — four of Ordering's and Shipping's events, as their contracts carry
  them, delivered to the real queue and sent to a real relay — in
  `Notifications.Worker.Tests`, where §12.4 gives this service a container
  set.
- **Depends on PR-1 to PR-4 having merged**, in the spec's order: PR-5
  needs 2, 3 and 4. These are their names, consumed as spelled:
  - PR-1: `Notifications.Application.Records.Notification` with
    `AssignCustomer(Guid)`, `Suppress(DateTimeOffset)`,
    `MarkUndeliverable(string, DateTimeOffset)`,
    `StartSend(int, string, DateTimeOffset)` and `MarkSent(DateTimeOffset)`,
    each returning whether it moved the row; `NotificationStatus`;
    `NotificationReasons` with `NoSuchCustomer`, `RecipientRefused`,
    `GaveUp`, `Erased` and `All`; `NotificationLimits`;
    `NotificationsDbContext.NotificationLog`; `NotificationConfiguration`;
    the rendered `EfUnitOfWork`, `SqlConnectionFactory` and
    `IDbConnectionFactory` registration; `RetentionPolicy` registered as
    `new RetentionPolicy()`; `MetricsInitialiser`; `NotificationsWorkerFactory`
    with `ConfigureAuthentication` and the `RetentionPurgeService` removal;
    `ServiceFixture` with `ResetAsync`, `ScalarAsync`, `ExecuteAsync`,
    `ColumnsAsync`, `AppliedMigrationsAsync`; `IntegrationCollection`;
    `DatabaseSmokeTests`; `MetricsRegistrationTests`; `HostSmokeTests`'
    `Ready_probe_reports_the_sql_and_bus_checks`.
  - PR-2: `IMailChannel.SendAsync(OutboundMail, CancellationToken)`;
    `OutboundMail(string Recipient, string Subject, string Body, MailMessageId MessageId, IReadOnlyList<string> Languages)`;
    `MailMessageId(Guid eventId, string templateKey)`;
    `MailResult.Accepted` and `MailResult.Refused(MailRefusal)`;
    `MailRefusal.RecipientRefused` and `NotAMailbox`;
    `MailUnavailableException` with `Cause` (`MailFault.Transient`,
    `Unconfirmed`, `Tls`, `Credential`, `Rejected`) and `SmtpStatus`;
    `MailHop` with `TotalTimeout`, `SendTick` and the breaker's numbers; the
    internal `MailPipeline` with `Pipeline`; `MailMetrics`;
    `OutboundMeter.Name`; `AddMailChannel`; the factory's `mailHost`,
    `mailPort`, `mailSecurity`, `mailUserName`, `mailPassword` and
    `mailFrom` parameters and `LocalFrom`; `Notifications.TestSupport.Mailpit`
    with `Plain()`, `Host`, `Port`, `StartAsync`, `ResetAsync`,
    `RefuseRecipientsAsync`, `MessagesAsync`, `SingleAsync`, `HeadersAsync`
    and the `MailpitSummary` and `MailpitMessage` records;
    `Unreachable.Sql` and `Unreachable.Rabbit`.
  - PR-3: `IContactSource.GetAsync(Guid, CancellationToken)` returning
    `ContactLookup.Found(string Email, string? Locale)` or
    `ContactLookup.NoSuchCustomer`; `ContactSourceRefusedException`;
    `IContactStore` with `SaveAsync`, `GetAsync` and `DeleteAsync`;
    `ContactRecord(string Email, string? Locale, DateTimeOffset FetchedAt)`;
    `ContactOptions` with `Freshness` and `StaleCeiling`, registered as a
    singleton; `ContactHop.TotalRequestTimeout` and `AttemptTimeout`;
    `ContactRecordRowConfiguration`; the factory's `contactSourceBaseUrl`
    parameter, `LocalRealm`, `Tokens` and `ConfigureTokens`;
    `ContactCounter.Refused` and `ContactCount`.
  - PR-4: `OrderRecord` with `CustomerId`, `CancelledAt`, `CancelReason`,
    `CancelOrigin` and `RecordedAt`, `For` and `Cancel`;
    `INotificationRepository` (`ExistsAsync`, `Add`) and
    `IOrderRecordRepository` (`GetAsync`, `Add`) with their
    implementations; `OrderRecordConfiguration`;
    `NotificationsDbContext.OrderRecords`; `TemplateKeys`;
    `NotificationParameters`; `ParametersFormat.Read` and `Write`;
    `UnreadableParametersException`; `TemplateRenderer.Render(string, NotificationParameters, string?)`
    returning `RenderedMessage(Subject, Body, TemplateVersion, Languages)`
    with `LanguageList`; `TemplateSet.Embedded` and `Reasons`;
    `NotificationsJurisdictionOptions` with `LogRetention`,
    `ContactRetention` and `OrderRetention`; the factory's five
    jurisdiction parameters and `InventedLanguages`; `ServiceFixture`'s
    `DeliverAsync`, `NotificationsAsync`, `OrderRecordAsync`,
    `QueueDepthAsync`, `WaitUntilAsync` and `StepDeadline`;
    `Notifications.Infrastructure.Messaging.DependencyInjection.EventsQueue`;
    `OrderEvents`; `MadeUpDeploymentTests`.

  Where an earlier PR spelled one of these differently, the spelling moves
  and nothing else in this plan does.
- **What this plan adds to earlier PRs' types, each argued where it lands:**
  `NotificationReasons.NotAMailbox` (PR-1's closed set has no reason for
  section 4's `Refused(NotAMailbox)` row, Task 1);
  `TemplateRenderer.Render(string, NotificationParameters, int, IReadOnlyList<string>)`
  (a resend must carry the stamped version and languages, Task 2);
  `MailPipeline.IsOpen` (the breaker's park, Task 3);
  `INotificationRepository.GetAsync(Guid, CancellationToken)` (the worker
  commits through the record's moves, Task 7); `Mailpit.PlainOn`,
  `StopAsync`, `WaitForAsync` and `MessageAsync` (Task 6).
- **Nothing in the service may throw into a queue, and nothing here does.**
  The worker is no consumer, so every fault it meets backs a row off and
  reaches no `_error`; a test reads `notifications-events_error` after each
  dependency dies.
- **No log line holds a mailbox or a body** (spec, section 12). The worker's
  lines take the notification's, the order's and the customer's ids;
  `OutboundMail`, `ContactLookup.Found` and `ContactRecord` already print no
  mailbox, and a test searches every captured line for a known one.
- Comments say why and cite the owner — a section, an ADR or a symbol, never a
  pull request, a test or the superpowers spec — a summary is one sentence, a
  `<remarks>` is cited and four lines, a block is five, and a touched block is
  judged whole. Explicit local types, file-scoped namespaces, braces on two
  statements or more and on one that wraps, one space before `=`, `=>` and
  `{`, 120 columns for code and 80 for prose, British spelling.
- `py -3.12`, never `python`. Container tests are in a collection carrying
  `[Trait("Category", "Integration")]` and are never skipped.
- Every step that adds behaviour writes its test first.

**Written against the plans, not run.** PR-1 to PR-4 are plans on this
branch, not merged code, so nothing below was compiled here. Every name is
the earlier plans' spelling or Shipping's built code's, and where a step
quotes an earlier plan's text as an anchor, it is that plan's text.

---

### Task 1: `not_a_mailbox`, and the pass's decisions as pure functions

**Files:**
- Modify: `src/Services/Notifications/Notifications.Application/Records/NotificationReasons.cs`
- Create: `src/Services/Notifications/Notifications.Application/Delivery/ContactAge.cs`
- Create: `src/Services/Notifications/Notifications.Application/Delivery/SendRules.cs`
- Modify: `tests/Notifications.Application.Tests/NotificationTests.cs` — one row of `Moves` and one fact
- Test: `tests/Notifications.Application.Tests/SendRulesTests.cs`

**Interfaces:**
- Consumes: PR-1's `NotificationReasons`; PR-3's `ContactRecord` and
  `ContactOptions`; PR-4's `OrderRecord` and `TemplateKeys`;
  `Common.Contracts.Ordering.V1.CancelReasons` and `CancelOrigins`.
- Produces:

```csharp
namespace Notifications.Application.Records;
public static class NotificationReasons { public const string NotAMailbox = "not_a_mailbox"; /* and PR-1's four */ }

namespace Notifications.Application.Delivery;
public enum ContactAge { Absent, Fresh, Stale, Expired }
public static class SendRules
{
    public static bool HasGivenUp(DateTimeOffset createdAt, DateTimeOffset now, TimeSpan giveUpAge);
    public static bool AwaitsOrderRecord(string templateKey, OrderRecord? order);
    public static bool Suppresses(string templateKey, OrderRecord order);
    public static bool CustomerCancelled(OrderRecord order);
    public static ContactAge AgeOf(ContactRecord? contact, DateTimeOffset now, ContactOptions options);
}
```

**Why `not_a_mailbox` joins the closed set here.** Section 5's table has the
row — `Pending`, "the contact's mailbox is not one a message can be addressed
to", `Undeliverable: not_a_mailbox` — and section 4's table sends PR-2's
`Refused(NotAMailbox)` to it. PR-1's `NotificationReasons.All` holds the four
reasons PR-1 needed and not this one, and `MarkUndeliverable` throws on a
reason outside `All`, so the worker's first CR/LF mailbox would be a row
retried for ever. It is the same move with its own reason, so PR-1's
table-driven test gains a row rather than a test.

**Why the decisions are functions and not the worker's branches.** Section 5
puts the record's rules in `Notifications.Application` as pure functions the
Application suite drives row by row, because §4.1 gives the service no Domain
project and §12.9's coverage filter cannot see the Infrastructure worker. Four
of the pass's seven steps are decisions over rows already read — the give-up
age, the order-record wait, ADR-049's suppression, ADR-052's freshness — so
each is one function here, and the worker holds only the reads, the calls and
the commits.

**ADR-049's reading, written down once.** `OrderCancelled.Origin`'s remark
lets an older publisher omit the origin, and a newer one could send a value
outside `CancelOrigins`' two. Either is read from the reason:
`payment_declined` and `payment_timeout` are the saga's own compensations, so
`workflow`; every other reason, and a reason PR-4's intake dropped, is
`user`. A decline is suppressed exactly when that reading is `user`, and
nothing but a decline is ever suppressed — the cancellation notice goes to the
customer who cancelled, as every other notice does.

**ADR-052's freshness, at its boundaries.** Younger than `Freshness` is
`Fresh` — served with no call. From `Freshness` up to but not reaching
`StaleCeiling` is `Stale` — the owner is asked, and the row is served only
while the owner cannot answer. At or past the ceiling is `Expired` — the
owner is asked and the row is never served. That the ceiling itself is not
served is ADR-052's "younger than the stale ceiling", and PR-4's
`ContactRetention` floor, which lets equal pass, deletes the row at the first
instant it is no use. A row fetched slightly ahead of this host's clock —
another replica's clock — is `Fresh`.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/NotificationTests.cs`: in `Moves`,
after the "the relay refuses the recipient for good" row:

```csharp
        {
            "the contact's mailbox is not one a message can be addressed to",
            n => n.MarkUndeliverable(NotificationReasons.NotAMailbox, Later),
            NotificationStatus.Undeliverable,
            NotificationReasons.NotAMailbox
        },
```

and after `A_reason_outside_the_closed_set_is_the_caller_s_defect`:

```csharp
    [Fact]
    public void The_closed_set_is_every_terminal_reason_the_record_names()
    {
        // ADR-052's outcomes, the relay's two refusals and §11.7's erasure, and nothing a caller could misspell.
        NotificationReasons.All.ShouldBe(
            ["no_such_customer", "recipient_refused", "not_a_mailbox", "gave_up", "erased"],
            ignoreOrder: true);
    }
```

`tests/Notifications.Application.Tests/SendRulesTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Notifications.Application.Contacts;
using Notifications.Application.Delivery;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The send worker's decisions, one row per case (ADR-049, ADR-052).</summary>
public class SendRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static OrderRecord Placed() => OrderRecord.For(Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

    private static OrderRecord Cancelled(string? reason, string? origin)
    {
        OrderRecord record = Placed();
        record.Cancel(reason, origin, Now).ShouldBeTrue();
        return record;
    }

    public static TheoryData<string> EveryKeyButTheDecline() =>
        [.. TemplateKeys.Placeholders.Keys.Where(k => k != TemplateKeys.PaymentDeclined)];

    /// <summary>ADR-049's reading: the origin when it is one of two, otherwise the reason.</summary>
    public static TheoryData<string?, string?, bool> Readings() => new()
    {
        // One of CancelOrigins' two: the origin decides, whatever the reason.
        { CancelReasons.CustomerRequest, CancelOrigins.User, true },
        { CancelReasons.PaymentDeclined, CancelOrigins.User, true },
        { CancelReasons.PaymentDeclined, CancelOrigins.Workflow, false },
        { CancelReasons.OutOfStock, CancelOrigins.Workflow, false },

        // Absent, as an older publisher sends it: the two payment reasons are the saga's, every other the customer's.
        { CancelReasons.PaymentDeclined, null, false },
        { CancelReasons.PaymentTimeout, null, false },
        { CancelReasons.OutOfStock, null, true },
        { CancelReasons.StockTimeout, null, true },
        { CancelReasons.CustomerRequest, null, true },
        { null, null, true },

        // Outside the two, as a newer publisher could send it: read as an absent one is.
        { CancelReasons.PaymentTimeout, "system", false },
        { CancelReasons.CustomerRequest, "system", true },
    };

    /// <summary>A stored contact's age in minutes: ADR-052's fifteen fresh, a day served stale.</summary>
    public static TheoryData<int, ContactAge> Ages() => new()
    {
        { 0, ContactAge.Fresh },
        { 14, ContactAge.Fresh },
        { 15, ContactAge.Stale },
        { 60, ContactAge.Stale },
        { (24 * 60) - 1, ContactAge.Stale },
        { 24 * 60, ContactAge.Expired },
        { 3 * 24 * 60, ContactAge.Expired },
    };

    [Theory]
    [MemberData(nameof(EveryKeyButTheDecline))]
    public void A_notice_waits_for_its_order_record_and_for_nothing_more(string key)
    {
        SendRules.AwaitsOrderRecord(key, null).ShouldBeTrue("§9.4 orders nothing between the events");
        SendRules.AwaitsOrderRecord(key, Placed()).ShouldBeFalse();
    }

    [Fact]
    public void A_decline_waits_for_its_order_record_and_then_for_its_cancellation()
    {
        SendRules.AwaitsOrderRecord(TemplateKeys.PaymentDeclined, null).ShouldBeTrue();
        SendRules.AwaitsOrderRecord(TemplateKeys.PaymentDeclined, Placed())
            .ShouldBeTrue("every decline has a cancellation, published before or after it (§9.6)");
        SendRules.AwaitsOrderRecord(
                TemplateKeys.PaymentDeclined,
                Cancelled(CancelReasons.PaymentDeclined, CancelOrigins.Workflow))
            .ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Readings))]
    public void A_decline_is_suppressed_exactly_when_the_customer_cancelled(
        string? reason,
        string? origin,
        bool customers)
    {
        OrderRecord record = Cancelled(reason, origin);

        SendRules.CustomerCancelled(record).ShouldBe(customers);
        SendRules.Suppresses(TemplateKeys.PaymentDeclined, record).ShouldBe(customers);
    }

    [Theory]
    [MemberData(nameof(EveryKeyButTheDecline))]
    public void Nothing_but_a_decline_is_ever_suppressed(string key)
    {
        SendRules.Suppresses(key, Cancelled(CancelReasons.CustomerRequest, CancelOrigins.User))
            .ShouldBeFalse("the cancellation itself, and every other notice, goes to the customer who cancelled");
    }

    [Fact]
    public void An_order_nobody_cancelled_was_not_cancelled_by_the_customer()
    {
        SendRules.CustomerCancelled(Placed()).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Ages))]
    public void A_stored_contact_s_age_is_read_against_both_numbers(int minutes, ContactAge expected)
    {
        ContactRecord stored = new("aigerim@example.test", "kk", Now.AddMinutes(-minutes));

        SendRules.AgeOf(stored, Now, new ContactOptions()).ShouldBe(expected);
    }

    [Fact]
    public void No_stored_contact_is_absent()
    {
        SendRules.AgeOf(null, Now, new ContactOptions()).ShouldBe(ContactAge.Absent);
    }

    [Fact]
    public void A_contact_fetched_ahead_of_this_host_s_clock_is_fresh()
    {
        // The stored instant is the clock of whichever replica wrote it.
        ContactRecord stored = new("aigerim@example.test", null, Now.AddSeconds(5));

        SendRules.AgeOf(stored, Now, new ContactOptions()).ShouldBe(ContactAge.Fresh);
    }

    [Fact]
    public void The_give_up_age_is_reached_at_its_instant_and_not_before()
    {
        TimeSpan age = TimeSpan.FromDays(1);

        SendRules.HasGivenUp(Now, Now + age - TimeSpan.FromTicks(1), age).ShouldBeFalse();
        SendRules.HasGivenUp(Now, Now + age, age).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~SendRulesTests|FullyQualifiedName~NotificationTests"
```

Expected: compile failure on `NotificationReasons.NotAMailbox` and on
`Notifications.Application.Delivery`.

- [ ] **Step 3: The reason**

`Records/NotificationReasons.cs` becomes:

```csharp
namespace Notifications.Application.Records;

/// <summary>Why a notification is undeliverable, a closed set ADR-052's outcomes and §11.7's erasure name.</summary>
public static class NotificationReasons
{
    public const string NoSuchCustomer = "no_such_customer";
    public const string RecipientRefused = "recipient_refused";

    /// <summary>The contact's mailbox does not parse or carries a line break, so nothing was sent.</summary>
    public const string NotAMailbox = "not_a_mailbox";

    public const string GaveUp = "gave_up";
    public const string Erased = "erased";

    /// <summary>Every reason a row may carry, so a caller's typo is refused rather than stored.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { NoSuchCustomer, RecipientRefused, NotAMailbox, GaveUp, Erased };
}
```

`NotificationLimits.MaxReasonLength` is thirty-two and `not_a_mailbox` is
thirteen, so no column moves.

- [ ] **Step 4: The rules**

`Delivery/ContactAge.cs`:

```csharp
namespace Notifications.Application.Delivery;

/// <summary>A stored contact against <c>ContactOptions</c>, which decides whether the owner is asked.</summary>
public enum ContactAge
{
    /// <summary>No row: the owner is asked, and nothing is served if it cannot answer.</summary>
    Absent,

    /// <summary>Younger than the freshness: served, and the owner is not asked (ADR-052).</summary>
    Fresh,

    /// <summary>Inside the ceiling: the owner is asked, and the row is served only while it cannot answer.</summary>
    Stale,

    /// <summary>At or past the ceiling: the owner is asked, and the row is never served.</summary>
    Expired,
}
```

`Delivery/SendRules.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Common.Contracts.Ordering.V1;
using Notifications.Application.Contacts;
using Notifications.Application.Records;
using Notifications.Application.Rendering;

namespace Notifications.Application.Delivery;

/// <summary>The send worker's decisions, each a pure function over what a pass has read (ADR-049, ADR-052).</summary>
/// <remarks>
/// Here rather than in the worker, as §4.1 gives the service no Domain project: the Application suite drives each
/// row, and the worker only reads, calls and commits.
/// </remarks>
public static class SendRules
{
    /// <summary>Whether a row has waited out the give-up age, measured from its creation (ADR-052).</summary>
    public static bool HasGivenUp(DateTimeOffset createdAt, DateTimeOffset now, TimeSpan giveUpAge) =>
        now - createdAt >= giveUpAge;

    /// <summary>A row waits for Ordering's record, and a decline for its cancellation too (ADR-049).</summary>
    public static bool AwaitsOrderRecord(string templateKey, [NotNullWhen(false)] OrderRecord? order) =>
        order is null || (templateKey == TemplateKeys.PaymentDeclined && order.CancelledAt is null);

    /// <summary>A decline is never sent to a customer who cancelled; nothing else is held back (ADR-049).</summary>
    public static bool Suppresses(string templateKey, OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return templateKey == TemplateKeys.PaymentDeclined && CustomerCancelled(order);
    }

    /// <summary>Who cancelled, by the origin when it is one of two, and otherwise by the reason (ADR-049).</summary>
    public static bool CustomerCancelled(OrderRecord order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.CancelledAt is null)
            return false;

        return order.CancelOrigin switch
        {
            CancelOrigins.User => true,
            CancelOrigins.Workflow => false,

            // An older publisher sends no origin and a newer one may send a third: the payment reasons are the saga's.
            _ => order.CancelReason is not (CancelReasons.PaymentDeclined or CancelReasons.PaymentTimeout)
        };
    }

    /// <summary>A stored contact against ADR-052's two numbers; the ceiling's instant is not served.</summary>
    public static ContactAge AgeOf(ContactRecord? contact, DateTimeOffset now, ContactOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (contact is null)
            return ContactAge.Absent;

        TimeSpan age = now - contact.FetchedAt;

        if (age < options.Freshness)
            return ContactAge.Fresh;

        return age < options.StaleCeiling ? ContactAge.Stale : ContactAge.Expired;
    }
}
```

`PaymentDeclined.Reason` is never an argument here: ADR-049 forbids
branching on it, and the decline's row holds no copy of it — PR-4's
`NotificationParameters` has no member for it. `[NotNullWhen(false)]` tells
the worker's flow analysis that a record it is not waiting for is there,
so it reads the record with no `!`; the attribute is `System.Runtime`'s, which
the architecture suite's allow-list already holds.

- [ ] **Step 5: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
```

Expected: 0 warnings; every test green, `ArchitectureTests` included — the
two files reference `Common.Contracts`, which PR-4 put on the Application
project and the allow-list already holds.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): SendRules decides the wait, ADR-049's suppression and ADR-052's freshness, and not_a_mailbox joins NotificationReasons"
```

The body argues the reading of an absent or unknown origin, the freshness
boundaries, and why `not_a_mailbox` was missing from the closed set.

---

### Task 2: A resend renders what the intent stamped

**Files:**
- Modify: `src/Services/Notifications/Notifications.Application/Rendering/TemplateRenderer.cs`
- Modify: `tests/Notifications.Application.Tests/TemplateRendererTests.cs`

**Interfaces:**
- Consumes: PR-4's `TemplateRenderer`, `RenderedMessage`, `TemplateSet`.
- Produces: `TemplateRenderer.Render(string templateKey, NotificationParameters parameters, int version, IReadOnlyList<string> languages)`.

**Why a second `Render`.** Section 4 resends a row claimed with
`SendStartedAt` set "under the same `Message-ID`", and PR-1's `StartSend`
keeps the first stamp, so the row says it was rendered at one version in one
set of languages. PR-4's `Render(key, parameters, locale)` renders the
*current* version in the *contact's* language — and between the first send
and the resend a deploy can add a version, or the customer can change their
locale. A resend rendered that way carries the same `Message-ID` over
different text, which is the one thing a receiver deduplicating on it cannot
survive, and leaves the row naming a version it did not send (ADR-053 rule
4). So the resend renders exactly the stamped version and languages. A
version is never deleted while a row names it (spec, section 7), so the
files are there; if they are not, the renderer throws as PR-4's does for a
gap, and the row backs off until its give-up age.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/TemplateRendererTests.cs`, after
`Any_other_locale_or_none_is_sent_every_language_of_the_set_in_the_set_s_order`:

```csharp
    [Fact]
    public void A_resend_renders_the_stamped_version_and_languages_exactly_as_the_first_send()
    {
        TemplateRenderer renderer = Renderer(["kk", "en"]);
        RenderedMessage first = renderer.Render(TemplateKeys.OrderPlaced, Everything, locale: null);

        RenderedMessage again = renderer.Render(
            TemplateKeys.OrderPlaced, Everything, first.TemplateVersion, first.Languages);

        again.Subject.ShouldBe(first.Subject);
        again.Body.ShouldBe(first.Body);
        again.TemplateVersion.ShouldBe(first.TemplateVersion);
        again.LanguageList.ShouldBe(first.LanguageList);
    }

    [Fact]
    public void A_resend_keeps_the_stamped_languages_where_a_locale_would_now_choose_one()
    {
        // The customer set a locale after the first send; the same Message-ID must carry the same text.
        RenderedMessage again = Renderer(["kk", "en"]).Render(TemplateKeys.OrderConfirmed, Everything, 1, ["kk", "en"]);

        again.Languages.ShouldBe(["kk", "en"]);
        again.Subject.ShouldContain(TemplateRenderer.SubjectSeparator);
    }

    [Fact]
    public void A_resend_of_a_version_the_set_does_not_hold_throws_rather_than_sending_another()
    {
        Should.Throw<InvalidOperationException>(() =>
            Renderer(["kk", "en"]).Render(TemplateKeys.OrderPlaced, Everything, 2, ["kk"]));
    }

    [Fact]
    public void A_resend_in_a_language_the_deployment_dropped_throws_rather_than_rendering_without_a_culture()
    {
        Should.Throw<InvalidOperationException>(() =>
            Renderer(["kk", "en"]).Render(TemplateKeys.OrderPlaced, Everything, 1, ["ru"]))
            .Message.ShouldContain("ru");
    }
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~TemplateRendererTests"
```

Expected: compile failure — no `Render` overload takes four arguments.

- [ ] **Step 3: The overload**

In `Rendering/TemplateRenderer.cs`, PR-4's `Render` — from its summary to its
closing brace — becomes these three members:

```csharp
    /// <summary>One row's notice, in the locale's language when the set holds it, else in all of them.</summary>
    public RenderedMessage Render(string templateKey, NotificationParameters parameters, string? locale)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return Compose(templateKey, parameters, _templates.CurrentVersion(templateKey), LanguagesFor(locale));
    }

    /// <summary>A resend, at the version and in the languages the row's intent stamped (ADR-053 rule 4).</summary>
    public RenderedMessage Render(
        string templateKey,
        NotificationParameters parameters,
        int version,
        IReadOnlyList<string> languages)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(languages);

        return Compose(templateKey, parameters, version, [.. languages]);
    }

    private RenderedMessage Compose(
        string templateKey,
        NotificationParameters parameters,
        int version,
        string[] languages)
    {
        List<string> subjects = [];
        List<string> bodies = [];

        foreach (string language in languages)
        {
            // A stamp may name a language a later deployment dropped, and only the set's have a culture here.
            if (!_cultures.ContainsKey(language))
                throw new InvalidOperationException($"{language} is not in the deployment's language set.");

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
```

The loop is PR-4's, moved, with one guard before it: `Value` reads the
language's culture from `_cultures`, which holds the deployment's set alone,
so a stamp naming a language a later deployment dropped would otherwise meet
a `KeyNotFoundException` with no word of which language. The comment on the
`Find` was PR-4's and is still true for the first overload; for the second,
the same miss is a version retired too soon, which the same exception
reports.

- [ ] **Step 4: Run the suite**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
```

Expected: 0 warnings; every renderer test green, PR-4's unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): TemplateRenderer renders a resend at the stamped version and languages"
```

The body says why a resend under one `Message-ID` must carry the first
send's text, and that the loop moved unchanged.

---

### Task 3: The breaker says when it is open, so the worker can park its claim

**Files:**
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailPipeline.cs`
- Modify: `tests/Notifications.Worker.Tests/Notifications.Worker.Tests.csproj` — `Microsoft.Extensions.TimeProvider.Testing`
- Test: `tests/Notifications.Worker.Tests/MailPipelineParkTests.cs`

**Interfaces:**
- Consumes: PR-2's `MailPipeline`, `MailHop`, `MailMetrics`,
  `MailUnavailableException`, `MailFault`.
- Produces: `MailPipeline(MailMetrics metrics, TimeProvider clock)`;
  `MailPipeline.IsOpen`; `MailPipeline.ParkedUntil`.

**What "open" has to mean for a worker that stops calling.** Section 4: while
the relay's breaker is open, a pass does not claim at all, so the rows stay
`Pending` with their backoff and nothing reaches `_error`. Polly.Core's
`CircuitBreakerStateProvider.CircuitState` is the breaker's own, up-to-date
state, but in Polly 8 an open circuit moves to half-open only when an
execution arrives after its break — and a worker that parks whenever the state
reads `Open` never makes that execution, so the circuit would stay open for
ever. So `IsOpen` is the state *and* the break: open while the provider says
`Open` or `Isolated` and the break `OnOpened` reported has not passed. Once it
has, `IsOpen` reads false, the next pass claims, and its first send is Polly's
half-open probe; a probe that fails opens a fresh break, and one that succeeds
closes the circuit. The callbacks are Polly's to run, and its documentation
lets them lag the state, so a circuit that has opened before `OnOpened` has
recorded its break reads open — `ParkedUntil` is null until the callback
runs, and `OnHalfOpened` clears it so a re-opening is read the same way.

**The clock is the host's.** The pipeline takes the registered `TimeProvider`
and hands it to Polly's builder, so the break, the sampling window and
`IsOpen` read one clock, and a test can hold all three still. In production
it is `TimeProvider.System`, which Polly uses when given none, so nothing
about PR-2's behaviour moves.

- [ ] **Step 1: The package reference**

`tests/Notifications.Worker.Tests/Notifications.Worker.Tests.csproj`, in the
`PackageReference` group:

```xml
    <!-- FakeTimeProvider, which holds the relay breaker's break still (§9.7). -->
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

Pinned at `9.9.0` in `Directory.Packages.props` already, for
`Common.Application.Tests`, so no pin and no Appendix B row.

- [ ] **Step 2: Write the failing tests**

`tests/Notifications.Worker.Tests/MailPipelineParkTests.cs` — no container,
so no collection and no category:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay breaker's open state, which the send worker reads to park its claim (§9.7).</summary>
public sealed class MailPipelineParkTests : IDisposable
{
    /// <summary>How long a breaker callback may take to record its break; a deadline, not a sleep.</summary>
    private static readonly TimeSpan CallbackDeadline = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));

    private readonly ServiceProvider _services =
        new ServiceCollection().AddMetrics().AddSingleton<MailMetrics>().BuildServiceProvider();

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Faults_short_of_the_throughput_park_nothing()
    {
        MailPipeline pipeline = Pipeline();

        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput - 1);

        pipeline.IsOpen.ShouldBeFalse("a breaker that has not opened parks no claim");
        pipeline.ParkedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task A_breaker_the_relay_opened_parks_until_its_break_has_passed()
    {
        MailPipeline pipeline = Pipeline();

        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput);

        pipeline.IsOpen.ShouldBeTrue();
        DateTimeOffset until = await ParkedAsync(pipeline);
        until.ShouldBe(_clock.GetUtcNow() + MailHop.CircuitBreakerBreakDuration);

        _clock.Advance(MailHop.CircuitBreakerBreakDuration - TimeSpan.FromSeconds(1));
        pipeline.IsOpen.ShouldBeTrue("the break has a second left");

        _clock.Advance(TimeSpan.FromSeconds(1));
        pipeline.IsOpen.ShouldBeFalse("past the break a pass claims again, and its first send is the probe");
    }

    [Fact]
    public async Task A_probe_that_fails_parks_the_claim_for_another_break()
    {
        MailPipeline pipeline = Pipeline();
        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput);
        await ParkedAsync(pipeline);
        _clock.Advance(MailHop.CircuitBreakerBreakDuration);

        await FailAsync(pipeline, 1);

        pipeline.IsOpen.ShouldBeTrue();
        (await ParkedAsync(pipeline)).ShouldBe(_clock.GetUtcNow() + MailHop.CircuitBreakerBreakDuration);
    }

    [Fact]
    public async Task A_probe_that_succeeds_ends_the_park()
    {
        MailPipeline pipeline = Pipeline();
        await FailAsync(pipeline, MailHop.CircuitBreakerMinimumThroughput);
        await ParkedAsync(pipeline);
        _clock.Advance(MailHop.CircuitBreakerBreakDuration);

        await pipeline.Pipeline.ExecuteAsync(_ => ValueTask.FromResult(1), TestContext.Current.CancellationToken);

        pipeline.IsOpen.ShouldBeFalse();
        await WaitAsync(() => pipeline.ParkedUntil is null);
    }

    private MailPipeline Pipeline() => new(_services.GetRequiredService<MailMetrics>(), _clock);

    // Rejected, which the retry leaves alone, so each send is one attempt and no delay waits on the held clock.
    private static async Task FailAsync(MailPipeline pipeline, int sends)
    {
        for (int send = 0; send < sends; send++)
        {
            await Should.ThrowAsync<Exception>(async () => await pipeline.Pipeline.ExecuteAsync<int>(
                _ => throw new MailUnavailableException("Staged.", MailFault.Rejected, 550),
                TestContext.Current.CancellationToken));
        }
    }

    /// <summary>The break the breaker's callback recorded, once it has run.</summary>
    private static async Task<DateTimeOffset> ParkedAsync(MailPipeline pipeline)
    {
        await WaitAsync(() => pipeline.ParkedUntil is not null);

        return pipeline.ParkedUntil!.Value;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CallbackDeadline;

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"The breaker's callback did not run within {CallbackDeadline}.");

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }
}
```

`Should.ThrowAsync<Exception>` rather than `MailUnavailableException`: the
send that opens the circuit throws the staged exception, and Polly may refuse
a later one with `BrokenCircuitException`, which the adapter — not this
pipeline — converts; the subject here is the state, not the exception.

- [ ] **Step 3: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailPipelineParkTests"
```

Expected: compile failure — `MailPipeline` has no two-argument constructor,
no `IsOpen` and no `ParkedUntil`.

- [ ] **Step 4: The pipeline**

`Mail/MailPipeline.cs` becomes:

```csharp
using Notifications.Application.Mail;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Notifications.Infrastructure.Mail;

/// <summary><see cref="MailHop"/>'s budget as one pipeline, a singleton so the breaker's state is the host's.</summary>
/// <remarks>
/// Built by hand because SMTP is no <c>HttpClient</c>, in the standard handler's order: total, retry, breaker,
/// attempt (§9.7). Polly is the engine under that handler too, so the two hops fail alike.
/// </remarks>
internal sealed class MailPipeline
{
    private readonly TimeProvider _clock;
    private readonly CircuitBreakerStateProvider _state = new();

    // UTC ticks until which the breaker's break runs, or zero before a break is recorded.
    private long _parkedUntil;

    public MailPipeline(MailMetrics metrics, TimeProvider clock)
    {
        _clock = clock;

        Pipeline = new ResiliencePipelineBuilder { TimeProvider = clock }
            .AddTimeout(MailHop.TotalTimeout)
            .AddRetry(new RetryStrategyOptions
            {
                // Only an attempt the relay never took the message in: a retry after that could send it twice (§9.7).
                ShouldHandle = new PredicateBuilder()
                    .Handle<MailUnavailableException>(e => e.Cause == MailFault.Transient)
                    .Handle<TimeoutRejectedException>(),
                MaxRetryAttempts = MailHop.MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = MailHop.RetryDelay,
                MaxDelay = MailHop.MaxRetryDelay
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // Every fault, the refusals that are decisions included: a relay refusing all is met once a window.
                ShouldHandle = new PredicateBuilder()
                    .Handle<MailUnavailableException>()
                    .Handle<TimeoutRejectedException>(),
                FailureRatio = MailHop.CircuitBreakerFailureRatio,
                MinimumThroughput = MailHop.CircuitBreakerMinimumThroughput,
                SamplingDuration = MailHop.CircuitBreakerSamplingDuration,
                BreakDuration = MailHop.CircuitBreakerBreakDuration,
                StateProvider = _state,

                // The break's end, which the send worker parks its claim until; the state alone never lapses.
                OnOpened = args =>
                {
                    Interlocked.Exchange(ref _parkedUntil, (clock.GetUtcNow() + args.BreakDuration).UtcTicks);
                    return ValueTask.CompletedTask;
                },
                OnHalfOpened = _ => Unpark(),
                OnClosed = _ => Unpark()
            })
            .AddTimeout(new TimeoutStrategyOptions
            {
                Timeout = MailHop.AttemptTimeout,

                // The one place an attempt timeout before the send is distinguishable from the caller cancelling.
                OnTimeout = _ =>
                {
                    metrics.Unavailable(MailFault.Transient);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    public ResiliencePipeline Pipeline { get; }

    /// <summary>When the open breaker's break ends, or null while none is recorded.</summary>
    public DateTimeOffset? ParkedUntil
    {
        get
        {
            long ticks = Interlocked.Read(ref _parkedUntil);

            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Open until its break passes; then a pass claims, and its first send is the probe (§9.7).</summary>
    public bool IsOpen =>
        (_state.CircuitState is CircuitState.Open or CircuitState.Isolated)
        && (ParkedUntil is not { } until || _clock.GetUtcNow() < until);

    private ValueTask Unpark()
    {
        Interlocked.Exchange(ref _parkedUntil, 0);
        return ValueTask.CompletedTask;
    }
}
```

Every strategy's options are PR-2's text unchanged; what is new is the clock
handed to the builder, the state provider, the three callbacks, and the two
members that read what they record. The registration
in `Mail/DependencyInjection.cs` is `services.AddSingleton<MailPipeline>();`
and stays so: the container resolves the new `TimeProvider` parameter from
`AddNotificationsApplication`'s `TimeProvider.System`, which every host
registers before the hop.

- [ ] **Step 5: Run the suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailPipelineParkTests|FullyQualifiedName~MailHopTests"
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailFaultTests"
```

Expected: 0 warnings; green. `MailFaultTests` — PR-2's, over Mailpit, Docker
running — is the evidence the breaker still opens and still refuses without a
connection once it has: the callbacks added nothing to what it decides.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.Worker.Tests
git commit -m "feat(notifications): MailPipeline reports its breaker open until the break passes, on the host's clock"
```

The body argues why the state provider alone would park the worker for ever,
and that the probe is Polly's half-open call made by the first pass after the
break.

---

### Task 4: `DeliveryOptions`, and the `InboxWindow` it may not outrun

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Delivery/DeliveryOptions.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Delivery/DeliveryOptionsValidator.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs` — the binding
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` — `giveUpAge` and `InventedGiveUpAge`
- Modify: `deploy/compose/services/notifications.yml` — `Delivery__GiveUpAge`
- Test: `tests/Notifications.Worker.Tests/DeliveryOptionsTests.cs`

**Interfaces:**
- Consumes: PR-1's `RetentionPolicy` registration; PR-4's factory
  parameters, after which this one goes.
- Produces:

```csharp
namespace Notifications.Infrastructure.Delivery;

public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public const string MinimumGiveUpAge = "01:00:00";
    public const string MaximumGiveUpAge = "3650.00:00:00";
    public TimeSpan? GiveUpAge { get; init; }
}
internal sealed class DeliveryOptionsValidator(RetentionPolicy retention) : IValidateOptions<DeliveryOptions>;
```

and the factory's `giveUpAge` parameter with `InventedGiveUpAge`.

**`FulfilmentOptions`' form, and why the cross-member rule is a validator.**
One nullable `TimeSpan` under `[Required]` and a `[Range]` read in the
invariant culture, so a missing key and an impossible one fail at start
(§15.4). The floor is `FulfilmentOptions`' hour and for its reason: shorter,
and an outage the backoff rides out — its ceiling is
`OutboxDispatcher.BackoffAttemptCap`'s twenty-one minutes — turns notices
terminal. `FulfilmentOptions` keeps its cross-rule in `IValidatableObject`
because it compares two constants; this one compares the age with
`RetentionPolicy.InboxWindow`, a registered value, so it is a validator that
takes the registered policy, as `NotificationsJurisdictionValidator` takes
`ContactOptions`, and runs the annotations first so one failed start names
everything.

**Why `InboxWindow` must be at least the give-up age.** Spec section 6: the
inbox window is "as long as the longest wait plus an `_error` replay", since
§9.5 makes it a constraint and a replay older than it is a duplicate notice.
The longest a notice waits is `GiveUpAge`, so a window shorter than the age
lets a message replayed within the age miss its inbox row. Equal passes, as
every other floor in the service does. With `RetentionPolicy`'s seven-day
default the age's practical ceiling is a week, and an operator who lengthens
past it meets the refusal, which names both numbers.

**Earns its options type on ADR-052's word**, as `Fulfilment` does in §15.4:
the age is the same everywhere until an outage makes an operator lengthen it,
and ADR-052 makes the end of waiting a value of the deployment so that the
change is a values edit and not a release. Task 12 says so in §15.4.

**Compose's value is a day**, the spec's section 11 default and the chart's
in PR-6: "a notification a day late is worse than none". The fixture's is
invented, as PR-4's windows are, so a test passing under it read its
configuration: two days and seven hours, inside `InboxWindow`'s week.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` gains one
parameter after PR-4's `orderRetention`, defaulted so every caller compiles
unchanged:

```csharp
    string giveUpAge = NotificationsWorkerFactory.InventedGiveUpAge
```

the constant beside PR-4's `Invented*` members:

```csharp
    /// <summary>A give-up age no deployment would choose, inside <c>RetentionPolicy.InboxWindow</c>'s week.</summary>
    public const string InventedGiveUpAge = "2.07:00:00";
```

and in `ConfigureWebHost`, after the jurisdiction's `UseSetting` calls, with
`using Notifications.Infrastructure.Delivery;` in sorted position:

```csharp
            .UseSetting($"{DeliveryOptions.SectionName}:GiveUpAge", giveUpAge)
```

`tests/Notifications.Worker.Tests/DeliveryOptionsTests.cs`:

```csharp
using System.Globalization;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's give-up age is the deployment's, so a missing, impossible or unguarded one fails start.</summary>
public sealed class DeliveryOptionsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("00:00:00")]
    [InlineData("00:59:59")]
    [InlineData("7.00:00:01")]
    public void The_host_refuses_to_start_without_a_usable_give_up_age(string value)
    {
        // The last is past RetentionPolicy's default InboxWindow, so it proves the host checks the registered one.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, giveUpAge: value);

        // The factory builds the host on first use, and a host refusing to start races its disposal.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData("")]
    [InlineData("00:59:59")]
    [InlineData("3651.00:00:00")]
    public void The_failure_names_the_give_up_age(string value)
    {
        using ServiceProvider provider = Bound(value, new RetentionPolicy());

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain(nameof(DeliveryOptions.GiveUpAge));
    }

    [Fact]
    public void An_inbox_window_shorter_than_the_give_up_age_is_refused_and_both_are_named()
    {
        using ServiceProvider provider =
            Bound("1.00:00:00", new RetentionPolicy { InboxWindow = TimeSpan.FromHours(23) });

        string message = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IStartupValidator>().Validate()).Message;

        message.ShouldContain(nameof(RetentionPolicy.InboxWindow));
        message.ShouldContain(nameof(DeliveryOptions.GiveUpAge));
    }

    [Fact]
    public void An_inbox_window_equal_to_the_give_up_age_passes()
    {
        using ServiceProvider provider =
            Bound("1.00:00:00", new RetentionPolicy { InboxWindow = TimeSpan.FromDays(1) });

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_day_a_deployment_is_given_fits_the_registered_inbox_window()
    {
        // Compose's value and the chart's default, against the policy the host registers.
        using ServiceProvider provider = Bound("1.00:00:00", new RetentionPolicy());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void The_invented_age_is_accepted_and_bound()
    {
        // The control for the refusals above, so they cannot pass against a class nothing satisfies.
        using ServiceProvider provider = Bound(NotificationsWorkerFactory.InventedGiveUpAge, new RetentionPolicy());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());

        provider.GetRequiredService<IOptions<DeliveryOptions>>().Value.GiveUpAge
            .ShouldBe(TimeSpan.Parse(NotificationsWorkerFactory.InventedGiveUpAge, CultureInfo.InvariantCulture));
    }

    // The production binding and validator; the host theory above is what still fails if the registration goes.
    private static ServiceProvider Bound(string value, RetentionPolicy retention)
    {
        ServiceCollection services = new();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [$"{DeliveryOptions.SectionName}:GiveUpAge"] = value })
            .Build());
        services.AddSingleton(retention);

        services
            .AddOptions<DeliveryOptions>()
            .BindConfiguration(DeliveryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DeliveryOptions>, DeliveryOptionsValidator>();

        return services.BuildServiceProvider();
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~DeliveryOptionsTests"
```

Expected: compile failure on `Notifications.Infrastructure.Delivery`.

- [ ] **Step 3: The options and the validator**

`Delivery/DeliveryOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Delivery;

/// <summary>ADR-052's give-up age for a notice still pending, past which it is <c>Undeliverable: gave_up</c>.</summary>
/// <remarks>
/// Configuration, as an operator lengthens it during an outage (ADR-052); one age covers every wait at once — the
/// order record, the contact and the relay — since the customer's lateness is the same for all three.
/// </remarks>
public sealed class DeliveryOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Delivery";

    /// <summary>Shorter, and an outage the pass's backoff rides out turns notices terminal.</summary>
    public const string MinimumGiveUpAge = "01:00:00";

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    public const string MaximumGiveUpAge = "3650.00:00:00";

    /// <summary>Measured from the row's <c>CreatedAt</c>; nullable so <c>[Required]</c> sees a missing key.</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumGiveUpAge, MaximumGiveUpAge, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? GiveUpAge { get; init; }
}
```

`Delivery/DeliveryOptionsValidator.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Delivery;

/// <summary>Runs <see cref="DeliveryOptions"/>' annotations, then refuses an age the inbox cannot cover.</summary>
internal sealed class DeliveryOptionsValidator(RetentionPolicy retention) : IValidateOptions<DeliveryOptions>
{
    public ValidateOptionsResult Validate(string? name, DeliveryOptions options)
    {
        List<ValidationResult> annotations = [];
        if (!Validator.TryValidateObject(options, new ValidationContext(options), annotations, true))
            return ValidateOptionsResult.Fail(annotations.Select(a => a.ErrorMessage ?? "Invalid."));

        // §9.5: a message replayed from _error inside the give-up age must still meet its inbox row.
        if (retention.InboxWindow < options.GiveUpAge)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(RetentionPolicy)}.{nameof(RetentionPolicy.InboxWindow)}, {retention.InboxWindow}, is " +
                $"shorter than {DeliveryOptions.SectionName}:{nameof(DeliveryOptions.GiveUpAge)}, " +
                $"{options.GiveUpAge}; a notice can wait longer than its redelivery is remembered.");
        }

        return ValidateOptionsResult.Success;
    }
}
```

- [ ] **Step 4: The binding, and Compose's value**

`DependencyInjection.cs`, after PR-4's renderer registration, with
`using Notifications.Infrastructure.Delivery;` in sorted position:

```csharp
        // ADR-052's give-up age, bound beside its worker (§15.4); a missing, impossible or unguarded one refuses the
        // host at start.
        services
            .AddOptions<DeliveryOptions>()
            .BindConfiguration(DeliveryOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DeliveryOptions>, DeliveryOptionsValidator>();
```

`deploy/compose/services/notifications.yml`, in `notifications-worker`'s
`environment:`, after PR-4's `Jurisdiction__OrderRetention` line:

```yaml
      # ADR-052's give-up age: a notice a day late is worse than none, and an
      # operator lengthens it during an outage rather than a release.
      Delivery__GiveUpAge: "1.00:00:00"
```

It is configuration, not a credential, so `docs/secrets.md` gains no row and
the secret scan's allow-list no entry.

- [ ] **Step 5: Run the suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~DeliveryOptionsTests|FullyQualifiedName~HostSmokeTests|FullyQualifiedName~JurisdictionOptionsTests"
docker compose -f deploy/compose/docker-compose.yml config --quiet
```

Expected: 0 warnings; green, and every host the suites build still starting,
because the factory supplies the invented age; `config` exits 0.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.TestSupport tests/Notifications.Worker.Tests deploy/compose/services/notifications.yml
git commit -m "feat(notifications): DeliveryOptions binds ADR-052's give-up age and refuses one InboxWindow cannot cover"
```

The body argues the floor, the `InboxWindow` rule and why it is a validator
taking the registered policy, and that Compose's day is the value the chart
will default to.

---

### Task 5: The indexes the claim and the purge seek on, and `AddSendAndRetentionIndexes`

**Files:**
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Persistence/NotificationConfiguration.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Persistence/ContactRecordRowConfiguration.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Persistence/OrderRecordConfiguration.cs`
- Create (generated): `Notifications.Infrastructure/Persistence/Migrations/<id>_AddSendAndRetentionIndexes.cs`,
  its designer, and the rewritten `NotificationsDbContextModelSnapshot.cs`
- Modify: `tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs` — the applied list
- Test: `tests/Notifications.Worker.Tests/SendIndexesSchemaTests.cs`

**Interfaces:**
- Consumes: PR-1's, PR-3's and PR-4's configurations and migrations.
- Produces: `IX_NotificationLog_SendClaim` on `NextAttemptAt` filtered
  `[Status] = 'Pending'` including `LockedUntil`;
  `IX_NotificationLog_PendingOrder` on `OrderId` filtered the same;
  `IX_NotificationLog_CompletedAt` on `CompletedAt` filtered
  `[CompletedAt] IS NOT NULL`; `IX_ContactRecords_FetchedAt`;
  `IX_OrderRecords_RecordedAt`.

**Each index arrives with the query that needs it**, which is what PR-3 and
PR-4 said when they left `FetchedAt` and `RecordedAt` unindexed. Five
queries arrive here, and each seeks on one:

| Query | Index |
|---|---|
| the send claim, `SendClaims.Claimable` ordered by `NextAttemptAt` | `IX_NotificationLog_SendClaim`, whose filter the claim repeats so the optimiser matches it, as Shipping's claims do |
| `OrderRetention`'s floor, "no `Pending` row names this order" | `IX_NotificationLog_PendingOrder` |
| `LogRetention`'s purge of terminal rows by the instant they ended | `IX_NotificationLog_CompletedAt` |
| `ContactRetention`'s purge by the instant a row was fetched | `IX_ContactRecords_FetchedAt` |
| `OrderRetention`'s purge by the instant the record was made | `IX_OrderRecords_RecordedAt` |

**One migration for three tables, named for what it adds.** The spec's
section 6 names the three migrations that create the tables; this one creates
none, so it is named for its content, as Shipping's
`SplitShipmentAttemptsAndIndexClaims` is.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Worker.Tests/SendIndexesSchemaTests.cs`:

```csharp
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The indexes the send claim and the retention pass seek on, read from the engine.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SendIndexesSchemaTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Each index's table, name, first key column and filter, as SQL Server normalises a filter.</summary>
    public static TheoryData<string, string, string, string?> Indexes() => new()
    {
        { "NotificationLog", "IX_NotificationLog_SendClaim", "NextAttemptAt", "([Status]='Pending')" },
        { "NotificationLog", "IX_NotificationLog_PendingOrder", "OrderId", "([Status]='Pending')" },
        { "NotificationLog", "IX_NotificationLog_CompletedAt", "CompletedAt", "([CompletedAt] IS NOT NULL)" },
        { "ContactRecords", "IX_ContactRecords_FetchedAt", "FetchedAt", null },
        { "OrderRecords", "IX_OrderRecords_RecordedAt", "RecordedAt", null },
    };

    [Theory]
    [MemberData(nameof(Indexes))]
    public async Task Each_index_keys_its_column_under_its_filter(
        string table,
        string index,
        string column,
        string? filter)
    {
        string qualified = $"notifications.{table}";

        (await fixture.ScalarAsync<string>(
            """
            SELECT Value = c.name
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID({0}) AND i.name = {1}
            """,
            qualified,
            index)).ShouldBe(column);

        (await fixture.ScalarAsync<string?>(
            "SELECT Value = filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID({0}) AND name = {1}",
            qualified,
            index)).ShouldBe(filter);
    }

    [Fact]
    public async Task The_claim_s_index_carries_the_lease_so_the_claim_reads_no_row_it_skips()
    {
        (await fixture.ScalarAsync<int>(
            """
            SELECT Value = COUNT(*)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'IX_NotificationLog_SendClaim' AND ic.is_included_column = 1 AND c.name = 'LockedUntil'
            """)).ShouldBe(1);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SendIndexesSchemaTests"
```

Expected: `Each_index_keys_its_column_under_its_filter` fails on every row —
`SingleAsync` finds no row, since no such index exists.

- [ ] **Step 3: The configurations**

`Persistence/NotificationConfiguration.cs`, after the unique index on
`(EventId, TemplateKey)`:

```csharp
        // The send claim's population, its filter repeated by SendClaims.Claimable so the optimiser matches it.
        builder
            .HasIndex(n => n.NextAttemptAt)
            .HasDatabaseName("IX_NotificationLog_SendClaim")
            .HasFilter("[Status] = 'Pending'")
            .IncludeProperties(n => new { n.LockedUntil });

        // OrderRetention's floor: an order record goes only once no pending notice names its order.
        builder
            .HasIndex(n => n.OrderId)
            .HasDatabaseName("IX_NotificationLog_PendingOrder")
            .HasFilter("[Status] = 'Pending'");

        // LogRetention's purge, over terminal rows by the instant each ended (ADR-053).
        builder
            .HasIndex(n => n.CompletedAt)
            .HasDatabaseName("IX_NotificationLog_CompletedAt")
            .HasFilter("[CompletedAt] IS NOT NULL");
```

`Persistence/ContactRecordRowConfiguration.cs`, after `Locale`'s property:

```csharp
        // ContactRetention's purge, which deletes a row by the instant it was last fetched (ADR-052).
        builder.HasIndex(r => r.FetchedAt);
```

`Persistence/OrderRecordConfiguration.cs`, after the two code columns:

```csharp
        // OrderRetention's purge, by the instant this service first heard of the order.
        builder.HasIndex(r => r.RecordedAt);
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet tool restore
dotnet ef migrations add AddSendAndRetentionIndexes \
    --project src/Services/Notifications/Notifications.Infrastructure \
    --startup-project src/Services/Notifications/Notifications.Migrator \
    --output-dir Persistence/Migrations
```

Open it. It holds exactly five `CreateIndex` and, in `Down`, five
`DropIndex` — no table, no column — and
`git diff -- '*NotificationsDbContextModelSnapshot.cs'` adds the five `HasIndex`
blocks and changes no other line. A snapshot drifted under PR-3 or PR-4 would
show here as a second operation, and is regenerated from `main`'s rather than
hand-edited. Give the hand-authored file the house dress — no byte-order
mark, a file-scoped namespace, no `#nullable disable`, no `/// <inheritdoc />`,
a field for the included columns (CA1861) — leaving the designer and the
snapshot as the tool wrote them. In the tool's order, the file reads:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>The send claim's and the retention pass's indexes, generated from the three configurations.</summary>
public partial class AddSendAndRetentionIndexes : Migration
{
    // A field, for CA1861.
    private static readonly string[] ClaimIncluded = ["LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_OrderRecords_RecordedAt",
            schema: "notifications",
            table: "OrderRecords",
            column: "RecordedAt");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog",
            column: "CompletedAt",
            filter: "[CompletedAt] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_PendingOrder",
            schema: "notifications",
            table: "NotificationLog",
            column: "OrderId",
            filter: "[Status] = 'Pending'");

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog",
            column: "NextAttemptAt",
            filter: "[Status] = 'Pending'")
            .Annotation("SqlServer:Include", ClaimIncluded);

        migrationBuilder.CreateIndex(
            name: "IX_ContactRecords_FetchedAt",
            schema: "notifications",
            table: "ContactRecords",
            column: "FetchedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_OrderRecords_RecordedAt",
            schema: "notifications",
            table: "OrderRecords");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_CompletedAt",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_PendingOrder",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_SendClaim",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropIndex(
            name: "IX_ContactRecords_FetchedAt",
            schema: "notifications",
            table: "ContactRecords");
    }
}
```

If the tool orders them differently, its order stands; what the review checks
is the five operations and nothing else.

`tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs`:
`applied.Length.ShouldBe(8);` becomes `applied.Length.ShouldBe(9);`, and after
the line PR-4 added, `applied[7].ShouldEndWith("_AddOrderRecords");`:

```csharp
        applied[8].ShouldEndWith("_AddSendAndRetentionIndexes");
```

- [ ] **Step 5: Run the suites**

```bash
dotnet build Platform.slnx
dotnet format Platform.slnx --verify-no-changes
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SendIndexesSchemaTests|FullyQualifiedName~DatabaseSmokeTests"
```

Expected: 0 warnings, format exit 0, green. Then, since a migration rewritten
in review is not believed until the engine builds it from empty:

```bash
docker compose -f deploy/compose/docker-compose.yml down -v
```

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.Worker.Tests
git commit -m "feat(notifications): AddSendAndRetentionIndexes indexes the send claim and the three retention windows"
```

The body names the query each index serves, and says why the migration is
named for its content rather than a table.

---

### Task 6: The fixture gains a relay, a stub Keycloak, a log capture and a commit fault

**Files:**
- Modify: `tests/Notifications.TestSupport/Notifications.TestSupport.csproj` — `WireMock.Net`
- Create: `tests/Notifications.TestSupport/CapturedLogs.cs`
- Create: `tests/Notifications.TestSupport/SentCommitFaults.cs`
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`
- Modify: `tests/Notifications.TestSupport/Mailpit.cs`
- Modify: `tests/Notifications.TestSupport/ServiceFixture.cs`
- Test: `tests/Notifications.Worker.Tests/FixtureStubsTests.cs`

**Interfaces:**
- Consumes: PR-2's `Mailpit`; PR-3's `LocalRealm` and `contactSourceBaseUrl`;
  PR-4's fixture helpers; Shipping's `CapturedLogs` and
  `ShipmentCommitFaults`, copied rather than shared (ADR-056).
- Produces: `CapturedLogs`; `SentCommitFaults` and `CommitFault`; the
  factory's `CommitFaults` and `CapturedLogs`; `Mailpit.PlainOn(int)`,
  `StopAsync`, `WaitForAsync(int, CancellationToken)` and
  `MessageAsync(string, CancellationToken)`; `ServiceFixture.Relay`,
  `Keycloak`, `CapturedLogs`, `FailNextSentCommit()`,
  `NewWorkerHost(string?, int?)`, `ResetWithRelayAsync()`,
  `UserPath(Guid)`, `ContactAnswers(Guid, string, string?, TimeSpan?)`,
  `ContactAnswers(Guid, int)`, `ContactCalls(Guid)`,
  `PendingAsync(string, Guid, string?)`, `OrderAsync(Guid, Guid)`,
  `CancelOrderAsync(Guid, string?, string?)`,
  `StageContactAsync(Guid, string, string?, TimeSpan)`,
  `ContactAsync(Guid)`, `NotificationAsync(Guid)`,
  `ClearBackoffAsync(Guid)`, `AgeAsync(Guid, TimeSpan)` and
  `SetAttemptsAsync(Guid, int)`.

**The relay is the image the suite and Compose share, and Keycloak is a
stub.** PR-2 put `Mailpit` in `Notifications.TestSupport` for this fixture to
start. A real Keycloak is PR-3's `KeycloakFixture`, in a collection of its
own — and §12.4's collection is one fixture per class, so a class needing SQL
Server, the broker and the relay cannot also have that container. The worker
asks Keycloak one thing, `GET /admin/realms/{realm}/users/{id}`, and PR-3
proved the adapter's answers against both a real Keycloak and WireMock.Net, so
the worker suites stand Keycloak up as WireMock.Net on loopback, as Shipping's
fixture stands the carrier up. The factory's `ConfigureTokens` already puts
`RecordingTokenCache` in place of the token source, so no identity provider is
needed for the bearer.

**The relay that must stop and come back is one a test starts itself.**
Testcontainers maps a random host port at every start, so the shared relay,
stopped and started, would come back where the host's `Mail:Port` no longer
points. `Mailpit.PlainOn(port)` binds SMTP to a host port the test chose,
which survives the restart; its API stays on a random port and the client is
rebuilt on each start.

**The commit fault qualifies on the one save the crash is about**: a
`Notification` modified to `Sent`. Shipping's fires on any save moving a
shipment; here the intent's commit and the customer's assignment are saves
moving a notification too, and a fault there is a different crash.

- [ ] **Step 1: The package reference**

`tests/Notifications.TestSupport/Notifications.TestSupport.csproj`, in the
`PackageReference` group:

```xml
    <!-- Keycloak's admin API, in process on loopback, as the worker suites meet it (§12.7). -->
    <PackageReference Include="WireMock.Net" />
```

Pinned at `2.12.0` already and on `Notifications.Worker.Tests.csproj` since
PR-3, so no pin.

- [ ] **Step 2: The log capture and the commit fault**

`tests/Notifications.TestSupport/CapturedLogs.cs` — Shipping's, in this
service's namespace:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Notifications.TestSupport;

/// <summary>Every line the host logs, with its state values and exception text, so a search misses none.</summary>
/// <remarks>§13.4 keeps a mailbox out of every log attribute and every exception's text.</remarks>
public sealed class CapturedLogs : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>Every line captured so far, copied at the read.</summary>
    public IReadOnlyCollection<string> Everything => _lines.ToArray();

    /// <summary>Drops what earlier passes logged; the fixture's reset calls it.</summary>
    public void Clear() => _lines.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(_lines);

    public void Dispose()
    {
    }

    private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        // No filtering of its own: what the host's rules let through is what an exporter receives.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Enqueue(formatter(state, exception));

            // The structured half, which an exporter writes as attributes rather than into the message.
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach (KeyValuePair<string, object?> value in values)
                    lines.Enqueue($"{value.Key}={value.Value}");
            }

            if (exception is not null)
                lines.Enqueue(exception.ToString());
        }
    }
}
```

`tests/Notifications.TestSupport/SentCommitFaults.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Notifications.Application.Records;

namespace Notifications.TestSupport;

/// <summary>Fails, once and on demand, the commit marking a notice sent: the crash after the relay's accept.</summary>
public sealed class SentCommitFaults : SaveChangesInterceptor
{
    private CommitFault? _armed;

    /// <summary>Arms the next qualifying save, one fault at a time, so which one fired is never in doubt.</summary>
    public CommitFault Arm()
    {
        CommitFault fault = new(this);
        if (Interlocked.CompareExchange(ref _armed, fault, null) is not null)
            throw new InvalidOperationException("A commit fault is already armed.");

        return fault;
    }

    internal void Disarm(CommitFault fault) => Interlocked.CompareExchange(ref _armed, null, fault);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        // Only the completion qualifies; the intent and the customer's assignment move a notice too.
        bool completing = eventData.Context is not null &&
            eventData.Context.ChangeTracker.Entries<Notification>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == NotificationStatus.Sent);

        if (!completing)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        CommitFault? fault = Interlocked.Exchange(ref _armed, null);
        if (fault is null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        fault.Fired = true;

        // Non-transient, so §6.3's strategy does not retry the unit and the fault reaches the pass's per-row catch.
        throw new DbUpdateException("Injected commit fault.");
    }
}

/// <summary>One armed fault; disposing disarms it, so an unfired fault cannot reach whatever runs next.</summary>
public sealed class CommitFault : IDisposable
{
    private readonly SentCommitFaults _owner;

    internal CommitFault(SentCommitFaults owner) => _owner = owner;

    /// <summary>Whether a save reached the fault and was failed by it.</summary>
    public bool Fired { get; internal set; }

    public void Dispose() => _owner.Disarm(this);
}
```

- [ ] **Step 3: The factory**

`tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`, with
`using Microsoft.AspNetCore.TestHost;`, `using Microsoft.EntityFrameworkCore;`,
`using Microsoft.Extensions.Logging;` and
`using Notifications.Infrastructure.Persistence;` in sorted position where
absent. Beside `Tokens`:

```csharp
    /// <summary>The host's commit fault on a notice marked sent, disarmed until a test arms it.</summary>
    public SentCommitFaults CommitFaults { get; } = new();

    /// <summary>The host's log, captured beside the providers the host configures rather than replacing them.</summary>
    public CapturedLogs CapturedLogs { get; } = new();
```

In `ConfigureWebHost`, after the last `UseSetting` and before
`.ConfigureServices(`:

```csharp
            .ConfigureLogging(logging => logging.AddProvider(CapturedLogs))
```

and after the `ConfigureServices` call's closing `})`, before the statement's
`;`:

```csharp
            .ConfigureTestServices(services =>
                services.ConfigureDbContext<NotificationsDbContext>(o => o.AddInterceptors(CommitFaults)))
```

- [ ] **Step 4: The relay's restart and its reads**

`tests/Notifications.TestSupport/Mailpit.cs`. Beside `Plain()`:

```csharp
    /// <summary>Plain SMTP on a host port the caller chose, which a stop and a start keep (§14.1).</summary>
    public static Mailpit PlainOn(int smtpHostPort) => new(Builder(smtpHostPort).Build());
```

`StartAsync` becomes:

```csharp
    public async Task StartAsync(CancellationToken ct)
    {
        await _container.StartAsync(ct);

        // A restarted container maps its API afresh, so the client is rebuilt on every start.
        _api?.Dispose();
        _api = new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}/api/v1/")
        };
    }

    /// <summary>Stops the relay as an outage would; <see cref="StartAsync"/> brings it back.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _api?.Dispose();
        _api = null;
        await _container.StopAsync(ct);
    }
```

after `SingleAsync`:

```csharp
    /// <summary>The sink's messages once it holds <paramref name="count"/>, waited for; more is a failure.</summary>
    public async Task<IReadOnlyList<MailpitSummary>> WaitForAsync(int count, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ArrivalDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<MailpitSummary> messages = await MessagesAsync(ct);
            if (messages.Count > count)
            {
                throw new InvalidOperationException(
                    $"Mailpit holds {messages.Count} messages where {count} were sent.");
            }

            if (messages.Count == count)
                return messages;

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new TimeoutException($"{count} message(s) did not reach Mailpit within {ArrivalDeadline}.");
    }

    /// <summary>One message, read whole.</summary>
    public async Task<MailpitMessage> MessageAsync(string id, CancellationToken ct) =>
        await Api.GetFromJsonAsync<MailpitMessage>($"message/{Uri.EscapeDataString(id)}", Json, ct)
        ?? throw new InvalidOperationException("Mailpit answered a message read with no body.");
```

and `Builder()` becomes:

```csharp
    private static ContainerBuilder Builder(int? smtpHostPort = null) =>
        (smtpHostPort is { } port
                ? new ContainerBuilder().WithPortBinding(port, SmtpPort)
                : new ContainerBuilder().WithPortBinding(SmtpPort, assignRandomHostPort: true))
            .WithImage(Image)
            .WithPortBinding(ApiPort, assignRandomHostPort: true)
            .WithEnvironment("MP_ENABLE_CHAOS", "true")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)ApiPort).ForPath("/readyz")));
```

`SingleAsync` is unchanged and still refuses a second message; the crash
case reads two, so it uses `WaitForAsync(2, …)`.

- [ ] **Step 5: The fixture**

`tests/Notifications.TestSupport/ServiceFixture.cs`, with
`using System.Text.Json.Nodes;`, `using Notifications.Application.Contacts;`,
`using Notifications.Application.Rendering;`, `using WireMock.RequestBuilders;`,
`using WireMock.ResponseBuilders;`, `using WireMock.Server;`,
`using WireMock.Settings;` and, after the other usings,
`using Response = WireMock.ResponseBuilders.Response;` — MassTransit names a
`Response` too, and the stub's builder is the one this file means. PR-1's
`CreateFactory` becomes `NewWorkerHost()`'s, and the members below join:

```csharp
    /// <summary>The relay, Compose's image, read back through its API (§14.1).</summary>
    public Mailpit Relay { get; } = Mailpit.Plain();

    /// <summary>Keycloak's admin API on loopback, the owner ADR-052's contact read asks.</summary>
    public WireMockServer Keycloak { get; private set; } = null!;

    /// <summary>Every line the host has logged since the last reset.</summary>
    public CapturedLogs CapturedLogs => Factory.CapturedLogs;

    /// <summary>Fails the host's next commit that marks a notice sent, once; disposing the fault disarms it.</summary>
    public CommitFault FailNextSentCommit() => Factory.CommitFaults.Arm();

    /// <summary>A second host over the same database, broker and stubs, with a breaker of its own.</summary>
    public NotificationsWorkerFactory NewWorkerHost(string? relayHost = null, int? relayPort = null) =>
        new(
            ConnectionString,
            BrokerConnectionString,
            mailHost: relayHost ?? Relay.Host,
            mailPort: relayPort ?? Relay.Port,
            contactSourceBaseUrl: Keycloak.Urls[0] + "/");

    protected override NotificationsWorkerFactory CreateFactory() => NewWorkerHost();

    protected override async Task StartStubsAsync()
    {
        Keycloak = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
        await Relay.StartAsync(TestContext.Current.CancellationToken);
    }

    // A mapping a test adds outlives a log reset, and a logged line would reach the next test.
    protected override void ResetStubs()
    {
        Keycloak.Reset();
        CapturedLogs.Clear();
    }

    protected override async ValueTask DisposeStubsAsync()
    {
        try
        {
            Keycloak?.Stop();
        }
        finally
        {
            await Relay.DisposeAsync();
        }
    }

    /// <summary>The schema, the stubs and the relay's messages and Chaos triggers, for a suite that sends.</summary>
    public async Task ResetWithRelayAsync()
    {
        await ResetAsync();
        await Relay.ResetAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The admin API's path for one user of the realm the host reads.</summary>
    public static string UserPath(Guid customer) =>
        $"/admin/realms/{NotificationsWorkerFactory.LocalRealm}/users/{customer:D}";

    /// <summary>Keycloak answering one user, with a name the adapter must never bind.</summary>
    public void ContactAnswers(Guid customer, string email, string? locale = null, TimeSpan? delay = null)
    {
        JsonObject user = new()
        {
            ["id"] = customer.ToString("D"),
            ["username"] = $"customer-{customer:N}",
            ["firstName"] = "Айгерім",
            ["enabled"] = true,
            ["email"] = email
        };

        if (locale is not null)
            user["attributes"] = new JsonObject { ["locale"] = new JsonArray(locale) };

        IResponseBuilder response = Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(user.ToJsonString());

        if (delay is { } stall)
            response = response.WithDelay(stall);

        Keycloak.Given(Request.Create().WithPath(UserPath(customer)).UsingGet()).RespondWith(response);
    }

    /// <summary>Keycloak answering one user with a status and no body: a refusal, an outage, or no such user.</summary>
    public void ContactAnswers(Guid customer, int status) =>
        Keycloak.Given(Request.Create().WithPath(UserPath(customer)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status));

    /// <summary>How many times the host asked for one user.</summary>
    public int ContactCalls(Guid customer) =>
        Keycloak.LogEntries.Count(e => e.RequestMessage!.Path == UserPath(customer));

    /// <summary>A notice owed and due, as a consumer writes one, half a minute old on the host's clock.</summary>
    public async Task<Notification> PendingAsync(string templateKey, Guid order, string? storedParameters = null)
    {
        DateTimeOffset created = DateTimeOffset.UtcNow.AddSeconds(-30);
        string parameters = storedParameters ?? ParametersFormat.Write(
            new NotificationParameters { OrderId = order, OccurredAt = created });

        Notification notification =
            Notification.Pending(Guid.CreateVersion7(), templateKey, order, parameters, created);

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.NotificationLog.Add(notification);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return notification;
    }

    /// <summary>Ordering's record of an order, as its first event writes it.</summary>
    public async Task OrderAsync(Guid order, Guid customer)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        db.OrderRecords.Add(OrderRecord.For(order, customer, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The record's cancellation, as <c>OrderCancelled</c>'s consumer sets it.</summary>
    public async Task CancelOrderAsync(Guid order, string? reason, string? origin)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        OrderRecord record = await db.OrderRecords
            .SingleAsync(r => r.OrderId == order, TestContext.Current.CancellationToken);

        if (!record.Cancel(reason, origin, DateTimeOffset.UtcNow))
            throw new InvalidOperationException($"Order {order} was already cancelled.");

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>A stored contact fetched <paramref name="age"/> ago, through the store the worker reads.</summary>
    public async Task StageContactAsync(Guid customer, string email, string? locale, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IContactStore>().SaveAsync(
            customer,
            new ContactLookup.Found(email, locale),
            DateTimeOffset.UtcNow - age,
            TestContext.Current.CancellationToken);
    }

    /// <summary>The stored contact for one customer, or null.</summary>
    public async Task<ContactRecord?> ContactAsync(Guid customer)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactStore>()
            .GetAsync(customer, TestContext.Current.CancellationToken);
    }

    /// <summary>One notice as the table holds it now, untracked.</summary>
    public async Task<Notification> NotificationAsync(Guid notificationId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        return await db.NotificationLog
            .AsNoTracking()
            .SingleAsync(n => n.NotificationId == notificationId, TestContext.Current.CancellationToken);
    }

    /// <summary>Makes a backed-off notice due now on the engine's clock, which the claim compares against.</summary>
    public Task ClearBackoffAsync(Guid notificationId) =>
        ExecuteAsync(
            "UPDATE notifications.NotificationLog SET NextAttemptAt = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notificationId);

    /// <summary>Moves a notice's creation back by <paramref name="age"/>, which its give-up age is read from.</summary>
    public Task AgeAsync(Guid notificationId, TimeSpan age) =>
        ExecuteAsync(
            "UPDATE notifications.NotificationLog SET CreatedAt = DATEADD(second, -{1}, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notificationId,
            (int)age.TotalSeconds);

    /// <summary>Seeds the failed passes a notice has had, through the column the backoff writes.</summary>
    public Task SetAttemptsAsync(Guid notificationId, int attempts) =>
        ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Attempts = {1} WHERE NotificationId = {0};",
            notificationId,
            attempts);
```

`PendingAsync`'s half minute keeps a row due whichever of the host's and the
engine's clocks is ahead; the give-up age is days, so it reads nothing of the
half minute. `ExecuteAsync`'s `{1}` inside `DATEADD` is a parameter, which
`DATEADD` takes as its number.

- [ ] **Step 6: Write the control tests**

`tests/Notifications.Worker.Tests/FixtureStubsTests.cs` — what every later
suite leans on, read through the host's own adapters, so a stub that drifted
from what the host asks fails here and not as a puzzle in a worker test:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Mail;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The fixture's stand-ins, read through the host's own adapters, so a later stub is a real one.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class FixtureStubsTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_stub_keycloak_answers_the_host_s_own_adapter_as_the_admin_api_does()
    {
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, "aigerim@example.test", "kk-KZ");

        (await ReadAsync(customer)).ShouldBe(new ContactLookup.Found("aigerim@example.test", "kk-KZ"));
        fixture.ContactCalls(customer).ShouldBe(1);
    }

    [Fact]
    public async Task A_user_the_stub_does_not_know_is_no_such_customer()
    {
        // WireMock.Net answers an unmapped path 404, which is the admin API's answer for no such user.
        (await ReadAsync(Guid.CreateVersion7())).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task The_host_s_relay_is_the_fixture_s()
    {
        MailResult result = await fixture.Factory.Services.GetRequiredService<IMailChannel>().SendAsync(
            new OutboundMail(
                "aigerim@example.test",
                "Your order is placed",
                "Order 42 is placed.",
                new MailMessageId(Guid.CreateVersion7(), TemplateKeys.OrderPlaced),
                ["en"]),
            Ct);

        result.ShouldBe(new MailResult.Accepted());
        (await fixture.Relay.WaitForAsync(1, Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_staged_notice_is_pending_due_and_unleased()
    {
        Notification staged = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());

        (await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
            "AND Status = 'Pending' AND NextAttemptAt <= SYSDATETIMEOFFSET() AND LockedUntil IS NULL",
            staged.NotificationId)).ShouldBe(1);
    }

    private async Task<ContactLookup> ReadAsync(Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>().GetAsync(customer, Ct);
    }
}
```

- [ ] **Step 7: Run them, and every suite over the fixture**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests
```

Expected: 0 warnings; green, Docker running. Every PR-1 to PR-4 container
suite still passes over the fixture's new host, which differs from the old
only in naming a reachable relay and a reachable contact source; the relay
container adds a few seconds to the collection's start.

- [ ] **Step 8: Commit**

```bash
git add tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "test(notifications): the fixture starts the relay and a stub Keycloak, captures the log and arms a sent-commit fault"
```

The body says why Keycloak is a stub here and a container in PR-3's suite,
why the restartable relay binds a fixed port, and why the fault qualifies on
`Sent` alone.

---

### Task 7: The send worker

**Files:**
- Modify: `src/Services/Notifications/Notifications.Application/Records/INotificationRepository.cs` — `GetAsync`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Persistence/NotificationRepository.cs` — `GetAsync`
- Modify: `tests/Notifications.Application.Tests/NotificationIntakeTests.cs` — `FakeNotifications.GetAsync`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Delivery/SendWork.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Delivery/SendPass.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Delivery/SendClaims.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Delivery/SendWorker.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Observability/NotificationMetrics.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Observability/MetricsInitialiser.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs` — the worker, its claims, the metrics
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` — the hosted worker's removal
- Modify: `tests/Notifications.TestSupport/ServiceFixture.cs` — `RunSendPassAsync`, `WaitUntilDueAsync`, `SendUntilSettledAsync`
- Modify: `tests/Notifications.Worker.Tests/MetricsRegistrationTests.cs` — the selector names `NotificationMetrics`
- Create: `tests/Notifications.Worker.Tests/ResentCounter.cs`
- Test: `tests/Notifications.Worker.Tests/SendWorkerBudgetTests.cs`
- Test: `tests/Notifications.Worker.Tests/SendWorkerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–6; every name *Global Constraints* lists.
- Produces:

```csharp
namespace Notifications.Application.Records;
public interface INotificationRepository { Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct); }

namespace Notifications.Infrastructure.Delivery;
public sealed record SendWork(Guid NotificationId, Guid EventId, Guid OrderId, Guid? CustomerId, string TemplateKey,
    string Parameters, DateTimeOffset CreatedAt, DateTimeOffset? SendStartedAt, int? TemplateVersion,
    string? Languages);
public readonly record struct SendPass(int Claimed, int Finished);
internal sealed class SendClaims   // Claimable, ClaimAsync, BackOffAsync
public sealed class SendWorker : BackgroundService
{
    public const int ClaimBatchSize = 10;
    public const int LeaseSeconds = 45;
    public static readonly TimeSpan DrainBudget;  // twenty-five seconds
    public static readonly TimeSpan CommitRoom;   // five seconds
    public Task<SendPass> RunOnceAsync(CancellationToken ct);
}

namespace Notifications.Infrastructure.Observability;
public sealed class NotificationMetrics { public void Resent(); }   // notifications.mail.resent
```

and `ServiceFixture.RunSendPassAsync()`, `WaitUntilDueAsync(Guid)`,
`SendUntilSettledAsync(int)`, `ResentCounter.Resent(IServiceProvider)`.

**The pass, step by step, and what each step does with a row.** Each is
section 4's, and each is driven by a test below.

| Step | Read | Decides | The row |
|---|---|---|---|
| give-up | `CreatedAt`, `DeliveryOptions.GiveUpAge` | `SendRules.HasGivenUp` | `Undeliverable: gave_up`; no call is made |
| order record | `IOrderRecordRepository.GetAsync` | `SendRules.AwaitsOrderRecord` | backs off and waits; no call is made |
| suppression | the record's cancellation | `SendRules.Suppresses` | `Suppressed`, the customer named; no call is made |
| customer | the record's `CustomerId` | — | `AssignCustomer`, committed once |
| contact | `IContactStore`, then `IContactSource` | `SendRules.AgeOf`, ADR-052's table | fresh: served; answered: stored and served; owner down: a stale row served, otherwise backs off; refused: backs off, never served stale; no such customer: `Undeliverable: no_such_customer`, the contact row deleted |
| render | `ParametersFormat.Read`, `TemplateRenderer` | the locale's primary subtag | an unreadable payload backs off |
| intent | — | — | `StartSend` with the version and languages, committed before the send; a row already stamped is a resend, rendered at its stamp and counted |
| send | `IMailChannel.SendAsync` | the answer | `Accepted`: `MarkSent`; `RecipientRefused`: `Undeliverable: recipient_refused`; `NotAMailbox`: `Undeliverable: not_a_mailbox`; a fault: backs off, a warning for `transient` and `unconfirmed`, an error for `tls`, `credential` and `rejected` |

**Everything that ends a row's turn without an outcome goes through one
backoff.** A wait calls `SendClaims.BackOffAsync` itself; a fault throws to
the per-row catch, which logs by type and backs off. The backoff is
`FulfilmentClaims`' — the dispatcher's ladder read from
`OutboxDispatcher.BackoffAttemptCap` and `BackoffBaseSeconds`, so the two
cannot drift, on the row's own `Attempts`, with the lease dropped. `check.py`'s
check 8 matches a service that *registers* `AddHostedService<OutboxDispatcher>`,
so naming its constants is not hosting it, as Shipping's worker already
shows.

**`UnreadableParametersException` backs the row off, and that is the
decision.** A payload whose `v` this code does not know was written by a
newer version, and the one way a running worker meets one is a rolling deploy
or a rollback, where a replica of that newer version will claim the row on a
later pass. A terminal outcome would throw away a notice the next replica
could send, and section 5's closed set has no reason for one; a fault would
stop nothing, as no queue is involved. So it backs off, logged as an error
that names the row and never the payload, and `DeliveryOptions.GiveUpAge`
ends it if no replica that can read it ever arrives. The waiting gauge reads
such a row under `contact` — it cannot tell a render's wait from a contact's
— and the error line is the signal for it.

**A resend renders the stamp and is counted.** A row claimed with
`SendStartedAt` set is a send that may have reached the relay: an
`unconfirmed` fault, a crash after the relay's accept, a lapsed lease. It is
rendered with Task 2's overload at the stamped version and languages, sent
under the same `Message-ID`, and counted on `notifications.mail.resent`. A
`transient` fault after the intent is counted as a resend too, though the
relay held nothing: the counter is the record that a duplicate *may* have
happened, which is section 4's posture — the duplicate is on the row and on a
counter, never invisible — and telling the two apart would need a column
section 6 does not have.

**The moves commit through the record's methods.** `CommitAsync` loads the row
tracked through `INotificationRepository.GetAsync`, applies one of PR-1's
moves, and saves inside `IUnitOfWork.ExecuteAsync`, as `FulfilmentWorker`
commits a shipment. A move that returns `false` changed nothing and is
reported as such: `StartSend` refusing means another pass stamped the row or
it left `Pending`, and the pass stops there rather than send. The lease is not
released by a commit, unlike Shipping's: a terminal row leaves the claim's
population by its status, and the intent's commit must keep the lease, since
the send follows it.

- [ ] **Step 1: Write the failing budget tests**

`tests/Notifications.Worker.Tests/SendWorkerBudgetTests.cs` — no container,
so no collection and no category:

```csharp
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The send pass's arithmetic: the lease and the drain against the two hops' totals (§9.7, §15.3).</summary>
public sealed class SendWorkerBudgetTests
{
    /// <summary>A row's two calls, made in turn; a pass makes every row's at once, so this bounds a pass too.</summary>
    private static readonly TimeSpan OneRow = MailHop.TotalTimeout + ContactHop.TotalRequestTimeout;

    [Fact]
    public void The_lease_outlives_both_calls_a_row_makes()
    {
        TimeSpan.FromSeconds(SendWorker.LeaseSeconds).ShouldBeGreaterThan(
            OneRow, "a lease shorter than a row's calls lets a second replica claim a row still being sent");
    }

    [Fact]
    public void The_drain_budget_and_its_last_commit_fit_the_host_s_drain()
    {
        // The default the solution never overrides, measured rather than written down (§15.3).
        (SendWorker.DrainBudget + SendWorker.CommitRoom).ShouldBeLessThanOrEqualTo(
            new HostOptions().ShutdownTimeout, "the host abandons a pass still committing when its drain runs out");
    }

    [Fact]
    public void A_pass_fits_the_drain_budget()
    {
        OneRow.ShouldBeLessThanOrEqualTo(
            SendWorker.DrainBudget, "a stop cancels a pass whose calls outrun the drain budget, mid-send");
    }

    [Fact]
    public void A_pass_has_rows_to_run_at_once()
    {
        SendWorker.ClaimBatchSize.ShouldBeGreaterThan(1, "one row a tick is twelve notices a minute a replica");
    }
}
```

- [ ] **Step 2: Write the failing worker tests**

`tests/Notifications.Worker.Tests/ResentCounter.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Infrastructure.Observability;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>One host's <c>notifications.mail.resent</c>, counted by <see cref="ContactCount"/>.</summary>
internal static class ResentCounter
{
    public static ContactCount Resent(IServiceProvider services)
    {
        // The counter is created in NotificationMetrics' constructor; a listener started first would see nothing.
        services.GetRequiredService<NotificationMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(OutboundMeter.Name);
        ContactCount count = new(mine, "notifications.mail.resent");
        count.Enabled.ShouldBeTrue("no resend counter on this host's meter was enabled, so a zero would prove nothing");

        return count;
    }
}
```

PR-3's `ContactCount(Meter, string instrument)` takes the instrument's name,
so it counts this counter as well as its own; a second listener class would
be a copy of it.

`tests/Notifications.Worker.Tests/SendWorkerTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The send pass's steps over the real tables, a real relay and a stub Keycloak (ADR-049, ADR-052).</summary>
/// <remarks>One shared host, as no case here fills a breaker: a refusal and a 404 are answers to it (§9.7).</remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class SendWorkerTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan GiveUpAge = TimeSpan.Parse(
        NotificationsWorkerFactory.InventedGiveUpAge, System.Globalization.CultureInfo.InvariantCulture);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_placed_order_is_sent_in_the_contact_s_language_under_its_event_s_message_id()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en-GB");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification sent = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.CustomerId.ShouldBe(customer, "copied from the order record (§11.7's erasure replaces it)");
        sent.TemplateVersion.ShouldBe(1);
        sent.Languages.ShouldBe("en", "en-GB's primary subtag is in the invented set, so it alone is sent");
        sent.SendStartedAt.ShouldNotBeNull("the intent was committed before the send");
        sent.CompletedAt.ShouldNotBeNull();

        MailpitMessage message = await fixture.Relay.SingleAsync(Ct);
        message.MessageId.Trim('<', '>').ShouldBe($"{sent.EventId:N}.{TemplateKeys.OrderPlaced}@commerce.test");
        message.To.ShouldHaveSingleItem().Address.ShouldBe(Mailbox);
        (await fixture.ContactAsync(customer))!.Email.ShouldBe(Mailbox, "the owner's answer is kept (ADR-052)");
    }

    [Fact]
    public async Task A_customer_with_no_locale_is_sent_every_language_of_the_set_in_its_order()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox);
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);

        await fixture.RunSendPassAsync();

        (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem().Languages.ShouldBe("kk,en");
        MailpitMessage message = await fixture.Relay.SingleAsync(Ct);
        message.Subject.ShouldContain(TemplateRenderer.SubjectSeparator);
        (await fixture.Relay.HeadersAsync(message.Id, Ct))["Content-Language"]
            .ShouldHaveSingleItem().ShouldBe("kk, en");
    }

    [Fact]
    public async Task A_notice_that_arrives_before_its_order_waits_then_sends_once_the_record_exists()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At));
        await fixture.WaitUntilDueAsync(order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        Notification waiting = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1, "a wait backs off on the row's own count");
        waiting.CustomerId.ShouldBeNull();
        fixture.ContactCalls(customer).ShouldBe(0, "nothing is asked of the owner before the customer is known");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.SendUntilSettledAsync();

        IReadOnlyList<Notification> rows = await fixture.NotificationsAsync(order);
        rows.ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.CustomerId == customer);
        (await fixture.Relay.WaitForAsync(2, Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_decline_before_its_cancellation_waits_and_the_customer_s_cancellation_suppresses_it()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));
        await fixture.WaitUntilDueAsync(order);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(2, 1), "the placement sends and the decline waits");
        Notification decline = await DeclineAsync(order);
        decline.Status.ShouldBe(NotificationStatus.Pending);
        decline.Attempts.ShouldBe(1);

        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.CustomerRequest, CancelOrigins.User));
        await fixture.SendUntilSettledAsync();

        decline = await DeclineAsync(order);
        decline.Status.ShouldBe(NotificationStatus.Suppressed, "ADR-049: a customer who cancelled is never told");
        decline.CustomerId.ShouldBe(customer);
        decline.SendStartedAt.ShouldBeNull("nothing was rendered or sent");

        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(2, Ct);
        delivered.ShouldNotContain(m => m.MessageId.Contains(TemplateKeys.PaymentDeclined, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_decline_the_saga_cancelled_is_sent_beside_its_cancellation()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, At, CancelReasons.PaymentDeclined, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));

        await fixture.SendUntilSettledAsync();

        (await fixture.NotificationsAsync(order)).ShouldAllBe(n => n.Status == NotificationStatus.Sent);
        (await fixture.Relay.WaitForAsync(3, Ct))
            .ShouldContain(m => m.MessageId.Contains(TemplateKeys.PaymentDeclined, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CancelReasons.PaymentTimeout, null, NotificationStatus.Sent)]
    [InlineData(CancelReasons.CustomerRequest, null, NotificationStatus.Suppressed)]
    [InlineData(CancelReasons.PaymentDeclined, "system", NotificationStatus.Sent)]
    [InlineData(CancelReasons.OutOfStock, "system", NotificationStatus.Suppressed)]
    public async Task A_cancellation_with_no_known_origin_is_read_from_its_reason(
        string reason,
        string? origin,
        NotificationStatus decline)
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Cancelled(order, customer, At, reason, origin));
        await fixture.DeliverAsync(OrderEvents.Declined(order, At));

        await fixture.SendUntilSettledAsync();

        (await DeclineAsync(order)).Status.ShouldBe(decline);
    }

    [Fact]
    public async Task A_fresh_contact_is_served_without_asking_the_owner()
    {
        // No stub mapping: an owner asked would answer 404, and the notice would end as no such customer.
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromMinutes(1));
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
        fixture.ContactCalls(customer).ShouldBe(0);
    }

    [Fact]
    public async Task A_stale_contact_is_refreshed_from_an_owner_that_answers()
    {
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, "old@example.test", "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, Mailbox, "en");
        await OwedAsync(order, customer);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await fixture.RunSendPassAsync();

        (await fixture.Relay.SingleAsync(Ct)).To.ShouldHaveSingleItem().Address.ShouldBe(Mailbox);
        ContactRecord refreshed = (await fixture.ContactAsync(customer)).ShouldNotBeNull();
        refreshed.Email.ShouldBe(Mailbox);
        refreshed.FetchedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
    }

    [Fact]
    public async Task A_customer_the_owner_does_not_know_is_undeliverable_and_their_contact_row_goes()
    {
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 404);
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.NoSuchCustomer);
        (await fixture.ContactAsync(customer)).ShouldBeNull("ADR-052's fifth row deletes the contact row");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
        (await fixture.RunSendPassAsync()).Claimed.ShouldBe(0, "a terminal row is not retried");
    }

    [Fact]
    public async Task A_recipient_the_relay_refuses_for_good_is_undeliverable_and_not_retried()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.Relay.RefuseRecipientsAsync(550, Ct);
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.RecipientRefused);
        (await fixture.RunSendPassAsync()).Claimed.ShouldBe(0);
    }

    [Fact]
    public async Task A_mailbox_carrying_a_line_break_is_not_a_mailbox_and_nothing_is_sent()
    {
        // The owner answers it as stored, as ADR-052 has no outcome for a malformed one; the channel judges a mailbox.
        const string broken = "aigerim@example.test\r\nBcc: someone@example.test";
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, broken, "en");
        Notification owed = await OwedAsync(order, customer);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.NotAMailbox);
        (await fixture.ContactAsync(customer))!.Email.ShouldBe(broken, "stored as the owner gave it");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notice_pending_past_its_give_up_age_is_given_up_without_asking_anyone()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        await fixture.AgeAsync(owed.NotificationId, GiveUpAge + TimeSpan.FromMinutes(1));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        Notification ended = await fixture.NotificationAsync(owed.NotificationId);
        ended.Status.ShouldBe(NotificationStatus.Undeliverable);
        ended.Reason.ShouldBe(NotificationReasons.GaveUp);
        fixture.ContactCalls(customer).ShouldBe(0, "the age is asked before anyone is");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
        (await fixture.RunSendPassAsync()).Claimed.ShouldBe(0, "a give-up is terminal and not retried");
    }

    [Fact]
    public async Task A_notice_inside_its_give_up_age_still_waits()
    {
        Guid order = Guid.CreateVersion7();
        Notification owed = await fixture.PendingAsync(TemplateKeys.ShipmentDelivered, order);
        await fixture.AgeAsync(owed.NotificationId, GiveUpAge - TimeSpan.FromHours(1));

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Parameters_this_version_cannot_read_back_the_row_off_and_fault_nothing()
    {
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromMinutes(1));
        await fixture.OrderAsync(order, customer);
        Notification owed = await fixture.PendingAsync(
            TemplateKeys.OrderConfirmed,
            order,
            storedParameters: $$"""{"v":2,"orderId":"{{order:D}}","occurredAt":"2026-10-02T09:00:00+00:00"}""");

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending, "a newer version's replica can still send it");
        waiting.Attempts.ShouldBe(1);
        waiting.SendStartedAt.ShouldBeNull();
        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("cannot read", StringComparison.Ordinal) &&
                line.Contains(owed.NotificationId.ToString(), StringComparison.Ordinal));
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lapsed_lease_is_taken_by_another_pass()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET LockedUntil = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            owed.NotificationId);

        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1), "a lease in the past is no lease");
    }

    [Fact]
    public async Task Two_workers_overlapping_claim_one_row_once()
    {
        // The owner stalls short of ContactHop's attempt timeout, so the first pass holds the lease meanwhile.
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(800));
        Notification owed = await OwedAsync(order, customer);
        using NotificationsWorkerFactory second = fixture.NewWorkerHost();
        // Resolved first, as the host starts on first use and must not spend the stall starting.
        SendWorker other = second.Services.GetRequiredService<SendWorker>();

        Task<SendPass> first = fixture.RunSendPassAsync();
        await ServiceFixture.WaitUntilAsync(async () => await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
            "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
            owed.NotificationId) == 1);

        (await other.RunOnceAsync(Ct)).ShouldBe(new SendPass(0, 0), "the second worker skipped a leased row");
        (await first).ShouldBe(new SendPass(1, 1));

        (await fixture.NotificationAsync(owed.NotificationId)).Attempts.ShouldBe(0);
        (await fixture.Relay.SingleAsync(Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_crash_between_the_relay_s_accept_and_the_commit_sends_twice_under_one_message_id_and_counts_it()
    {
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en");
        Notification owed = await OwedAsync(order, customer);
        using ContactCount resent = ResentCounter.Resent(fixture.Factory.Services);

        using (CommitFault fault = fixture.FailNextSentCommit())
        {
            // The relay accepted; the commit failed, which the per-row catch backs off rather than throws.
            (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 0));
            fault.Fired.ShouldBeTrue();
        }

        Notification crashed = await fixture.NotificationAsync(owed.NotificationId);
        crashed.Status.ShouldBe(NotificationStatus.Pending);
        crashed.SendStartedAt.ShouldNotBeNull("the intent precedes the send, so the row says it may be out");

        await fixture.ClearBackoffAsync(owed.NotificationId);
        (await fixture.RunSendPassAsync()).ShouldBe(new SendPass(1, 1));

        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(2, Ct);
        delivered.Select(m => m.MessageId.Trim('<', '>')).Distinct().ShouldHaveSingleItem()
            .ShouldBe($"{owed.EventId:N}.{TemplateKeys.OrderConfirmed}@commerce.test");
        delivered.Select(m => m.Subject).Distinct().ShouldHaveSingleItem("a resend renders the stamped text");

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent);
        sent.SendStartedAt.ShouldBe(crashed.SendStartedAt, "a second start keeps the first stamp");
        resent.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_pass_that_throws_leaves_the_host_running()
    {
        // The claim failing, not a row, as the pass catches per row; an unreachable database makes the pass throw.
        using NotificationsWorkerFactory broken = new(Unreachable.Sql, Unreachable.Rabbit);
        SendWorker worker = broken.Services.GetRequiredService<SendWorker>();

        await Should.ThrowAsync<Exception>(() => worker.RunOnceAsync(Ct));

        await worker.StartAsync(Ct);

        // Staged on the loop's own line, which the direct call above never logs.
        await ServiceFixture.WaitUntilAsync(() =>
            Task.FromResult(ClaimFailedLogged(broken) || worker.ExecuteTask!.IsCompleted));

        // ExecuteTask is the loop, and a faulted one is the host on its way down.
        worker.ExecuteTask!.IsFaulted.ShouldBeFalse();
        ClaimFailedLogged(broken).ShouldBeTrue();

        await worker.StopAsync(Ct);
    }

    [Fact]
    public async Task A_host_stopped_mid_pass_finishes_the_send_and_commits_it_once()
    {
        // The owner stalls, so the stop lands inside the pass, ahead of the send and the commit the drain lets run.
        (Guid order, Guid customer) = Ids();
        fixture.ContactAnswers(customer, Mailbox, "en", delay: TimeSpan.FromMilliseconds(800));
        Notification owed = await OwedAsync(order, customer);
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        using ContactCount resent = ResentCounter.Resent(host.Services);
        SendWorker worker = host.Services.GetRequiredService<SendWorker>();

        await worker.StartAsync(Ct);
        await ServiceFixture.WaitUntilAsync(async () => await fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE NotificationId = {0} " +
            "AND Status = 'Pending' AND LockedUntil > SYSDATETIMEOFFSET()",
            owed.NotificationId) == 1);
        await worker.StopAsync(Ct);

        Notification sent = await fixture.NotificationAsync(owed.NotificationId);
        sent.Status.ShouldBe(NotificationStatus.Sent, "a stop drains the pass under way rather than cancel it");
        sent.Attempts.ShouldBe(0);
        (await fixture.Relay.SingleAsync(Ct)).ShouldNotBeNull();
        resent.Value.ShouldBe(0, "nothing is left for a later pass to send again");
    }

    private static (Guid Order, Guid Customer) Ids() => (Guid.CreateVersion7(), Guid.CreateVersion7());

    private static bool ClaimFailedLogged(NotificationsWorkerFactory host) =>
        host.CapturedLogs.Everything.Any(line => line.Contains("Send claim failed", StringComparison.Ordinal));

    /// <summary>A confirmed order's notice with its record made, so a pass meets nothing to wait on.</summary>
    private async Task<Notification> OwedAsync(Guid order, Guid customer)
    {
        await fixture.OrderAsync(order, customer);

        return await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
    }

    private async Task<Notification> DeclineAsync(Guid order) =>
        (await fixture.NotificationsAsync(order)).Single(n => n.TemplateKey == TemplateKeys.PaymentDeclined);
}
```

The cases that fill a breaker — a dependency that dies — are Task 8's, a
host each.

- [ ] **Step 3: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SendWorkerBudgetTests|FullyQualifiedName~SendWorkerTests"
```

Expected: compile failure on `Notifications.Infrastructure.Delivery.SendWorker`,
`SendPass`, `NotificationMetrics` and the fixture's `RunSendPassAsync` and
`WaitUntilDueAsync`.

- [ ] **Step 4: The repository's read**

`Records/INotificationRepository.cs`, after `ExistsAsync`:

```csharp
    /// <summary>One notice by its id, tracked, for the send worker to move and commit.</summary>
    Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct);
```

`Persistence/NotificationRepository.cs`, after `ExistsAsync`:

```csharp
    public Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct) =>
        db.NotificationLog.SingleOrDefaultAsync(n => n.NotificationId == notificationId, ct);
```

`tests/Notifications.Application.Tests/NotificationIntakeTests.cs`,
`FakeNotifications`, after `ExistsAsync`:

```csharp
        public Task<Notification?> GetAsync(Guid notificationId, CancellationToken ct) =>
            Task.FromResult(Added.Find(n => n.NotificationId == notificationId));
```

- [ ] **Step 5: The work, the pass and the claims**

`Delivery/SendWork.cs`:

```csharp
namespace Notifications.Infrastructure.Delivery;

/// <summary>One leased row: what the pass decides on, and the stamp a resend renders again.</summary>
/// <remarks>No mailbox is projected, since the row holds none (ADR-053 rule 4).</remarks>
public sealed record SendWork(
    Guid NotificationId,
    Guid EventId,
    Guid OrderId,
    Guid? CustomerId,
    string TemplateKey,
    string Parameters,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SendStartedAt,
    int? TemplateVersion,
    string? Languages);
```

`Delivery/SendPass.cs`:

```csharp
namespace Notifications.Infrastructure.Delivery;

/// <summary>What one pass did: the rows it claimed, and how many of them reached an outcome.</summary>
public readonly record struct SendPass(int Claimed, int Finished);
```

`Delivery/SendClaims.cs`:

```csharp
using System.Data;
using Common.Application;
using Common.Infrastructure.Outbox;
using Dapper;

namespace Notifications.Infrastructure.Delivery;

/// <summary>ADR-052's lease and backoff over <c>NotificationLog</c>, in <c>FulfilmentClaims</c>' shape.</summary>
internal sealed class SendClaims(IDbConnectionFactory connections)
{
    /// <summary>A pending row due and unleased; the first line repeats the claim index's filter.</summary>
    internal const string Claimable =
        """
        Status = 'Pending'
            AND NextAttemptAt <= SYSDATETIMEOFFSET()
            AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
        """;

    // One statement selects and leases, so two replicas cannot take one row; READPAST skips another's.
    private static readonly string ClaimSql =
        $"""
        WITH claimable AS (
            SELECT TOP ({SendWorker.ClaimBatchSize}) *
            FROM notifications.NotificationLog WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE {Claimable}
            ORDER BY NextAttemptAt
        )
        UPDATE claimable
        SET LockedUntil = DATEADD(second, {SendWorker.LeaseSeconds}, SYSDATETIMEOFFSET())
        OUTPUT inserted.NotificationId, inserted.EventId, inserted.OrderId, inserted.CustomerId, inserted.TemplateKey,
            inserted.Parameters, inserted.CreatedAt, inserted.SendStartedAt, inserted.TemplateVersion,
            inserted.Languages;
        """;

    // The dispatcher's ladder, read from its constants so the two cannot drift; the lease drops with it. No count
    // abandons a row: waiting ends at DeliveryOptions.GiveUpAge (ADR-052).
    private static readonly string BackOffSql =
        $"""
        UPDATE notifications.NotificationLog
        SET
            Attempts      = Attempts + 1,
            LockedUntil   = NULL,
            NextAttemptAt = DATEADD(
                second,
                POWER(2, CASE WHEN Attempts > {OutboxDispatcher.BackoffAttemptCap}
                              THEN {OutboxDispatcher.BackoffAttemptCap}
                              ELSE Attempts END) * {OutboxDispatcher.BackoffBaseSeconds},
                SYSDATETIMEOFFSET())
        WHERE NotificationId = @NotificationId AND Status = 'Pending';
        """;

    public async Task<IReadOnlyList<SendWork>> ClaimAsync(CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        // CommandDefinition, so a shutdown's token can interrupt a blocked claim.
        return [.. await connection.QueryAsync<SendWork>(new CommandDefinition(ClaimSql, cancellationToken: ct))];
    }

    public async Task BackOffAsync(Guid notificationId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(
            new CommandDefinition(BackOffSql, new { NotificationId = notificationId }, cancellationToken: ct));
    }
}
```

`Status = 'Pending'` in the backoff's `WHERE` is the one difference from
Shipping's: a row a commit has just ended is never pushed back on to the
ladder by the catch that follows a later fault in the same pass.

- [ ] **Step 6: The counter**

`Observability/NotificationMetrics.cs`, which Task 9 extends with the two
gauges:

```csharp
using System.Diagnostics.Metrics;

namespace Notifications.Infrastructure.Observability;

/// <summary>The send worker's instruments, on <see cref="OutboundMeter.Name"/>, which §13.2 collects.</summary>
public sealed class NotificationMetrics
{
    private readonly Counter<long> _resent;

    public NotificationMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);

        // The record a possible duplicate leaves: the row holds the intent, and this counts each send over one.
        _resent = meter.CreateCounter<long>(
            "notifications.mail.resent",
            unit: "{send}",
            description: "Sends started over an intent already stamped, each one a message the relay may hold twice.");
    }

    public void Resent() => _resent.Add(1);
}
```

- [ ] **Step 7: The worker**

`Delivery/SendWorker.cs`:

```csharp
using Common.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Application.Delivery;
using Notifications.Application.Mail;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Delivery;

/// <summary>Claims notices under a lease and makes every call that leaves the service.</summary>
/// <remarks>
/// <c>FulfilmentWorker</c>'s shape with <c>TrackingWorker</c>'s rows at once. No consumer calls out (ADR-052), and
/// the intent is committed before the send, so a crash after the relay's accept is a row that says so.
/// </remarks>
public sealed class SendWorker(
    IServiceScopeFactory scopes,
    ILogger<SendWorker> log) : BackgroundService
{
    /// <summary>Rows one pass sends at once; a pass lasts as long as its slowest row.</summary>
    public const int ClaimBatchSize = 10;

    /// <summary>Above <c>MailHop.TotalTimeout</c> plus <c>ContactHop.TotalRequestTimeout</c>.</summary>
    public const int LeaseSeconds = 45;

    /// <summary>How long a pass under way at a stop runs before its token fires; above one row's calls.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(25);

    /// <summary>What a pass under way at a stop leaves of the host's drain for its last commit (§15.3).</summary>
    public static readonly TimeSpan CommitRoom = TimeSpan.FromSeconds(5);

    // CA1848 (ADR-019); every line names the row by its ids and never by its mailbox (§13.4).
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Send claim failed; retrying next tick.");

    private static readonly Action<ILogger, DateTimeOffset?, Exception?> Parked =
        LoggerMessage.Define<DateTimeOffset?>(
            LogLevel.Debug,
            new EventId(2, nameof(Parked)),
            "The relay's breaker is open until {ParkedUntil}; no notice is claimed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> PassFailed =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(3, nameof(PassFailed)),
            "Send pass for notification {NotificationId} on order {OrderId} failed; the row backs off.");

    private static readonly Action<ILogger, Guid, Guid, MailFault, Exception?> RelayUnavailable =
        LoggerMessage.Define<Guid, Guid, MailFault>(
            LogLevel.Warning,
            new EventId(4, nameof(RelayUnavailable)),
            "Notification {NotificationId} on order {OrderId} met the relay unavailable ({Cause}); the row backs off.");

    private static readonly Action<ILogger, Guid, Guid, MailFault, Exception?> RelayRefused =
        LoggerMessage.Define<Guid, Guid, MailFault>(
            LogLevel.Error,
            new EventId(5, nameof(RelayRefused)),
            "The relay refused notification {NotificationId} on order {OrderId} as a deployment fault ({Cause}); " +
            "the row backs off until the relay, its session or its sender is fixed.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ContactRefused =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(6, nameof(ContactRefused)),
            "The contact read for notification {NotificationId} on order {OrderId} was refused over this host's " +
            "credential; the row backs off and no stored contact is served (ADR-052).");

    private static readonly Action<ILogger, Guid, Guid, Exception?> ParametersUnreadable =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(7, nameof(ParametersUnreadable)),
            "Notification {NotificationId} on order {OrderId} stores parameters this version cannot read; the row " +
            "backs off until a version that can claims it.");

    private static readonly Action<ILogger, Guid, Guid, TimeSpan, Exception?> GaveUp =
        LoggerMessage.Define<Guid, Guid, TimeSpan>(
            LogLevel.Warning,
            new EventId(8, nameof(GaveUp)),
            "Notification {NotificationId} on order {OrderId} was pending past its give-up age of {GiveUpAge}; it is " +
            "undeliverable and will not be retried.");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> Suppressed =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Information,
            new EventId(9, nameof(Suppressed)),
            "Notification {NotificationId} on order {OrderId} declined a payment customer {CustomerId} had " +
            "cancelled; it is suppressed (ADR-049).");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> NoSuchCustomer =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Warning,
            new EventId(10, nameof(NoSuchCustomer)),
            "Notification {NotificationId} on order {OrderId} names customer {CustomerId}, whom the owner does not " +
            "know; it is undeliverable and the contact row is deleted (ADR-052).");

    private static readonly Action<ILogger, Guid, Guid, Guid, Exception?> ServedStale =
        LoggerMessage.Define<Guid, Guid, Guid>(
            LogLevel.Warning,
            new EventId(11, nameof(ServedStale)),
            "Notification {NotificationId} on order {OrderId} is served customer {CustomerId}'s stored contact, as " +
            "the owner could not answer (ADR-052).");

    private static readonly Action<ILogger, Guid, Guid, string, Exception?> Refused =
        LoggerMessage.Define<Guid, Guid, string>(
            LogLevel.Warning,
            new EventId(12, nameof(Refused)),
            "Notification {NotificationId} on order {OrderId} is undeliverable: {Reason}; it will not be retried.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> Resending =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Warning,
            new EventId(13, nameof(Resending)),
            "Notification {NotificationId} on order {OrderId} was claimed after its intent; it is sent again under " +
            "the same Message-ID.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> SentUncommitted =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Error,
            new EventId(14, nameof(SentUncommitted)),
            "The relay accepted notification {NotificationId} on order {OrderId}, but the outcome was not committed; " +
            "the next pass sends it again under the same Message-ID.");

    private static readonly Action<ILogger, Guid, Guid, Exception?> Outgrown =
        LoggerMessage.Define<Guid, Guid>(
            LogLevel.Information,
            new EventId(15, nameof(Outgrown)),
            "Notification {NotificationId} on order {OrderId} moved beneath this pass; it is left as it stands.");

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(MailHop.SendTick);

        // A stop ends the loop and a claim in flight, never the rows under way: theirs fires DrainBudget after it.
        using CancellationTokenSource drain = new();
        using CancellationTokenRegistration stopping = stoppingToken.Register(() => drain.CancelAfter(DrainBudget));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken, drain.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The token, not the type: a call's own deadline throws the same type, and an escape stops the host.
                ClaimFailed(log, ex);
            }
        }
    }

    /// <summary>One claim-and-send pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public Task<SendPass> RunOnceAsync(CancellationToken ct) => RunOnceAsync(ct, ct);

    // The claim on the stop's token, so a stop leases and starts no row; the rows on the drain's (§15.3).
    private async Task<SendPass> RunOnceAsync(CancellationToken claim, CancellationToken rows)
    {
        await using AsyncServiceScope claimScope = scopes.CreateAsyncScope();
        MailPipeline relay = claimScope.ServiceProvider.GetRequiredService<MailPipeline>();

        // The breaker parks the claim, not the messages: rows keep their backoff and nothing reaches a queue (§9.7).
        if (relay.IsOpen)
        {
            Parked(log, relay.ParkedUntil, null);
            return new SendPass(0, 0);
        }

        SendClaims claims = claimScope.ServiceProvider.GetRequiredService<SendClaims>();
        IReadOnlyList<SendWork> claimed = await claims.ClaimAsync(claim);

        // Every row at once, so a pass lasts one row's calls and the lease bounds it; WhenAll, so one row's fault
        // leaves the others to finish.
        bool[] finished = await Task.WhenAll(claimed.Select(work => SendOrBackOffAsync(claims, work, rows)));

        return new SendPass(claimed.Count, finished.Count(f => f));
    }

    private async Task<bool> SendOrBackOffAsync(SendClaims claims, SendWork work, CancellationToken ct)
    {
        try
        {
            // A scope per row, so a row that throws mid-write hands the next none of its tracked state.
            await using AsyncServiceScope row = scopes.CreateAsyncScope();

            return await SendAsync(row.ServiceProvider, claims, work, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Logged before the backoff is written, so a database fault there cannot hide this one.
            Fault(work, ex);
            await claims.BackOffAsync(work.NotificationId, ct);

            return false;
        }
    }

    /// <summary>One leased row, step by step; true when it reached an outcome (ADR-049, ADR-052).</summary>
    internal async Task<bool> SendAsync(IServiceProvider sp, SendClaims claims, SendWork work, CancellationToken ct)
    {
        TimeProvider clock = sp.GetRequiredService<TimeProvider>();
        TimeSpan giveUpAge = sp.GetRequiredService<IOptions<DeliveryOptions>>().Value.GiveUpAge!.Value;

        // Asked before anyone is: one age covers every wait, as a notice a day late is worse than none (ADR-052).
        if (SendRules.HasGivenUp(work.CreatedAt, clock.GetUtcNow(), giveUpAge))
        {
            bool ended = await CommitAsync(
                sp, work, (n, now) => n.MarkUndeliverable(NotificationReasons.GaveUp, now), ct);

            if (ended)
                GaveUp(log, work.NotificationId, work.OrderId, giveUpAge, null);

            return ended;
        }

        OrderRecord? order = await sp.GetRequiredService<IOrderRecordRepository>().GetAsync(work.OrderId, ct);

        // §9.4 orders nothing between the events: the row waits on its backoff for Ordering's record (ADR-017).
        if (SendRules.AwaitsOrderRecord(work.TemplateKey, order))
        {
            await claims.BackOffAsync(work.NotificationId, ct);
            return false;
        }

        Guid customer = work.CustomerId ?? order.CustomerId;

        if (SendRules.Suppresses(work.TemplateKey, order))
        {
            bool suppressed = await CommitAsync(
                sp,
                work,
                (n, now) =>
                {
                    n.AssignCustomer(customer);
                    return n.Suppress(now);
                },
                ct);

            if (suppressed)
                Suppressed(log, work.NotificationId, work.OrderId, customer, null);

            return suppressed;
        }

        // Copied once, so every later step and §11.7's erasure find the customer on the row.
        if (work.CustomerId is null)
            await CommitAsync(sp, work, (n, _) => n.AssignCustomer(customer), ct);

        if (await ContactAsync(sp, work, customer, ct) is not ContactLookup.Found contact)
        {
            bool unknown = await CommitAsync(
                sp, work, (n, now) => n.MarkUndeliverable(NotificationReasons.NoSuchCustomer, now), ct);

            if (unknown)
                NoSuchCustomer(log, work.NotificationId, work.OrderId, customer, null);

            return unknown;
        }

        NotificationParameters parameters = ParametersFormat.Read(work.Parameters);
        TemplateRenderer renderer = sp.GetRequiredService<TemplateRenderer>();
        RenderedMessage message;

        if (work.SendStartedAt is null)
        {
            message = renderer.Render(work.TemplateKey, parameters, contact.Locale);

            // The intent, committed before the send, so a crash after the relay's accept leaves a row that says so.
            bool started = await CommitAsync(
                sp, work, (n, now) => n.StartSend(message.TemplateVersion, message.LanguageList, now), ct);

            if (!started)
            {
                Outgrown(log, work.NotificationId, work.OrderId, null);
                return false;
            }
        }
        else
        {
            // A send that may have reached the relay: the stamped text again, under the same Message-ID, and counted.
            message = renderer.Render(
                work.TemplateKey, parameters, work.TemplateVersion!.Value, work.Languages!.Split(','));
            sp.GetRequiredService<NotificationMetrics>().Resent();
            Resending(log, work.NotificationId, work.OrderId, null);
        }

        MailResult result = await sp.GetRequiredService<IMailChannel>().SendAsync(
            new OutboundMail(
                contact.Email,
                message.Subject,
                message.Body,
                new MailMessageId(work.EventId, work.TemplateKey),
                message.Languages),
            ct);

        return result switch
        {
            MailResult.Accepted => await CompleteAsync(sp, work),
            MailResult.Refused refused => await RefusedAsync(sp, work, refused.Reason, ct),
            _ => throw new InvalidOperationException($"Unknown relay answer {result.GetType().Name}.")
        };
    }

    /// <summary>ADR-052's five outcomes over the stored row and the owner; any other fault throws.</summary>
    private async Task<ContactLookup> ContactAsync(
        IServiceProvider sp,
        SendWork work,
        Guid customer,
        CancellationToken ct)
    {
        IContactStore store = sp.GetRequiredService<IContactStore>();
        TimeProvider clock = sp.GetRequiredService<TimeProvider>();

        ContactRecord? stored = await store.GetAsync(customer, ct);
        ContactAge age = SendRules.AgeOf(stored, clock.GetUtcNow(), sp.GetRequiredService<ContactOptions>());

        if (age == ContactAge.Fresh)
            return new ContactLookup.Found(stored!.Email, stored.Locale);

        ContactLookup answer;

        try
        {
            answer = await sp.GetRequiredService<IContactSource>().GetAsync(customer, ct);
        }
        // An owner that cannot answer is served a stale row; a refused credential never is (ADR-052).
        catch (Exception ex) when (
            ex is not ContactSourceRefusedException && age == ContactAge.Stale && !ct.IsCancellationRequested)
        {
            ServedStale(log, work.NotificationId, work.OrderId, customer, ex);
            return new ContactLookup.Found(stored!.Email, stored.Locale);
        }

        // Kept before the send, so a pass repeated after a crash reads it from the row and not the owner (ADR-052).
        if (answer is ContactLookup.Found found)
            await store.SaveAsync(customer, found, clock.GetUtcNow(), ct);
        else
            await store.DeleteAsync(customer, ct);

        return answer;
    }

    private async Task<bool> CompleteAsync(IServiceProvider sp, SendWork work)
    {
        try
        {
            // Never cancelled: the relay holds the message, and an abandoned commit would send it twice.
            return await CommitAsync(sp, work, (n, now) => n.MarkSent(now), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The relay holds the message and the row does not say so; its intent makes the next pass a counted resend.
            SentUncommitted(log, work.NotificationId, work.OrderId, ex);
            throw;
        }
    }

    private async Task<bool> RefusedAsync(
        IServiceProvider sp,
        SendWork work,
        MailRefusal refusal,
        CancellationToken ct)
    {
        string reason = refusal switch
        {
            MailRefusal.RecipientRefused => NotificationReasons.RecipientRefused,
            MailRefusal.NotAMailbox => NotificationReasons.NotAMailbox,
            _ => throw new InvalidOperationException($"Unknown refusal {refusal}.")
        };

        bool ended = await CommitAsync(sp, work, (n, now) => n.MarkUndeliverable(reason, now), ct);

        if (ended)
            Refused(log, work.NotificationId, work.OrderId, reason, null);

        return ended;
    }

    // The type picks the line, never whether the row backs off; none of them quotes a mailbox (§13.4).
    private void Fault(SendWork work, Exception ex)
    {
        switch (ex)
        {
            case MailUnavailableException { Cause: MailFault.Transient or MailFault.Unconfirmed } relay:
                RelayUnavailable(log, work.NotificationId, work.OrderId, relay.Cause, ex);
                break;
            case MailUnavailableException relay:
                RelayRefused(log, work.NotificationId, work.OrderId, relay.Cause, ex);
                break;
            case ContactSourceRefusedException:
                ContactRefused(log, work.NotificationId, work.OrderId, ex);
                break;
            case UnreadableParametersException:
                ParametersUnreadable(log, work.NotificationId, work.OrderId, ex);
                break;
            default:
                PassFailed(log, work.NotificationId, work.OrderId, ex);
                break;
        }
    }

    /// <summary>One of the record's moves, committed; false when the row had outgrown it and nothing changed.</summary>
    private static async Task<bool> CommitAsync(
        IServiceProvider sp,
        SendWork work,
        Func<Notification, DateTimeOffset, bool> move,
        CancellationToken ct)
    {
        IUnitOfWork unitOfWork = sp.GetRequiredService<IUnitOfWork>();

        return await unitOfWork.ExecuteAsync(
            async inner =>
            {
                Notification notification =
                    await sp.GetRequiredService<INotificationRepository>().GetAsync(work.NotificationId, inner)
                    ?? throw new InvalidOperationException(
                        $"Notification {work.NotificationId} was claimed and is now absent.");

                bool moved = move(notification, sp.GetRequiredService<TimeProvider>().GetUtcNow());
                await unitOfWork.SaveChangesAsync(inner);

                return moved;
            },
            ct);
    }
}
```

Four things in it a reviewer would question:

- **A stop drains the pass; it does not cancel it.** `BackgroundService`
  cancels `stoppingToken` first, so a pass run under it would be cancelled
  between the relay's 250 and the `MarkSent` commit on every rolling deploy,
  and sent again after the lease. The loop starts no pass once the token
  fires, and the claim runs on it, so a claim in flight at the stop is
  cancelled rather than lease and start rows; the rows already claimed run on
  a token that fires `DrainBudget` after it, and `MarkSent` commits on
  `CancellationToken.None` once the relay has accepted. The budget is a
  constant, as `HostOptions` is in the hosting package Infrastructure does
  not reference; `SendWorkerBudgetTests` holds it and `CommitRoom` inside the
  host's default `ShutdownTimeout`.
- **`SendAsync` is `internal` and takes `SendClaims`**, which is internal, so
  the member cannot be public; a suite reaches it through
  `InternalsVisibleTo`, as Shipping's reaches `FulfilAsync`. Nothing here
  needs it yet; `RunOnceAsync` is what every test drives.
- **The order record is read outside the unit**, as the contact is: it is a
  read of a row only consumers write, and the commits that follow each reload
  the notice they move, so a cancellation landing between the read and the
  commit is met on the next pass, where the decline is already suppressed or
  sent. ADR-049's guarantee is about which fact decides — the record's
  cancellation — and a decline sent before the cancellation exists is not a
  case: the decline waits for it.
- **`work.Languages!.Split(',')`** reads PR-4's `LanguageList`, which joins
  with a bare comma; a stamp is never null when `SendStartedAt` is set,
  because `StartSend` sets all three in one move.

- [ ] **Step 8: The registrations**

`Observability/MetricsInitialiser.cs` gains `NotificationMetrics` as its last
parameter, after PR-3's `ContactMetrics`, with its guard:

```csharp
    public MetricsInitialiser(
        MessagingMetrics messaging,
        RequestMetrics requests,
        MailMetrics mail,
        ContactMetrics contact,
        NotificationMetrics notifications)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(contact);
        ArgumentNullException.ThrowIfNull(notifications);
    }
```

`DependencyInjection.cs`, after Task 4's `DeliveryOptions` binding, with
`using Notifications.Infrastructure.Observability;` in sorted position where
absent:

```csharp
        // The send worker's instruments, a singleton so one meter holds one set (§13.6).
        services.AddSingleton<NotificationMetrics>();

        // The send pass; the generic overload, so a suite can remove it by its ImplementationType (§12.4).
        services.AddScoped<SendClaims>();
        services.AddHostedService<SendWorker>();
```

before `services.AddHostedService<RetentionPurgeService>();`, which stays
last so it is stopped first.

`tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`, in
`ConfigureServices`, after the `RetentionPurgeService` block, with
`using Notifications.Infrastructure.Delivery;` already present from Task 4:

```csharp
                // The send pass, by the same match, so its tick cannot send a row underneath an assertion.
                ServiceDescriptor send = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(SendWorker));
                services.Remove(send);

                services.AddSingleton<SendWorker>();
```

`tests/Notifications.TestSupport/ServiceFixture.cs`, with
`using Notifications.Infrastructure.Delivery;`:

```csharp
    /// <summary>Runs exactly one send pass on the fixture's host, with no timers and no waiting.</summary>
    public Task<SendPass> RunSendPassAsync() =>
        Factory.Services.GetRequiredService<SendWorker>().RunOnceAsync(TestContext.Current.CancellationToken);

    /// <summary>Waits for the engine's clock to reach the order's pending rows, stamped by the host's.</summary>
    public Task WaitUntilDueAsync(Guid order) =>
        WaitUntilAsync(async () => await ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE OrderId = {0} " +
            "AND Status = 'Pending' AND NextAttemptAt > SYSDATETIMEOFFSET()",
            order) == 0);

    /// <summary>Runs passes, each pending row made due first, until none is pending or a bound is hit.</summary>
    public async Task SendUntilSettledAsync(int maxPasses = 10)
    {
        const string Pending = "SELECT Value = COUNT(*) FROM notifications.NotificationLog WHERE Status = 'Pending'";

        for (int pass = 0; pass < maxPasses; pass++)
        {
            if (await ScalarAsync<int>(Pending) == 0)
                return;

            await ExecuteAsync(
                "UPDATE notifications.NotificationLog SET NextAttemptAt = DATEADD(second, -1, SYSDATETIMEOFFSET()) " +
                "WHERE Status = 'Pending';");
            await RunSendPassAsync();
        }

        throw new TimeoutException($"A notice was still pending after {maxPasses} passes.");
    }
```

`WaitUntilDueAsync` is Shipping's `WaitUntilAttemptDueAsync` for a notice: a
consumer stamps `NextAttemptAt` from the host's clock and the claim compares
it with the engine's, so a pass run straight after a delivery could find the
row not yet due. Every such pass waits on it; `SendUntilSettledAsync` makes
its rows due itself.

`tests/Notifications.Worker.Tests/MetricsRegistrationTests.cs`, in
`The_metrics_selector_actually_selects_something`, with
`using Notifications.Infrastructure.Observability;` where absent:

```csharp
        registered.ShouldContain(typeof(NotificationMetrics));
```

- [ ] **Step 9: Run the suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Application.Tests
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SendWorkerBudgetTests|FullyQualifiedName~SendWorkerTests|FullyQualifiedName~MetricsRegistrationTests|FullyQualifiedName~HostSmokeTests"
```

Expected: 0 warnings; green, Docker running. Before Step 8's initialiser edit,
`Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be` fails on
"add it to MetricsInitialiser" — the red that proves the selector sees the new
type. `HostSmokeTests`' readiness test stays green with its two names: the
worker added no check, and the relay and Keycloak are not in the set (spec,
section 11).

Prove the lease by mutation: delete `Claimable`'s `LockedUntil` line, run
`Two_workers_overlapping_claim_one_row_once`, and see the second worker claim
the leased row, its pass no longer `SendPass(0, 0)`; restore. A zero
`LeaseSeconds` would fail the case at its lease wait instead, never reaching
the assertion. The intent's order is proved in Task 8, whose stopped relay
fails the send before a moved commit could run; the crash case cannot, as its
fault fires only on `MarkSent`. Prove the drain by mutation:
pass `stoppingToken` as `RunOnceAsync`'s `rows`, run
`A_host_stopped_mid_pass_finishes_the_send_and_commits_it_once`, and see the
row still `Pending`; restore.

- [ ] **Step 10: Commit**

```bash
git add src/Services/Notifications tests/Notifications.TestSupport tests/Notifications.Worker.Tests tests/Notifications.Application.Tests
git commit -m "feat(notifications): SendWorker claims under a lease, waits, suppresses, reads the contact, stamps the intent and sends"
```

The body walks the table above, argues the unreadable payload's backoff, the
resend's render and count, and why a commit does not release the lease.

---

### Task 8: A dependency dies

**Files:**
- Test: `tests/Notifications.Worker.Tests/SendFaultTests.cs`

**Interfaces:**
- Consumes: Tasks 3, 6 and 7; PR-2's `Mailpit`; PR-3's `ContactCounter`
  and `ContactHop.MaxRetryAttempts`; PR-4's `QueueDepthAsync` and
  `EventsQueue`.

**Why a host each.** As in Shipping's `FulfilmentFaultTests` and PR-2's
`MailFaultTests`: each of these fills a breaker — the relay's sized to open at
two failed sends, the contact read's at four failed attempts — and behind one
shared host the first case would open it for every case after. So each case
builds `fixture.NewWorkerHost()`, over the same database, broker and stubs,
with breakers of its own.

**What "a dependency dies" has to show, from spec section 13.** With the relay
stopped, the row stays `Pending` with its intent and its backoff, the
`_error` queue is empty, and when the relay returns the row sends. With
Keycloak refusing, the same, and no stored contact is served. With the relay
refusing every attempt, the breaker opens and the next pass claims nothing.
The no-mailbox export of section 12 rides on the same staging, because it is
the staging where the most lines carrying an exception are written.

**No `Fault<T>` to read, as section 9 says.** "No `Fault<T>` exists to carry
one, since the worker is not a consumer" (spec, section 9) — no consumer
makes the read under ADR-052 — so the `_error` queue's depth is the
assertion: nothing was published to carry a mailbox. The log is the one
export that can, and it is searched whole.

- [ ] **Step 1: Write the tests**

`tests/Notifications.Worker.Tests/SendFaultTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using MessagingRegistration = Notifications.Infrastructure.Messaging.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>A dependency that dies, each on its own host, as the breakers these cases fill are sized to open.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class SendFaultTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly string ErrorQueue = $"{MessagingRegistration.EventsQueue}_error";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_relay_that_stops_leaves_its_row_pending_and_sends_it_when_it_returns()
    {
        await using Mailpit relay = Mailpit.PlainOn(FreeLoopbackPort());
        await relay.StartAsync(Ct);
        using NotificationsWorkerFactory host = fixture.NewWorkerHost(relay.Host, relay.Port);
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromMinutes(1));
        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.WaitUntilDueAsync(order);
        await relay.StopAsync(Ct);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        Notification waiting = (await fixture.NotificationsAsync(order)).ShouldHaveSingleItem();
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
        waiting.SendStartedAt.ShouldNotBeNull("the intent precedes every send, a failed one included");
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0, "a dead relay is no consumer's fault (ADR-052)");

        await relay.StartAsync(Ct);
        await fixture.ClearBackoffAsync(waiting.NotificationId);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));

        (await fixture.NotificationAsync(waiting.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
        (await relay.SingleAsync(Ct)).MessageId.Trim('<', '>')
            .ShouldBe($"{waiting.EventId:N}.{TemplateKeys.OrderPlaced}@commerce.test");
    }

    [Fact]
    public async Task A_relay_refusing_every_attempt_opens_the_breaker_and_the_next_pass_claims_nothing()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        await fixture.Relay.RefuseRecipientsAsync(451, Ct);
        await OwedAsync(Guid.CreateVersion7());
        await OwedAsync(Guid.CreateVersion7());

        (await PassAsync(host)).ShouldBe(new SendPass(2, 0));
        host.Services.GetRequiredService<MailPipeline>().IsOpen
            .ShouldBeTrue("two sends of two attempts each reach MailHop's throughput, every one failed");

        // Healthy again, so a send that left the process now would be delivered.
        await fixture.Relay.ResetAsync(Ct);
        Notification parked = await OwedAsync(Guid.CreateVersion7());

        (await PassAsync(host)).ShouldBe(new SendPass(0, 0), "an open breaker parks the claim, not the messages");

        Notification untouched = await fixture.NotificationAsync(parked.NotificationId);
        untouched.Status.ShouldBe(NotificationStatus.Pending);
        untouched.Attempts.ShouldBe(0, "a row no pass claimed is not backed off");
        untouched.LockedUntil.ShouldBeNull();
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0);
    }

    [Fact]
    public async Task A_stale_contact_is_served_while_the_owner_cannot_answer()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 503);
        await OwedAsync(order, customer);
        using ContactCount refused = ContactCounter.Refused(host.Services);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));

        (await fixture.Relay.SingleAsync(Ct)).To.ShouldHaveSingleItem().Address.ShouldBe(Mailbox);
        fixture.ContactCalls(customer).ShouldBe(ContactHop.MaxRetryAttempts + 1, "the owner was asked, and retried");
        refused.Value.ShouldBe(0, "an outage is not a refused credential");
        host.CapturedLogs.Everything.ShouldContain(line => line.Contains("could not answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_contact_past_its_stale_ceiling_is_never_served_and_the_row_waits_for_the_owner()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(25));
        fixture.ContactAnswers(customer, 503);
        Notification owed = await OwedAsync(order, customer);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
        waiting.SendStartedAt.ShouldBeNull("nothing was rendered without a contact");
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusing_owner_backs_the_row_off_serves_no_stored_contact_and_is_counted_until_it_answers()
    {
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Mailbox, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 403);
        Notification owed = await OwedAsync(order, customer);
        using ContactCount refused = ContactCounter.Refused(host.Services);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        Notification waiting = await fixture.NotificationAsync(owed.NotificationId);
        waiting.Status.ShouldBe(NotificationStatus.Pending);
        waiting.Attempts.ShouldBe(1);
        (await fixture.Relay.MessagesAsync(Ct)).ShouldBeEmpty("a refusal never proceeds on a stale row (ADR-052)");
        refused.Value.ShouldBe(1);
        host.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("refused over this host's credential", StringComparison.Ordinal));
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0);

        fixture.Keycloak.Reset();
        fixture.ContactAnswers(customer, Mailbox, "en");
        await fixture.ClearBackoffAsync(owed.NotificationId);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));
        (await fixture.NotificationAsync(owed.NotificationId)).Status.ShouldBe(NotificationStatus.Sent);
    }

    [Fact]
    public async Task No_line_the_worker_logs_holds_the_mailbox_or_the_message()
    {
        // Every line carrying an exception: the owner's outage, the relay's refusal, the resend over the intent.
        const string Private = "aigerim.private@example.test";
        using NotificationsWorkerFactory host = fixture.NewWorkerHost();
        (Guid order, Guid customer) = Ids();
        await fixture.StageContactAsync(customer, Private, "en", TimeSpan.FromHours(1));
        fixture.ContactAnswers(customer, 503);
        await fixture.Relay.RefuseRecipientsAsync(451, Ct);
        Notification owed = await OwedAsync(order, customer);

        (await PassAsync(host)).ShouldBe(new SendPass(1, 0));

        await fixture.Relay.ResetAsync(Ct);
        await fixture.ClearBackoffAsync(owed.NotificationId);
        (await PassAsync(host)).ShouldBe(new SendPass(1, 1));

        MailpitMessage sent = await fixture.Relay.SingleAsync(Ct);
        string[] captured = [.. host.CapturedLogs.Everything, .. fixture.CapturedLogs.Everything];

        captured.ShouldContain(
            line => line.StartsWith(typeof(MailUnavailableException).FullName!, StringComparison.Ordinal),
            "a capture that dropped the relay's exception would search none of it");
        captured.ShouldContain(
            line => line.Contains("could not answer", StringComparison.Ordinal),
            "a capture that missed the stale contact's line would search none of the owner's exception");
        captured.ShouldContain(
            line => line.Contains("sent again under the same Message-ID", StringComparison.Ordinal),
            "a capture that missed the resend would search none of the second send");

        captured.ShouldNotContain(line => line.Contains("aigerim.private", StringComparison.OrdinalIgnoreCase));
        captured.ShouldNotContain(line => line.Contains(sent.Subject, StringComparison.Ordinal));
        (await fixture.QueueDepthAsync(ErrorQueue)).ShouldBe(0, "no consumer faulted, so no Fault<T> carries anything");
    }

    private static (Guid Order, Guid Customer) Ids() => (Guid.CreateVersion7(), Guid.CreateVersion7());

    private static Task<SendPass> PassAsync(NotificationsWorkerFactory host) =>
        host.Services.GetRequiredService<SendWorker>().RunOnceAsync(Ct);

    // Bound and released here, so Docker can bind it next; a port another process takes between is a rerun.
    private static int FreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();

        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>A confirmed order's notice for a customer with a fresh contact, unless one is given.</summary>
    private async Task<Notification> OwedAsync(Guid order, Guid? customer = null)
    {
        Guid owner = customer ?? Guid.CreateVersion7();
        if (customer is null)
            await fixture.StageContactAsync(owner, Mailbox, "en", TimeSpan.FromMinutes(1));

        await fixture.OrderAsync(order, owner);

        return await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
    }
}
```

- [ ] **Step 2: Run them**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SendFaultTests"
```

Expected: green, Docker running — these exercise Tasks 3 and 7's code and
are written after it, so the red they prove is by mutation:

- delete the `if (relay.IsOpen)` block in `RunOnceAsync`, and see the
  breaker case claim the parked row and its `Attempts` reach one; restore;
- change `ContactAsync`'s filter to catch `ContactSourceRefusedException` too,
  and see the refusing-owner case send to the stale mailbox; restore;
- change `ContactAge.Stale` in that filter to `!= ContactAge.Absent`, and see
  the expired case send; restore;
- move the `StartSend` commit to after `SendAsync`, and see
  `A_relay_that_stops_leaves_its_row_pending_and_sends_it_when_it_returns`
  find `SendStartedAt` null on the waiting row; restore.

The stopped relay costs a refused connection and one jittered retry, a few
seconds; the breaker case two sends of two `451` attempts each, also seconds.

- [ ] **Step 3: Commit**

```bash
git add tests/Notifications.Worker.Tests
git commit -m "test(notifications): a stopped relay, a refusing or silent Keycloak, and an open breaker each leave rows pending and nothing on _error"
```

The body records the three mutations and why the fault half of the export
assertion is the `_error` queue's depth.

---

### Task 9: `notifications.waiting` by step, and `notifications.overdue`

**Files:**
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Notifications.Infrastructure.csproj` — `Microsoft.Extensions.Caching.Memory`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Observability/WaitingSteps.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Observability/INotificationStats.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Observability/NotificationStats.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Observability/NotificationMetrics.cs` — the two gauges
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs` — the stats, on a bounded connection
- Test: `tests/Notifications.Worker.Tests/NotificationGaugeTests.cs`

**Interfaces:**
- Consumes: Task 7's `SendClaims.Claimable` and `NotificationMetrics`;
  PR-2's `MailHop.SendTick`; PR-4's `TemplateKeys.PaymentDeclined` and
  `OrderRecords`.
- Produces: `WaitingSteps.OrderRecord`, `Contact`, `Relay` and `All`;
  `INotificationStats` with `WaitingByStep()` and `OverdueSeconds()`;
  `NotificationStats` with `ConnectTimeoutSeconds` and `OverdueGrace`;
  `NotificationMetrics(IMeterFactory, INotificationStats, ILogger<NotificationMetrics>)`;
  the series `notifications_waiting` with `step`, and
  `notifications_overdue_seconds` with no attribute — PR-6's dashboard and
  `check.py` step read exactly those two.

**The step is read from the tables, so no column is owed.** Section 6's
table has no column naming what a row waits on, and none is needed: a row
past its first backoff with `SendStartedAt` set is waiting on the relay; one
without, whose order record is absent — or is a decline's whose record holds
no cancellation — is waiting on the order record; every other is waiting on
the contact. "Every other" includes a row whose parameters this version cannot
read, which Task 7 backs off before the intent; its error line, not this
gauge, is its signal, and the runbook's half in PR-6 is told so by this
plan's PR body.

**The overdue gauge is `shipping.shipments.overdue`'s form, less two ticks.**
The claim's own population — `SendClaims.Claimable`, read from the claim so
the two cannot drift — aged from the earliest `NextAttemptAt`, in seconds.
Section 12 asks for how long that row "has waited unclaimed past two ticks":
a healthy worker reaches a due row within a tick, so a reading at zero is
health and the first ten seconds of every row's wait are not reported. With
one pass there is no `pass` attribute.

**Every step or none, and the overdue reading or none**, as Shipping's
gauges are: a step missing from a `max by (step)` reads as a healthy zero,
so a failed read omits the whole collection and logs it, and the collector,
which abandons its pass on an exception, never sees one (§13.6).

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Worker.Tests/NotificationGaugeTests.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Observability;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>Notices past their first backoff by step, and the wait of the longest-due notice no pass holds.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationGaugeTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Each_waiting_notice_is_counted_under_the_step_it_waits_on()
    {
        Notification noRecord = await fixture.PendingAsync(TemplateKeys.ShipmentDispatched, Guid.CreateVersion7());

        Guid declined = Guid.CreateVersion7();
        await fixture.OrderAsync(declined, Guid.CreateVersion7());
        Notification noCancellation = await fixture.PendingAsync(TemplateKeys.PaymentDeclined, declined);

        Guid placed = Guid.CreateVersion7();
        await fixture.OrderAsync(placed, Guid.CreateVersion7());
        Notification noContact = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, placed);
        Notification noRelay = await fixture.PendingAsync(TemplateKeys.OrderPlaced, placed);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET SendStartedAt = SYSDATETIMEOFFSET() WHERE NotificationId = {0};",
            noRelay.NotificationId);

        foreach (Notification waiting in new[] { noRecord, noCancellation, noContact, noRelay })
            await fixture.SetAttemptsAsync(waiting.NotificationId, 1);

        ReadGauge("notifications.waiting").ShouldBe(
            [("step=order_record", 2), ("step=contact", 1), ("step=relay", 1)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task A_notice_no_pass_has_failed_and_a_finished_one_wait_on_nothing()
    {
        Guid order = Guid.CreateVersion7();
        await fixture.OrderAsync(order, Guid.CreateVersion7());
        await fixture.PendingAsync(TemplateKeys.OrderPlaced, order);
        Notification finished = await fixture.PendingAsync(TemplateKeys.OrderConfirmed, order);
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Status = 'Sent', Attempts = 2, " +
            "CompletedAt = SYSDATETIMEOFFSET() WHERE NotificationId = {0};",
            finished.NotificationId);

        ReadGauge("notifications.waiting").ShouldBe(
            [("step=order_record", 0), ("step=contact", 0), ("step=relay", 0)],
            ignoreOrder: true,
            "every step reports, as a step missing from a max reads as a healthy zero");
    }

    [Fact]
    public async Task A_due_notice_no_pass_holds_reads_as_its_wait_past_two_ticks()
    {
        Notification due = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await DueAsync(due, TimeSpan.FromSeconds(120));

        (string tags, double overdue) = ReadGauge("notifications.overdue").ShouldHaveSingleItem();

        tags.ShouldBeEmpty("one pass, so no pass attribute");
        double grace = NotificationStats.OverdueGrace.TotalSeconds;
        overdue.ShouldBeInRange(120 - grace, 180 - grace);
    }

    [Fact]
    public async Task A_notice_due_inside_two_ticks_reads_as_no_wait()
    {
        Notification due = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await DueAsync(due, TimeSpan.FromSeconds(3));

        ReadGauge("notifications.overdue").ShouldHaveSingleItem().Value.ShouldBe(0);
    }

    [Fact]
    public async Task A_notice_a_pass_holds_has_no_wait()
    {
        // Due long ago and being sent: a MIN over the due column alone would read it.
        Notification held = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await DueAsync(held, TimeSpan.FromMinutes(10));
        await fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET LockedUntil = DATEADD(second, 30, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            held.NotificationId);

        ReadGauge("notifications.overdue").ShouldHaveSingleItem().Value.ShouldBe(0);
    }

    [Fact]
    public void A_stats_reader_that_throws_reports_neither_gauge_rather_than_a_healthy_reading()
    {
        List<(string Tags, double Value)> measured = Read(new Throwing(), "notifications.waiting", allowEmpty: true);
        List<(string Tags, double Value)> overdue = Read(new Throwing(), "notifications.overdue", allowEmpty: true);

        measured.ShouldBeEmpty();
        overdue.ShouldBeEmpty();
    }

    private Task DueAsync(Notification notification, TimeSpan ago) =>
        fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET NextAttemptAt = DATEADD(second, -{1}, SYSDATETIMEOFFSET()) " +
            "WHERE NotificationId = {0};",
            notification.NotificationId,
            (int)ago.TotalSeconds);

    /// <summary>One of the gauges, read once over this suite's own stats reader on the fixture's database.</summary>
    private List<(string Tags, double Value)> ReadGauge(string instrument)
    {
        using NotificationStats stats = new(new SqlConnectionFactory(fixture.ConnectionString));

        return Read(stats, instrument, allowEmpty: false);
    }

    private static List<(string Tags, double Value)> Read(INotificationStats stats, string instrument, bool allowEmpty)
    {
        // The factory has to outlive the collection: a Meter disposed with its factory publishes nothing.
        using ServiceProvider services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory factory = services.GetRequiredService<IMeterFactory>();
        NotificationMetrics metrics = new(factory, stats, NullLogger<NotificationMetrics>.Instance);
        metrics.ShouldNotBeNull();

        // The same Meter the constructor used, since IMeterFactory caches by name.
        Meter mine = factory.Create(OutboundMeter.Name);
        List<(string Tags, double Value)> measured = [];
        bool enabled = false;
        using MeterListener listener = new();

        listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == instrument)
            {
                l.EnableMeasurementEvents(published);
                enabled = true;
            }
        };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            measured.Add((string.Join(",", Pairs(tags)), value)));

        listener.Start();
        listener.RecordObservableInstruments();

        // Fails closed: with nothing enabled, an assertion over an empty list asserts nothing.
        enabled.ShouldBeTrue($"the listener enabled no {instrument} on this meter");
        if (!allowEmpty)
            measured.ShouldNotBeEmpty();

        return measured;
    }

    private static List<string> Pairs(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        List<string> pairs = [];
        foreach (KeyValuePair<string, object?> tag in tags)
            pairs.Add($"{tag.Key}={tag.Value}");

        return pairs;
    }

    private sealed class Throwing : INotificationStats
    {
        public IReadOnlyDictionary<string, int> WaitingByStep() => throw new InvalidOperationException("Staged.");

        public double OverdueSeconds() => throw new InvalidOperationException("Staged.");
    }
}
```

`NotificationStats` and `SqlConnectionFactory` are internal, which PR-4's
`InternalsVisibleTo` for this suite reaches; Shipping's gauge suite reads its
stats the same way.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NotificationGaugeTests"
```

Expected: compile failure on `NotificationStats`, `INotificationStats` and
`NotificationMetrics`' three-argument constructor.

- [ ] **Step 3: The package reference**

`Notifications.Infrastructure.csproj`, in the `PackageReference` group — the
line PR-1's mode cut with the outbox's stats, back for this reader:

```xml
    <!-- NotificationStats' MemoryCache (§13.6), named directly though Common.Infrastructure carries it. -->
    <PackageReference Include="Microsoft.Extensions.Caching.Memory" />
```

- [ ] **Step 4: The steps, the questions and their reader**

`Observability/WaitingSteps.cs`:

```csharp
namespace Notifications.Infrastructure.Observability;

/// <summary>What a waiting notice waits on, the <c>step</c> attribute of <c>notifications.waiting</c>.</summary>
public static class WaitingSteps
{
    /// <summary>Ordering's record of the order, or a decline's cancellation on it (ADR-049).</summary>
    public const string OrderRecord = "order_record";

    /// <summary>The owner's answer about the customer, or anything else before the intent (ADR-052).</summary>
    public const string Contact = "contact";

    /// <summary>The relay, once the intent is stamped.</summary>
    public const string Relay = "relay";

    public static IReadOnlyList<string> All { get; } = [OrderRecord, Contact, Relay];
}
```

`Observability/INotificationStats.cs`:

```csharp
namespace Notifications.Infrastructure.Observability;

/// <summary>The questions <see cref="NotificationMetrics"/>' gauges ask of the notification log.</summary>
public interface INotificationStats
{
    /// <summary>Pending notices past their first backoff, for every step in <see cref="WaitingSteps.All"/>.</summary>
    IReadOnlyDictionary<string, int> WaitingByStep();

    /// <summary>How long the longest-due claimable notice has waited past two ticks; zero when none has.</summary>
    double OverdueSeconds();
}
```

`Observability/NotificationStats.cs`:

```csharp
using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Delivery;
using Notifications.Infrastructure.Mail;

namespace Notifications.Infrastructure.Observability;

/// <summary><see cref="INotificationStats"/>, cached briefly in <c>ShipmentStats</c>' shape.</summary>
/// <remarks>It throws; <see cref="NotificationMetrics"/> contains that into an absent series (§13.6).</remarks>
internal sealed class NotificationStats(IDbConnectionFactory connections) : INotificationStats, IDisposable
{
    /// <summary>The open a command timeout does not cover, as the connection's <c>ConnectTimeout</c>.</summary>
    public const int ConnectTimeoutSeconds = 2;

    /// <summary>Not SqlClient's thirty: in a gauge callback, a black-holed database would stall the reader.</summary>
    private const int CommandTimeoutSeconds = 2;

    /// <summary>Inside one export interval, so a repeat callback within it reuses the result.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    /// <summary>Two ticks: a due row a healthy pass has not reached yet reads as no wait.</summary>
    internal static readonly TimeSpan OverdueGrace = MailHop.SendTick * 2;

    // The intent first, then the record, then everything else before the intent, which is the contact's step.
    private static readonly string WaitingSql =
        $"""
        SELECT Step, COUNT(*) AS Waiting
        FROM (
            SELECT CASE
                WHEN n.SendStartedAt IS NOT NULL THEN '{WaitingSteps.Relay}'
                WHEN r.OrderId IS NULL
                    OR (n.TemplateKey = '{TemplateKeys.PaymentDeclined}' AND r.CancelledAt IS NULL)
                    THEN '{WaitingSteps.OrderRecord}'
                ELSE '{WaitingSteps.Contact}'
            END AS Step
            FROM notifications.NotificationLog n
            LEFT JOIN notifications.OrderRecords r ON r.OrderId = n.OrderId
            WHERE n.Status = 'Pending' AND n.Attempts > 0
        ) waiting
        GROUP BY Step;
        """;

    // The claim's own population, aged from when each row became due; NULL is no wait.
    private static readonly string OverdueSql =
        $"""
        SELECT DATEDIFF_BIG(millisecond, MIN(NextAttemptAt), SYSDATETIMEOFFSET()) / 1000.0
        FROM notifications.NotificationLog
        WHERE {SendClaims.Claimable};
        """;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public IReadOnlyDictionary<string, int> WaitingByStep() =>
        _cache.GetOrCreate(nameof(WaitingByStep), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            Dictionary<string, int> waiting = WaitingSteps.All.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
            foreach (WaitingRow row in connection.Query<WaitingRow>(
                         new CommandDefinition(WaitingSql, commandTimeout: CommandTimeoutSeconds)))
            {
                waiting[row.Step] = row.Waiting;
            }

            return waiting;
        })!;

    // Floored, as host-stamped instants meet the engine's clock, and less the grace a healthy pass needs.
    public double OverdueSeconds() =>
        _cache.GetOrCreate(nameof(OverdueSeconds), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            using IDbConnection connection = connections.Create();

            double? waited = connection.ExecuteScalar<double?>(
                new CommandDefinition(OverdueSql, commandTimeout: CommandTimeoutSeconds));

            return Math.Max(0, (waited ?? 0) - OverdueGrace.TotalSeconds);
        });

    public void Dispose() => _cache.Dispose();

    private sealed record WaitingRow(string Step, int Waiting);
}
```

- [ ] **Step 5: The gauges**

`Observability/NotificationMetrics.cs` becomes:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Notifications.Infrastructure.Observability;

/// <summary>The send worker's instruments, on <see cref="OutboundMeter.Name"/>, which §13.2 collects.</summary>
/// <remarks><c>ShipmentMetrics</c>' gauges for one pass: the waiting by step, and the overdue (§13.6).</remarks>
public sealed class NotificationMetrics
{
    // CA1848 (ADR-019), as ShipmentMetrics does.
    private static readonly Action<ILogger, Exception?> WaitingReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(WaitingReadFailed)),
            "Waiting-notification gauge read failed; this collection omits every step rather than reporting some.");

    private static readonly Action<ILogger, Exception?> OverdueReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(OverdueReadFailed)),
            "Overdue-notification gauge read failed; this collection omits it rather than reporting no wait.");

    private readonly Counter<long> _resent;

    public NotificationMetrics(IMeterFactory factory, INotificationStats stats, ILogger<NotificationMetrics> logger)
    {
        Meter meter = factory.Create(OutboundMeter.Name);

        // The record a possible duplicate leaves: the row holds the intent, and this counts each send over one.
        _resent = meter.CreateCounter<long>(
            "notifications.mail.resent",
            unit: "{send}",
            description: "Sends started over an intent already stamped, each one a message the relay may hold twice.");

        // Past a first backoff, by what the row waits on; a relay step rising during an outage is the breaker working.
        meter.CreateObservableGauge(
            "notifications.waiting",
            () => PerStep(stats, logger),
            unit: "{notification}",
            description: "Pending notifications past their first failed pass, by the step they wait on.");

        // The row no pass has reached, which the gauge above cannot see; one that climbs is too few replicas.
        meter.CreateObservableGauge(
            "notifications.overdue",
            () => Overdue(stats, logger),
            unit: "s",
            description: "How long the longest-due notification has waited unclaimed past two ticks.");
    }

    public void Resent() => _resent.Add(1);

    /// <summary>Every step or none: one missing from a <c>max by (step)</c> reads as a healthy zero.</summary>
    /// <remarks>Contained, since the collector abandons its pass on an exception (§13.6).</remarks>
    private static List<Measurement<double>> PerStep(INotificationStats stats, ILogger logger)
    {
        IReadOnlyDictionary<string, int> waiting;

        try
        {
            waiting = stats.WaitingByStep();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WaitingReadFailed(logger, exception);
            return [];
        }

        return
        [
            .. WaitingSteps.All.Select(step => new Measurement<double>(
                waiting.TryGetValue(step, out int count) ? count : 0,
                new KeyValuePair<string, object?>("step", step)))
        ];
    }

    private static List<Measurement<double>> Overdue(INotificationStats stats, ILogger logger)
    {
        try
        {
            return [new Measurement<double>(stats.OverdueSeconds())];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OverdueReadFailed(logger, exception);
            return [];
        }
    }
}
```

- [ ] **Step 6: The reader's registration**

`DependencyInjection.cs`, directly before Task 7's
`services.AddSingleton<NotificationMetrics>();`, with
`using Microsoft.Data.SqlClient;` in sorted position where absent:

```csharp
        // The gauges' reader on the runtime key (§7.1), with a bounded connect timeout no query inherits, since a
        // callback that waits stalls every other one; through a factory, so the container disposes what it built.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Notifications"))
            {
                ConnectTimeout = NotificationStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<INotificationStats>(
            _ => new NotificationStats(new SqlConnectionFactory(metricsConnectionString)));
```

and the comment over `AddSingleton<NotificationMetrics>()` becomes:

```csharp
        // The send worker's counter and gauges, a singleton so one meter holds one set (§13.6).
```

- [ ] **Step 7: Run the suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NotificationGaugeTests|FullyQualifiedName~MetricsRegistrationTests|FullyQualifiedName~SendWorkerTests"
py -3.12 deploy/observability/check.py
```

Expected: 0 warnings; green, Docker running; `check.py` exits 0 — check 6
now declares `notifications_waiting`, `notifications_overdue_seconds` and
`notifications_mail_resent_total` from the three literal names, which no
panel reads until PR-6, and check 8 still finds no dispatcher registered in
the service.

- [ ] **Step 8: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.Worker.Tests
git commit -m "feat(notifications): notifications.waiting by step and notifications.overdue past two ticks, on Notifications.Outbound"
```

The body says how a step is read from the tables, why the overdue reading
subtracts two ticks, and the series names PR-6's dashboard reads.

---

### Task 10: The retention pass

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Retention/NotificationsRetentionService.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs` — the hosted pass
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` — the hosted pass's removal
- Modify: `tests/Notifications.TestSupport/ServiceFixture.cs` — `PurgeNotificationsRetentionAsync`
- Test: `tests/Notifications.Worker.Tests/NotificationsRetentionTests.cs`

**Interfaces:**
- Consumes: PR-4's `NotificationsJurisdictionOptions` (`LogRetention`,
  `ContactRetention`, `OrderRetention`) and the factory's invented windows
  (1013, 17 and 71 days); PR-1's `IDbConnectionFactory`; Task 5's indexes.
- Produces: `NotificationsRetentionService` with `Interval`, `BatchSize`,
  `MaxBatchesPerPass` and
  `Task<(int Notifications, int Contacts, int Orders)> PurgeAsync(CancellationToken)`;
  `ServiceFixture.PurgeNotificationsRetentionAsync()`.

**`ShippingRetentionService`' shape, with three windows and one floor.** A
hosted service of its own beside `RetentionPurgeService`, because ADR-053's
windows are statutory and §9.5's are housekeeping, and an hourly delete
measured in days must not share a tick with a five-second claim. Each window
selects keys in bounded batches, then deletes by key, so a delete locks
nothing it did not select:

| Window | Deletes | From |
|---|---|---|
| `LogRetention` | a terminal `NotificationLog` row | its `CompletedAt` |
| `OrderRetention` | an `OrderRecords` row **no `Pending` notice names** | its `RecordedAt` |
| `ContactRetention` | a `ContactRecords` row | its `FetchedAt`, the last refresh |

**`OrderRetention`'s floor is asked twice.** Spec section 6: an order record
deleted while a notice still waits on it turns that notice into a give-up. The
select asks whether a `Pending` row names the order, and so does the delete,
because a consumer can write a pending notice for an old order between the
two — a late `ShipmentDelivered` — and the second asking is what keeps its
record. The contact delete asks its window again for the same reason: a pass
may have refreshed the row since the select.

**A pending notice is never purged**, whatever its age: `LogRetention`
reaches terminal rows alone, and a pending row ends at
`DeliveryOptions.GiveUpAge`, which Task 4 holds far below any window ADR-053's
bounds admit.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Worker.Tests/NotificationsRetentionTests.cs`:

```csharp
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.Infrastructure.Retention;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-053's three windows against their tables, under the fixture's invented deployment.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class NotificationsRetentionTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_terminal_notice_goes_past_the_log_window_and_a_pending_one_of_any_age_stays()
    {
        Notification old = await EndedAsync(TimeSpan.FromDays(1014));
        Notification inside = await EndedAsync(TimeSpan.FromDays(1012));
        Notification pending = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await fixture.AgeAsync(pending.NotificationId, TimeSpan.FromDays(2000));

        (int notifications, _, _) = await fixture.PurgeNotificationsRetentionAsync();

        notifications.ShouldBe(1, "1013 days is the invented log window");
        (await CountAsync("NotificationLog", "NotificationId", old.NotificationId)).ShouldBe(0);
        (await CountAsync("NotificationLog", "NotificationId", inside.NotificationId)).ShouldBe(1);
        (await CountAsync("NotificationLog", "NotificationId", pending.NotificationId))
            .ShouldBe(1, "a pending notice ends at its give-up age, never at a window");
    }

    [Fact]
    public async Task A_contact_not_refreshed_for_its_window_goes_and_a_fresher_one_stays()
    {
        Guid stale = Guid.CreateVersion7();
        Guid fresher = Guid.CreateVersion7();
        await fixture.StageContactAsync(stale, "aigerim@example.test", null, TimeSpan.FromDays(18));
        await fixture.StageContactAsync(fresher, "dana@example.test", "kk", TimeSpan.FromDays(16));

        (_, int contacts, _) = await fixture.PurgeNotificationsRetentionAsync();

        contacts.ShouldBe(1, "17 days is the invented contact window");
        (await fixture.ContactAsync(stale)).ShouldBeNull();
        (await fixture.ContactAsync(fresher)).ShouldNotBeNull();
    }

    [Fact]
    public async Task An_order_record_past_its_window_goes_only_once_no_pending_notice_names_its_order()
    {
        Guid unnamed = await AgedOrderAsync(TimeSpan.FromDays(72));
        Guid waitedOn = await AgedOrderAsync(TimeSpan.FromDays(72));
        Notification waiting = await fixture.PendingAsync(TemplateKeys.ShipmentDelivered, waitedOn);
        Guid finished = await AgedOrderAsync(TimeSpan.FromDays(72));
        await EndAsync(await fixture.PendingAsync(TemplateKeys.OrderPlaced, finished), TimeSpan.FromDays(1));
        Guid inside = await AgedOrderAsync(TimeSpan.FromDays(70));

        (_, _, int orders) = await fixture.PurgeNotificationsRetentionAsync();

        orders.ShouldBe(2);
        (await CountAsync("OrderRecords", "OrderId", unnamed)).ShouldBe(0);
        (await CountAsync("OrderRecords", "OrderId", finished)).ShouldBe(0, "a terminal notice waits on nothing");
        (await CountAsync("OrderRecords", "OrderId", waitedOn))
            .ShouldBe(1, "deleting it would turn the waiting notice into a give-up");
        (await CountAsync("OrderRecords", "OrderId", inside)).ShouldBe(1, "71 days is the invented order window");

        await EndAsync(waiting, TimeSpan.FromMinutes(1));
        (_, _, int later) = await fixture.PurgeNotificationsRetentionAsync();

        later.ShouldBe(1, "once nothing waits on it, the record goes on its window");
        (await CountAsync("OrderRecords", "OrderId", waitedOn)).ShouldBe(0);
    }

    [Fact]
    public async Task A_backlog_larger_than_one_batch_drains_within_the_pass()
    {
        const int backlog = NotificationsRetentionService.BatchSize + 100;
        await fixture.ExecuteAsync(
            """
            INSERT INTO notifications.ContactRecords (CustomerId, Email, Locale, FetchedAt)
            SELECT TOP ({0}) NEWID(), N'someone@example.test', NULL, DATEADD(day, -18, SYSDATETIMEOFFSET())
            FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            """,
            backlog);

        (_, int contacts, _) = await fixture.PurgeNotificationsRetentionAsync();

        contacts.ShouldBe(backlog);
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM notifications.ContactRecords")).ShouldBe(0);
    }

    [Fact]
    public async Task No_line_of_the_purge_holds_a_mailbox()
    {
        await fixture.StageContactAsync(
            Guid.CreateVersion7(), "aigerim.private@example.test", "kk", TimeSpan.FromDays(18));

        await fixture.PurgeNotificationsRetentionAsync();

        fixture.CapturedLogs.Everything.ShouldContain(
            line => line.Contains("Notifications retention deleted", StringComparison.Ordinal),
            "the pass's own line must be in the capture, or the absence below proves nothing");
        fixture.CapturedLogs.Everything.ShouldNotContain(
            line => line.Contains("aigerim.private", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Notification> EndedAsync(TimeSpan ago)
    {
        Notification notification = await fixture.PendingAsync(TemplateKeys.OrderPlaced, Guid.CreateVersion7());
        await EndAsync(notification, ago);

        return notification;
    }

    private Task EndAsync(Notification notification, TimeSpan ago) =>
        fixture.ExecuteAsync(
            "UPDATE notifications.NotificationLog SET Status = 'Sent', " +
            "CompletedAt = DATEADD(minute, -{1}, SYSDATETIMEOFFSET()) WHERE NotificationId = {0};",
            notification.NotificationId,
            (int)ago.TotalMinutes);

    private async Task<Guid> AgedOrderAsync(TimeSpan ago)
    {
        Guid order = Guid.CreateVersion7();
        await fixture.OrderAsync(order, Guid.CreateVersion7());
        await fixture.ExecuteAsync(
            "UPDATE notifications.OrderRecords SET RecordedAt = DATEADD(minute, -{1}, SYSDATETIMEOFFSET()) " +
            "WHERE OrderId = {0};",
            order,
            (int)ago.TotalMinutes);

        return order;
    }

    private Task<int> CountAsync(string table, string key, Guid id) =>
        fixture.ScalarAsync<int>($"SELECT Value = COUNT(*) FROM notifications.{table} WHERE {key} = {{0}}", id);
}
```

`ScalarAsync`'s first argument interpolates two names this file wrote, never a
value: `SqlQueryRaw` takes the text as SQL, and the id stays a parameter.
`EndAsync` and `AgedOrderAsync` work in minutes so 1014 days stays inside
`DATEADD`'s `int`. The backlog's `INSERT … SELECT TOP` writes six hundred rows
in one statement, as `StageContactAsync` six hundred times would take a
minute to say the same thing.

- [ ] **Step 2: Run them to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NotificationsRetentionTests"
```

Expected: compile failure on `NotificationsRetentionService` and
`PurgeNotificationsRetentionAsync`.

- [ ] **Step 3: The pass**

`Retention/NotificationsRetentionService.cs`:

```csharp
using System.Data;
using Common.Application;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Jurisdiction;

namespace Notifications.Infrastructure.Retention;

/// <summary>ADR-053's three windows, applied: the record of a send, the order record and the contact.</summary>
/// <remarks>
/// Apart from <c>RetentionPurgeService</c>, whose windows are housekeeping (ADR-053). An order record goes only once
/// no pending notice names its order, since that notice waits on it.
/// </remarks>
public sealed class NotificationsRetentionService : BackgroundService
{
    /// <summary>Slow on purpose: a window is measured in days, and a pass competes with the claims.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Under SQL Server's 2,100-parameter ceiling, as the delete carries one parameter per key.</summary>
    public const int BatchSize = 500;

    /// <summary>Batches per table per pass, so a backlog drains and a pass still ends.</summary>
    public const int MaxBatchesPerPass = 20;

    private const string EndedNotices =
        """
        SELECT TOP (@BatchSize) NotificationId
        FROM notifications.NotificationLog
        WHERE CompletedAt IS NOT NULL AND CompletedAt < @Before AND Status <> 'Pending'
        ORDER BY CompletedAt;
        """;

    private const string DeleteNotices =
        """
        DELETE FROM notifications.NotificationLog
        WHERE NotificationId IN @Keys AND CompletedAt < @Before AND Status <> 'Pending';
        """;

    private const string ExpiredOrders =
        """
        SELECT TOP (@BatchSize) r.OrderId
        FROM notifications.OrderRecords r
        WHERE r.RecordedAt < @Before
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog n WHERE n.OrderId = r.OrderId AND n.Status = 'Pending')
        ORDER BY r.RecordedAt;
        """;

    // Asked again, so a notice a consumer wrote since the select keeps the record it will wait on.
    private const string DeleteOrders =
        """
        DELETE r FROM notifications.OrderRecords r
        WHERE r.OrderId IN @Keys
            AND r.RecordedAt < @Before
            AND NOT EXISTS (
                SELECT 1 FROM notifications.NotificationLog n WHERE n.OrderId = r.OrderId AND n.Status = 'Pending');
        """;

    private const string ExpiredContacts =
        """
        SELECT TOP (@BatchSize) CustomerId
        FROM notifications.ContactRecords
        WHERE FetchedAt < @Before
        ORDER BY FetchedAt;
        """;

    // Asked again, so a row a pass refreshed since the select is kept.
    private const string DeleteContacts =
        "DELETE FROM notifications.ContactRecords WHERE CustomerId IN @Keys AND FetchedAt < @Before;";

    private static readonly Action<ILogger, int, string, Exception?> Purged =
        LoggerMessage.Define<int, string>(
            LogLevel.Information,
            new EventId(1, nameof(Purged)),
            "Notifications retention deleted {Rows} row(s) from {Table}.");

    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(2, nameof(PurgeFailed)),
            "Notifications retention failed; retrying next pass.");

    private readonly IServiceScopeFactory _scopes;
    private readonly NotificationsJurisdictionOptions _windows;
    private readonly ILogger<NotificationsRetentionService> _log;

    public NotificationsRetentionService(
        IServiceScopeFactory scopes,
        IOptions<NotificationsJurisdictionOptions> windows,
        ILogger<NotificationsRetentionService> log)
    {
        _scopes = scopes;
        _windows = windows.Value;
        _log = log;
    }

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);

        // A pass at start, or a deployment restarting more often than Interval would never apply ADR-053's windows.
        do
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Swallowed, since an exception out of ExecuteAsync stops the host; the token, for §9.4's reason.
                PurgeFailed(_log, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over the three tables, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<(int Notifications, int Contacts, int Orders)> PurgeAsync(CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // The registered clock rather than DateTimeOffset.UtcNow, for §9.5's reason: a test host substitutes it.
        DateTimeOffset now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        // Validated at start (ADR-053), so each window is present here.
        int notifications = await PurgeWindowAsync(
            connection, EndedNotices, DeleteNotices, now - _windows.LogRetention!.Value, ct);
        Purged(_log, notifications, "the notification log", null);

        int orders = await PurgeWindowAsync(
            connection, ExpiredOrders, DeleteOrders, now - _windows.OrderRetention!.Value, ct);
        Purged(_log, orders, "order records", null);

        int contacts = await PurgeWindowAsync(
            connection, ExpiredContacts, DeleteContacts, now - _windows.ContactRetention!.Value, ct);
        Purged(_log, contacts, "contact records", null);

        return (notifications, contacts, orders);
    }

    /// <summary>Selected, then deleted by key, so a delete locks nothing the select did not choose.</summary>
    private static async Task<int> PurgeWindowAsync(
        IDbConnection connection,
        string select,
        string delete,
        DateTimeOffset before,
        CancellationToken ct)
    {
        int total = 0;

        for (int batch = 0; batch < MaxBatchesPerPass; batch++)
        {
            Guid[] keys =
            [
                .. await connection.QueryAsync<Guid>(
                    new CommandDefinition(select, new { BatchSize, Before = before }, cancellationToken: ct))
            ];

            if (keys.Length == 0)
                break;

            total += await connection.ExecuteAsync(
                new CommandDefinition(delete, new { Keys = keys, Before = before }, cancellationToken: ct));

            if (keys.Length < BatchSize)
                break;
        }

        return total;
    }
}
```

- [ ] **Step 4: The registration, the factory and the fixture**

`DependencyInjection.cs`, after Task 7's `AddHostedService<SendWorker>()` and
before `AddHostedService<RetentionPurgeService>()`, with
`using Notifications.Infrastructure.Retention;` in sorted position:

```csharp
        // ADR-053's three windows, applied by a pass of its own; by implementation type, which §12.4's fixture
        // removes it by, so its start-up pass never races a seed.
        services.AddHostedService<NotificationsRetentionService>();
```

`tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`, after Task
7's `SendWorker` block, with `using Notifications.Infrastructure.Retention;`:

```csharp
                // The statutory windows' pass, by the same match and for the same reason.
                ServiceDescriptor retention = services.Single(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(NotificationsRetentionService));
                services.Remove(retention);

                services.AddSingleton<NotificationsRetentionService>();
```

`tests/Notifications.TestSupport/ServiceFixture.cs`:

```csharp
    /// <summary>Runs exactly one pass of ADR-053's three windows, with no timer.</summary>
    public Task<(int Notifications, int Contacts, int Orders)> PurgeNotificationsRetentionAsync() =>
        Factory.Services
            .GetRequiredService<NotificationsRetentionService>()
            .PurgeAsync(TestContext.Current.CancellationToken);
```

- [ ] **Step 5: Run the suites**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~NotificationsRetentionTests|FullyQualifiedName~RetentionPurgeTests"
```

Expected: 0 warnings; green. Prove the floor by mutation: delete the
`NOT EXISTS` clause from `ExpiredOrders` and `DeleteOrders`, and see
`An_order_record_past_its_window_goes_only_once_no_pending_notice_names_its_order`
fail on the waited-on record; restore. Delete it from `DeleteOrders` alone and
the test stays green — the second asking guards a race this suite does not
stage, which the statement's own comment states rather than a test pretending
to.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "feat(notifications): NotificationsRetentionService applies the log, order and contact windows, keeping a record a pending notice waits on"
```

The body argues the floor, why it is asked twice, and why a pending notice is
never a window's to delete.

---

### Task 11: An order's journey to the relay, and the made-up deployment sends

**Files:**
- Test: `tests/Notifications.Worker.Tests/OrderJourneyTests.cs`
- Modify: `tests/Notifications.Worker.Tests/MadeUpDeploymentTests.cs` — the relay reset, and a send of all seven

**Interfaces:**
- Consumes: Tasks 6 and 7; PR-4's `OrderEvents`, `DeliverAsync`,
  `MadeUpDeploymentTests`, `TemplateSet.Embedded.Reasons`.

**The order journey, where the spec puts it.** Section 13 puts it in PR-5's
worker suite and "Not in `Platform.IntegrationTests`", on §12.1's and §12.6's
grounds. So it is proved here: the four events, built as their publishers'
contracts carry them, sent to the real `notifications-events` queue under the
narrow account, consumed by the real consumers, sent by the real worker to
the real relay — four `Sent` rows and four messages, and a cancelled order's
cancellation with its reason's phrase. The publishers' half — that Ordering, Payments and
Shipping publish these shapes — is each publisher's own suite and §12.6's
contract tests, which is how §12 already divides it.

**The made-up deployment sends.** PR-4's `MadeUpDeploymentTests` rendered all
seven notices under the fixture's invented language set (`kk`, `en`) and zone
(`Pacific/Chatham`); spec section 13 asks that they *send* under it in PR-5.
The test delivers the seven, with a workflow cancellation so the decline is
sent (ADR-049), to a customer with no locale, and reads each message back:
both languages, Kazakh first, the date on Chatham's side of midnight.

- [ ] **Step 1: Write the tests**

`tests/Notifications.Worker.Tests/OrderJourneyTests.cs`:

```csharp
using Common.Contracts.Ordering.V1;
using Notifications.Application.Records;
using Notifications.Application.Rendering;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>An order's events, as their contracts carry them, through the queue and the worker to the relay.</summary>
/// <remarks>
/// The service's half of the journey (§3.2); that each publisher sends these shapes is its own suite's to hold, and
/// §12.6's contract tests between them.
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class OrderJourneyTests(ServiceFixture fixture) : IAsyncLifetime
{
    private const string Mailbox = "aigerim@example.test";

    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_placed_confirmed_despatched_and_delivered_is_four_sent_notices_and_four_messages()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, At.AddMinutes(1)));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, At.AddDays(1)));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, At.AddDays(2)));

        await fixture.SendUntilSettledAsync();

        IReadOnlyList<Notification> rows = await fixture.NotificationsAsync(order);
        rows.Select(n => n.TemplateKey).ShouldBe(
            [TemplateKeys.OrderPlaced, TemplateKeys.OrderConfirmed, TemplateKeys.ShipmentDispatched,
                TemplateKeys.ShipmentDelivered],
            ignoreOrder: true);
        rows.ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.CustomerId == customer);

        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(4, Ct);
        delivered.Select(m => m.MessageId.Trim('<', '>')).ShouldBe(
            rows.Select(n => $"{n.EventId:N}.{n.TemplateKey}@commerce.test"),
            ignoreOrder: true,
            "one message per event, each under its own row's Message-ID");
        delivered.ShouldAllBe(m => m.To.Single().Address == Mailbox);

        MailpitMessage despatch = await fixture.Relay.MessageAsync(
            delivered.Single(m => m.MessageId.Contains(TemplateKeys.ShipmentDispatched, StringComparison.Ordinal)).Id,
            Ct);
        despatch.Text.ShouldContain("ZZ-0042", Case.Sensitive, "the carrier's tracking number reaches the customer");
    }

    [Fact]
    public async Task A_cancelled_order_is_told_of_its_cancellation_and_why()
    {
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();
        fixture.ContactAnswers(customer, Mailbox, "en");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, At));
        await fixture.DeliverAsync(OrderEvents.Cancelled(
            order, customer, At.AddMinutes(5), CancelReasons.CustomerRequest, CancelOrigins.User));

        await fixture.SendUntilSettledAsync();

        (await fixture.NotificationsAsync(order)).ShouldAllBe(n => n.Status == NotificationStatus.Sent);
        IReadOnlyList<MailpitSummary> delivered = await fixture.Relay.WaitForAsync(2, Ct);
        MailpitMessage cancellation = await fixture.Relay.MessageAsync(
            delivered.Single(m => m.MessageId.Contains(TemplateKeys.OrderCancelled, StringComparison.Ordinal)).Id,
            Ct);

        string phrase = TemplateSet.Embedded.Reasons(1, "en")![CancelReasons.CustomerRequest];
        cancellation.Text.ShouldContain(phrase, Case.Sensitive, "a cancellation says why, in Ordering's codes");
    }
}
```

`tests/Notifications.Worker.Tests/MadeUpDeploymentTests.cs`: its
`InitializeAsync` becomes

```csharp
    public async ValueTask InitializeAsync() => await fixture.ResetWithRelayAsync();
```

and after `The_host_bound_the_invented_jurisdiction_and_not_a_default`:

```csharp
    [Fact]
    public async Task All_seven_notices_send_under_the_invented_deployment_in_its_languages_and_its_zone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        Guid order = Guid.CreateVersion7();
        Guid customer = Guid.CreateVersion7();

        // No locale, so each is owed every language of the set, in the set's order (ADR-053).
        fixture.ContactAnswers(customer, "aigerim@example.test");

        await fixture.DeliverAsync(OrderEvents.Placed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Confirmed(order, customer, LateInTheDay));
        await fixture.DeliverAsync(
            OrderEvents.Cancelled(order, customer, LateInTheDay, CancelReasons.OutOfStock, CancelOrigins.Workflow));
        await fixture.DeliverAsync(OrderEvents.Declined(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Refunded(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Dispatched(order, LateInTheDay));
        await fixture.DeliverAsync(OrderEvents.Delivered(order, LateInTheDay));

        await fixture.SendUntilSettledAsync();

        IReadOnlyList<Notification> rows = await fixture.NotificationsAsync(order);
        rows.Count.ShouldBe(7, "the workflow's cancellation sends the decline beside it (ADR-049)");
        rows.ShouldAllBe(n => n.Status == NotificationStatus.Sent && n.Languages == "kk,en");

        foreach (MailpitSummary summary in await fixture.Relay.WaitForAsync(7, ct))
        {
            summary.Subject.Split(TemplateRenderer.SubjectSeparator).Length.ShouldBe(2, summary.MessageId);
            (await fixture.Relay.HeadersAsync(summary.Id, ct))["Content-Language"]
                .ShouldHaveSingleItem().ShouldBe("kk, en", summary.MessageId);

            string[] bodies = (await fixture.Relay.MessageAsync(summary.Id, ct)).Text
                .ReplaceLineEndings("\n")
                .Split(TemplateRenderer.LanguageRule);
            bodies.Length.ShouldBe(2, summary.MessageId);
            bodies[0].ShouldContain("3 қазан", Case.Sensitive, $"{summary.MessageId}: Kazakh first, Chatham's day");
            bodies[1].ShouldContain("October 3, 2026", Case.Sensitive, $"{summary.MessageId}: English second");
        }
    }
```

`ReplaceLineEndings("\n")` because SMTP carries CRLF whatever the body's own
ending was (RFC 5321 section 2.3.8) and `LanguageRule` is PR-4's LF text; the
same reading PR-2's Kazakh round trip makes.

- [ ] **Step 2: Run them**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~OrderJourneyTests|FullyQualifiedName~MadeUpDeploymentTests"
```

Expected: green, Docker running. These exercise Tasks 4–10's code end to end
and are written after it; their red is by mutation — make
`SendRules.Suppresses` return `true` for every key and see the journey's
rows end `Suppressed` and Mailpit hold nothing; restore.

- [ ] **Step 3: Commit**

```bash
git add tests/Notifications.Worker.Tests
git commit -m "test(notifications): an order's four events reach the relay as four messages, a cancellation says why, and ZZ's deployment sends all seven"
```

The body says the journey sits in this suite, as the spec's section 13 places
it, and what it holds.

---

### Task 12: §15.4's `Delivery` row, and the options type it earned

**Files:**
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's inventory, its options-type paragraph and its callout

**Interfaces:**
- Consumes: Task 4's `DeliveryOptions` and Compose value.

**What moves, and what does not.** The spec's section 14 gives PR-5 two
places in §15.4: the inventory's give-up age row, and the sentence naming the
options types that earned one. The callout under that sentence argues
`Fulfilment`'s exception by name, and `Delivery` takes the same exception on
the same word, so the callout names it too rather than leave a second type
argued nowhere. Nothing else in the blueprint states the worker's numbers or
its gauges: §9.7's lists of third-party hops are PR-2's, §15.3's replica and
worker paragraphs and the runbook are PR-6's, and §13.2's `AddMeter` line
already collects `Notifications.Outbound`.

- [ ] **Step 1: The inventory**

`docs/backend-architecture/15-cicd-deployment.md`, §15.4's key table, after
the `Fulfilment__GiveUpAge` row:

```markdown
| `Delivery__GiveUpAge` | Config | Helm `delivery.giveUpAge` → ConfigMap, defaulted in the chart | ✓ — **Notifications only**; [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s give-up age for a notice still pending, past which it is undeliverable with the reason `gave_up`; the host refuses to start without it, or with one longer than `RetentionPolicy.InboxWindow` |
```

The Helm spelling is the one PR-6's chart renders, as Shipping's rows named
theirs before its chart existed; it is configuration, so `docs/secrets.md`
gains nothing.

- [ ] **Step 2: The paragraph and the callout**

In the paragraph that opens "**Every options type in the solution had to earn
it.**", the `Fulfilment` clause. Before — whatever PR-2's `Mail` clause and
PR-4's `Jurisdiction` clause left in front of it:

> and `Fulfilment` holds the give-up age
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> makes one — a deadline that decides whether a day's shipments survive a long
> outage, which is an operator's call and not a build's.

After:

> and `Fulfilment` and Notifications' `Delivery` each hold the give-up age
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> makes one — a deadline that decides whether a day's shipments, or a day's
> notices, survive a long outage, which is an operator's call and not a
> build's.

In the callout under it, before:

> `Fulfilment` passes
> on ADR-052's word rather than on this test: its age is the same everywhere
> until an outage makes an operator lengthen it, and that record makes the end
> of waiting a value of the deployment so that the change is a values edit and
> not a release.

After:

> `Fulfilment` and
> `Delivery` pass on ADR-052's word rather than on this test: each age is the
> same everywhere until an outage makes an operator lengthen it, and that
> record makes the end of waiting a value of the deployment so that the change
> is a values edit and not a release.

Both are rewrapped at 80 columns from the changed line to the end of their
sentence, and nothing else in either moves.

- [ ] **Step 3: Audit**

Run `/check-links` and then `/validate-blueprint`: `docs/change-locality.md`
owes the audit after a chapter edit, and a finding is fixed in this task's
commit. The audit's own frontmatter denies `src/` edits for the rest of its
turn, so it runs after every code task has committed.

- [ ] **Step 4: Commit**

```bash
git add docs/backend-architecture/15-cicd-deployment.md
git commit -m "docs(15.4): Delivery__GiveUpAge's row, and Delivery beside Fulfilment as the options types ADR-052 earns"
```

The body says the key is configuration, that its Helm spelling is the chart's
to render in PR-6, and why the callout moves with the sentence.

---

### Task 13: The platform up, everything run, and the PR

- [ ] **Step 1: Bring the platform up and watch a notice send**

```bash
docker compose -f deploy/compose/docker-compose.yml config --quiet
docker compose -f deploy/compose/docker-compose.yml up --build --wait
```

A RabbitMQ service on the host holding 5672 or 15672 refuses the broker's
published ports; bring the stack up with a scratchpad-only override that moves
them, never an edit to the committed files. Expected: `notifications-migrator`
exited 0, `notifications-worker` running, its log naming no options failure.
Place an order through the BFF as §14.1's walkthrough does, signed in as the
realm's `demo` user, then, within a tick or two:

```bash
curl -s http://127.0.0.1:8025/api/v1/messages
docker compose -f deploy/compose/docker-compose.yml exec sql sh -c \
    '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT TemplateKey, Status, Languages, TemplateVersion FROM Notifications.notifications.NotificationLog"'
```

Expected: Mailpit holds an `order-placed` message to `demo@example.invalid`
in Compose's three languages, `en`, `kk`, `ru`, since the realm keeps no
locale; the row reads `Sent`, `en,kk,ru`, `1`. That is the first notice the
platform has sent. Then stop Mailpit alone —
`docker compose -f deploy/compose/docker-compose.yml stop mailpit` — place a
second order, see its row stay `Pending` with `SendStartedAt` set and
`Attempts` climbing, start Mailpit again, and see it send. Then
`docker compose -f deploy/compose/docker-compose.yml down -v`.

- [ ] **Step 2: Build and test everything, then every gate**

```bash
dotnet build Platform.slnx
dotnet format Platform.slnx --verify-no-changes
dotnet test Platform.slnx
py -3.12 -m unittest discover -s deploy/compose/rabbitmq
py -3.12 deploy/compose/rabbitmq/check_permissions.py
py -3.12 deploy/observability/check.py
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
py -3.12 -m unittest discover -s .github/licence-gate
py -3.12 .github/licence-gate/licence_gate.py
py -3.12 -m unittest discover -s .github/locality-gate
py -3.12 -m unittest discover -s .github/comment-gate
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: 0 warnings, format exit 0, every suite green, every gate exit 0. A
gate with a suite is tested and then run, and none of these is in
`Platform.slnx`; the observability gate has no suite of its own, so it is
only run. The licence gate sees no new identity: the three package
references are to pins already registered. The comment gate judges `HEAD`, so
it runs after the last commit; every block this plan adds is five lines or
fewer, and the two touched configurations' blocks are judged whole.

**This plan's own text** carries no connection string and no credential; the
fixture's `Unreachable.Sql` is quoted by name. A plan file is the branch's that
wrote it, never this PR's to edit or allow-list.

- [ ] **Step 3: Open the PR**

The body carries `| Class | A+D+E |` and the touch-set row from *Global
Constraints* verbatim, with the reasons under the table, and:

- the dependency on PR-2, PR-3 and PR-4 by name, and the additions to their
  types (*Global Constraints*), each with its reason;
- the decisions the spec left to the plan: the unreadable payload backs off;
  a `transient` fault after the intent is counted as a resend; the overdue
  gauge subtracts two ticks; the step a row waits on is read from the tables;
- for PR-6's runbook half: a row whose parameters this version cannot read
  shows under `contact` on the waiting gauge, and its error line is its
  signal;
- the mutations Tasks 7, 8, 10 and 11 record.

Then `/ship`.

## Self-review

**Spec coverage.**

- Section 1, where the outbound calls sit: every call is the worker's (Task
  7), no consumer was touched. How a notification learns its customer: the
  order-record wait and `AssignCustomer` (Tasks 1, 7). What a decline tells a
  customer: ADR-049's suppression with the origin, absent and unknown origins
  read from the reason, `PaymentDeclined.Reason` never read (Task 1's table,
  Task 7's four worker cases). The locale's primary subtag: PR-4's renderer,
  driven through the worker in Task 7 (`en-GB` alone; none, every language).
- Section 3, PR-5's row: the claim and its lease, the order-record wait,
  ADR-049's suppression, ADR-052's five outcomes over PR-3's source, the
  render, the send with its `Message-ID`, the intent and completion stamps
  (Task 7); the give-up age under `DeliveryOptions` (Task 4, Task 7); the
  breaker's park (Tasks 3, 8); the retention pass (Task 10); the waiting
  gauge (Task 9); the order journey through the service's own queue (Task
  11).
- Section 4, whole: `UPDLOCK, READPAST, ROWLOCK` (Task 7's `SendClaims`); the
  lease above `MailHop`'s total plus `ContactHop`'s, held by a test (Task 7's
  `SendWorkerBudgetTests`); `Attempts` and `NextAttemptAt` apart from the
  lease (`BackOffSql`); the per-pass catch filtered on the token, and the
  loop's (Task 7); the drain (Task 7); two replicas overlapping (Task 7); the
  seven steps in order (Task 7's table); at-least-once with the window stated,
  the `Message-ID`, the staged crash with two deliveries, one `Message-ID`,
  one `Sent` row and the counter at one (Task 7); every row of the three-way
  table (Task 7's `RefusedAsync` and `Fault`, Task 8); the breaker parks the
  queue of rows (Tasks 3, 8); the give-up age covering every wait (Tasks 4, 7).
- Section 5: every move the worker makes is PR-1's, `not_a_mailbox` added to
  the closed set (Task 1); a move the row has outgrown is reported and changes
  nothing (Task 7's `Outgrown`).
- Section 6: the three windows applied, `OrderRetention`'s floor held by a
  test, a pending notice never purged (Task 10); the `InboxWindow` refusal
  (Task 4); the indexes the queries need (Task 5).
- Section 9's dependency table: Keycloak unreachable — stale served, expired
  backs off; Keycloak refusing — backs off, counted, never served stale; no
  such customer — terminal; the relay down — the breaker opens and no pass
  claims (Tasks 7, 8).
- Section 11: `Delivery__GiveUpAge`, a day in Compose (Task 4); the readiness
  set still SQL and the bus (PR-1's `HostSmokeTests`, run in Task 7).
- Section 12: `notifications.mail.resent` (Task 7), `notifications.waiting`
  by step and `notifications.overdue` in `shipping.shipments.overdue`'s form,
  exported as `notifications_overdue_seconds` (Task 9); no log line holds a
  mailbox or a body, the fault half by the `_error` queue (Task 8).
- Section 13's worker rows: the inequality and the lease (Task 7); two
  workers, a pass that throws, a lapsed lease (Task 7); a dependency dies
  (Task 8); the staged crash (Task 7); the log and fault export, no
  `Fault<T>` existing (Task 8); the CR or LF mailbox through a stub of the
  same answer (Task 7); the made-up deployment sends and the order journey
  (Task 11); the `InboxWindow` refusal (Task 4).
- Section 14: §15.4's row and sentence, and the callout with it (Task 12).

**Where this plan departs from the spec, each with the owner that decides.**

- One migration for five indexes, named for its content rather than a table
  (Task 5).

**Type consistency.** `NotificationReasons.NotAMailbox`, `SendRules`,
`ContactAge` (Task 1); `TemplateRenderer.Render(…, int, IReadOnlyList<string>)`
(Task 2); `MailPipeline(MailMetrics, TimeProvider)`, `IsOpen`, `ParkedUntil`
(Task 3); `DeliveryOptions`, `DeliveryOptionsValidator`, the factory's
`giveUpAge` and `InventedGiveUpAge` (Task 4); the five index names and
`AddSendAndRetentionIndexes` (Task 5); `CapturedLogs`, `SentCommitFaults`,
`CommitFault`, `Mailpit.PlainOn`, `StopAsync`, `WaitForAsync`, `MessageAsync`,
and the fixture's members (Task 6); `INotificationRepository.GetAsync`,
`SendWork`, `SendPass`, `SendClaims`, `SendWorker` with `ClaimBatchSize`,
`LeaseSeconds`, `DrainBudget`, `CommitRoom` and `RunOnceAsync`,
`NotificationMetrics.Resent`, `ResentCounter`, `RunSendPassAsync`,
`WaitUntilDueAsync`, `SendUntilSettledAsync` (Task 7); `WaitingSteps`,
`INotificationStats`, `NotificationStats` with `ConnectTimeoutSeconds` and
`OverdueGrace` (Task 9); `NotificationsRetentionService` and
`PurgeNotificationsRetentionAsync` (Task 10). PR-6 consumes `DeliveryOptions`
bound from `Delivery` with `GiveUpAge`, the series `notifications_waiting` with
`step` ∈ `order_record`, `contact`, `relay`, and
`notifications_overdue_seconds` with no attribute, under these spellings.

**Deliberately left.** §11.7's erasure consumer, which marks a pending notice
`Undeliverable: erased` and replaces the customer's id — owed with that
extension (spec, section 6). ADR-053 rule 3's relay country — owed with the
first real relay (spec, section 9). The chart, its `delivery` capability and
the runbook's half — PR-6's. A rule over the waiting gauge — the spec's own
argument that a growing set during an outage is the breaker working.

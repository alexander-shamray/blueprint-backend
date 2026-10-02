# Notifications PR-2 — the mail channel and the Compose relay — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Notifications the one port everything that leaves the service
by mail goes through, a MailKit adapter that is the only code knowing SMTP,
the budget and breaker that hop runs under, the options that refuse a plain or
unauthenticated relay outside Development, and Mailpit as the relay Compose
runs and the suite starts. Nothing calls the port yet.

**Architecture:** `IMailChannel` in `Notifications.Application` speaks the
domain's vocabulary: a send is `MailResult.Accepted` or
`MailResult.Refused(MailRefusal)`, and every fault is a
`MailUnavailableException` carrying a `MailFault` and the relay's reply code,
never its words. `SmtpMailChannel` in `Notifications.Infrastructure.Mail` opens
one MailKit `SmtpClient` per attempt inside a Polly `ResiliencePipeline` built
by hand — total timeout, retry, circuit breaker, attempt timeout, the order
`AddStandardResilienceHandler` uses — because SMTP is no `HttpClient`. The
retry repeats only an attempt the relay never took the message in, which is
§9.7's fourth rule applied to a send that has no idempotency key: a `4xx`, a
timeout or a refused connection before the data is handed over is
`transient` and retried; a break after it is `unconfirmed` and is not; `tls`,
`credential` and `rejected` are decisions, and are not either. Every header
is configuration, a checked value or the clock; the body is
`text/plain; charset=utf-8`; the `Message-ID` is the row's event id and
template key under the domain of `Mail:From`.

**Tech Stack:** MailKit and MimeKit (new pins, MIT), `Polly.Core` (a new direct
pin at the version the graph already resolves, BSD-3),
`System.Diagnostics.Metrics`, Testcontainers' generic `ContainerBuilder` over
`axllent/mailpit`.

**Spec:** `docs/superpowers/specs/2026-10-02-notifications-service-design.md`,
sections 1 (the channel, MailKit, Mailpit, the content type, the languages), 2
(§14.1, §15.4 and Appendix B move), 3 (PR-2's row), 4 (the three-way table,
`MailHop`, the breaker, the tick, the `Message-ID`), 8 (no interpolated value
in a header, the CR/LF mailbox), 9 (the mail port, `MailOptions`, the
certificate rule, the credential's places), 11 (the six `Mail__*` keys), 12
(the `Notifications.Outbound` meter and `notifications.mail.unavailable` with
its `cause` attribute), 13
(the mail adapter's suite, the Kazakh-script round trip, the TLS refusal, the
inequality, the opened circuit) and 14 (§14.1, §12.7, §9.7, Appendix B and
§15.4 in PR-2).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Notifications/**`,
  `tests/Notifications.*`, `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`,
  `tests/Common.Web.Tests/ObservabilityTests.cs`, `Directory.Packages.props`,
  `deploy/compose/services/notifications.yml`, `.github/secret-scan/allowed/tests.txt`,
  `docs/backend-architecture/09-messaging.md`,
  `docs/backend-architecture/12-test-strategy.md`,
  `docs/backend-architecture/14-local-development.md`,
  `docs/backend-architecture/15-cicd-deployment.md`,
  `docs/backend-architecture/appendix-b-licences.md`, `docs/secrets.md`

  Why each: the service's own code and tests are A, and so are `Common.Web`'s
  one `AddMeter` line and its test, under A's `src/BuildingBlocks/**` and
  `tests/**`; the three pins, the four package references in
  `Notifications.Infrastructure.csproj` (none with a `Version=`) and Appendix
  B's rows are E; the Compose unit, the allow-list entry and the five
  documents are D. The relay is a service in Notifications' own unit and
  mounts nothing, so there is no `deploy/compose/mailpit/` directory and no
  `.gitattributes` line for one.
  `.github/locality-gate/locality_gate.py` admits `A+D+E`, the one
  three-member cell the class row accepts, so the row is spelled exactly that
  way and carries no prose.
- **Depends on PR-1 having merged.** These names are PR-1's, rendered from the
  scaffold's pure-consumer mode, and this plan consumes them as spelled here:
  `src/Services/Notifications/Notifications.Application`,
  `Notifications.Infrastructure`, `Notifications.Migrator` and
  `Notifications.Worker`; `tests/Notifications.Application.Tests`,
  `tests/Notifications.Worker.Tests` and `tests/Notifications.TestSupport`;
  `AddNotificationsApplication`, which registers `TimeProvider.System` as
  every rendered service's does; `AddNotificationsInfrastructure(IServiceCollection, IConfiguration)`
  in `Notifications.Infrastructure.DependencyInjection`;
  `Notifications.Infrastructure.Observability.MetricsInitialiser` with the
  outbox parameter stripped, so `(MessagingMetrics messaging, RequestMetrics requests)`;
  `NotificationsWorkerFactory(string connectionString, string rabbitConnectionString)`
  over the worker's `Program`, with `UnreachableAuthority`;
  `MetricsRegistrationTests` with its `BuildServices()` and its selector test;
  `HostSmokeTests.Ready_probe_reports_the_sql_and_bus_checks`, which already
  pins readiness to `sql` and `masstransit-bus` by name and so holds the relay
  out of it; Application's `ArchitectureTests`; and
  `deploy/compose/services/notifications.yml` with `notifications-migrator`
  and `notifications-worker` running as Development. Where PR-1 spelled one
  of these differently, the spelling moves and nothing else in this plan
  does.
- **PR-3 may land first, in either order, and five things are shared.**
  `Notifications.Infrastructure/Observability/OutboundMeter.cs`, the
  `.AddMeter("Notifications.Outbound")` line in `ObservabilityExtensions` with
  its `ObservabilityTests` entry, `tests/Notifications.Worker.Tests/Unreachable.cs`,
  `tests/Notifications.Worker.Tests/RepositoryRoot.cs` and the
  `Microsoft.Extensions.Hosting.Abstractions` reference in
  `Notifications.Infrastructure.csproj` are each written by whichever of PR-2
  and PR-3 lands first, with the same text in both plans; the second finds the
  file or line present, adds nothing, and its PR body says so. That is why the
  meter's name lives in a class of its own rather than on `MailMetrics`, as
  `Shipping.Outbound` lives on `CarrierMetrics`: neither adapter is the
  other's owner. Three edits are additive in either order and written so:
  `MetricsInitialiser` gains `MailMetrics` after PR-1's parameters and before
  or after PR-3's `ContactMetrics`, whichever is there; `Program.cs` holds
  one order whichever lands first — `AddNotificationsInfrastructure`, then
  `AddMailChannel`, then PR-3's `AddContactSource`; and
  `MetricsRegistrationTests` gains its `TestEnvironment`
  and its `BuildServices` summary from whichever lands first, Shipping's
  forms in both.
- **Three new pins, and why `Polly.Core` is one of them.** `MailKit` and
  `MimeKit` at `4.18.1`, the current release, both MIT, neither carrying an
  advisory; MimeKit is MailKit's own dependency at exactly that version, and
  is pinned because the adapter names its types. `Polly.Core` at `8.4.2`,
  BSD-3-Clause: `Microsoft.Extensions.Http.Resilience` `10.0.0` reaches it
  through `Microsoft.Extensions.Resilience` `10.0.0` and `Polly.Extensions`
  and `Polly.RateLimiting` `8.4.2`, so `8.4.2` is what every resolved graph
  already holds. `Notifications.Infrastructure` references no HTTP package —
  its pipeline guards SMTP — so it reaches Polly only by a direct
  `PackageReference`, and central package management refuses one without a
  `PackageVersion`. Pinning the version already resolved moves no other
  project's graph, which Task 2 measures rather than assumes; with
  `CentralPackageTransitivePinningEnabled` a lower pin would be NU1109's
  downgrade and a higher one would silently move the BFF's, Payments' and
  Shipping's handlers. Appendix B gains a `Polly.Core` row, separate from the
  `Microsoft.Extensions.Http.Resilience` row because the licence differs.
- **No Testcontainers pin.** No `Testcontainers.Mailpit` module exists at the
  pinned `4.6.0` — the first is `4.14.0`, and moving four module pins that
  ship as one release is not this PR's — so the relay is a generic
  `ContainerBuilder`. Its types come from the `Testcontainers` core package,
  which the module packages carry transitively, and `Common.TestSupport` and
  `Web.Bff.Tests`' `KeycloakFixture` already name `DotNet.Testcontainers.*`
  that way with no pin of their own. This plan follows that, and adds none.
- **Mailpit is `axllent/mailpit:v1.31.3`**, the release of 2026-09-27, in the
  Compose unit and in `Notifications.TestSupport.Mailpit.Image`, and a test
  holds the two equal. Never `latest`. Both its ports are published on
  `127.0.0.1` alone — the UI because it shows every message to whoever
  reaches it, SMTP so §14.1's infrastructure-only workflow has a relay for a
  worker run on the host — and a test holds that too.
- **Four of `docs/secrets.md`'s five places for `Mail__Password`, and the
  fifth is PR-6's.** Compose carries no relay credential by design — Mailpit
  takes unauthenticated submission and the host allows that in Development
  alone — and says so in the unit; the Aspire host (§14.2) is not adopted;
  §15.4's row is Task 7's; the fixture is Task 3's
  `NotificationsWorkerFactory`; the chart's values cannot exist before PR-6's
  chart, which is where the spec puts the `mail` capability.
- Comments say why and cite the owner — a section, an ADR or a symbol, never
  a pull request, a test or the superpowers spec — a summary is one sentence,
  a `<remarks>` is cited and four lines, a block is five. Explicit local
  types, file-scoped namespaces, 120 columns, British spelling. `py -3.12`.
- Every step that adds behaviour writes its test first.

---

### Task 1: The mail port and its vocabulary

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Mail/IMailChannel.cs`
- Create: `src/Services/Notifications/Notifications.Application/Mail/OutboundMail.cs`
- Create: `src/Services/Notifications/Notifications.Application/Mail/MailMessageId.cs`
- Create: `src/Services/Notifications/Notifications.Application/Mail/MailResult.cs`
- Create: `src/Services/Notifications/Notifications.Application/Mail/MailRefusal.cs`
- Create: `src/Services/Notifications/Notifications.Application/Mail/MailFault.cs`
- Create: `src/Services/Notifications/Notifications.Application/Mail/MailUnavailableException.cs`
- Test: `tests/Notifications.Application.Tests/MailPortTests.cs`

**Interfaces:**
- Consumes: nothing of PR-1's beyond the project.
- Produces:

```csharp
namespace Notifications.Application.Mail;

public interface IMailChannel
{
    Task<MailResult> SendAsync(OutboundMail mail, CancellationToken ct);
}

public sealed record OutboundMail(
    string Recipient, string Subject, string Body, MailMessageId MessageId, IReadOnlyList<string> Languages);

public sealed record MailMessageId   // (Guid eventId, string templateKey), and LocalPart

public abstract record MailResult
{
    public sealed record Accepted : MailResult;
    public sealed record Refused(MailRefusal Reason) : MailResult;
}

public enum MailRefusal { RecipientRefused, NotAMailbox }

public enum MailFault { Transient, Unconfirmed, Tls, Credential, Rejected }

public sealed class MailUnavailableException : Exception   // + (string message, MailFault cause, int? smtpStatus)
```

`MailRefusal` is section 4's two reasons: `RecipientRefused`, a relay's
permanent `5xx` to the recipient, which PR-5 records as
`Undeliverable: recipient_refused`, and `NotAMailbox`, a contact mailbox that
does not parse or carries CR or LF, refused before any connection and
recorded as `Undeliverable: not_a_mailbox`. `MailMessageId` carries the event
id and the template key rather than a finished header, because the `@domain`
half is `Mail:From`'s and that is configuration the Application layer never
sees.

`MailFault` is section 4's five causes, and each member's lowercase name is
the `cause` attribute `notifications.mail.unavailable` carries: `transient`
— a `4xx`, a timeout or a refused connection before the message was handed
over — is retried in the client, and an open circuit is `transient` thrown
at once; `unconfirmed` — the connection broke after the message was handed
over and before the reply — is not retried, because the relay may hold it,
and the row's next pass resends it under the same `Message-ID`; `tls`,
`credential` and `rejected` — a session weaker than configured, a refused
credential, a permanent `5xx` about the sender or the message — are a
deployment's decisions, not retried, and logged by PR-5's worker as errors.
All five back the row off.

- [ ] **Step 1: Write the failing test**

`tests/Notifications.Application.Tests/MailPortTests.cs`:

```csharp
using Notifications.Application.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class MailPortTests
{
    [Fact]
    public void A_send_is_accepted_or_refused_and_nothing_else()
    {
        typeof(MailResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(MailResult)))
            .Select(t => t.Name)
            .ShouldBe(["Accepted", "Refused"], ignoreOrder: true,
                "a fault is an exception, so a dead relay can never reach a row as a refusal");
    }

    [Fact]
    public void A_refusal_is_the_relays_or_the_mailboxs_and_each_is_its_own_terminal_reason()
    {
        Enum.GetNames<MailRefusal>().ShouldBe(["RecipientRefused", "NotAMailbox"], ignoreOrder: true);
    }

    [Fact]
    public void A_fault_is_one_of_five_and_the_names_are_the_counters_vocabulary()
    {
        Enum.GetNames<MailFault>()
            .ShouldBe(["Transient", "Unconfirmed", "Tls", "Credential", "Rejected"], ignoreOrder: true);
    }

    [Fact]
    public void The_message_id_is_the_event_and_the_template_key()
    {
        Guid eventId = Guid.CreateVersion7();

        new MailMessageId(eventId, "order-placed").LocalPart.ShouldBe($"{eventId:N}.order-placed");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Order-Placed")]
    [InlineData("order placed")]
    [InlineData("order-placed\r\nBcc: someone@example.test")]
    [InlineData("order@placed")]
    [InlineData("-order")]
    [InlineData("order-")]
    public void A_template_key_that_is_not_kebab_case_never_reaches_a_header(string key)
    {
        Should.Throw<ArgumentException>(() => new MailMessageId(Guid.CreateVersion7(), key));
    }

    [Fact]
    public void An_empty_event_id_is_refused()
    {
        Should.Throw<ArgumentException>(() => new MailMessageId(Guid.Empty, "order-placed"));
    }

    [Fact]
    public void A_mail_prints_its_message_id_and_neither_its_mailbox_nor_its_body()
    {
        OutboundMail mail = new(
            "aigerim@example.test",
            "Your order is placed",
            "Order 42 is placed.",
            new MailMessageId(Guid.CreateVersion7(), "order-placed"),
            ["kk"]);

        string printed = mail.ToString();

        printed.ShouldContain(mail.MessageId.LocalPart);
        printed.ShouldNotContain("aigerim", Case.Insensitive, "§13.4: a log takes the ids, never the mailbox");
        printed.ShouldNotContain("Order 42", Case.Insensitive, "nor the body");
    }

    [Fact]
    public void An_unavailable_relay_carries_its_cause_and_its_code_and_nothing_of_the_relays_own()
    {
        MailUnavailableException thrown = new(
            "Message x met SmtpCommandException 451 while sending.", MailFault.Transient, 451);

        thrown.Cause.ShouldBe(MailFault.Transient);
        thrown.SmtpStatus.ShouldBe(451);
        thrown.InnerException.ShouldBeNull("a relay's own exception can quote the mailbox");
    }
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~MailPortTests"`
Expected: compile failure — `Notifications.Application.Mail` does not exist.

- [ ] **Step 3: Write the types**

`IMailChannel.cs`:

```csharp
namespace Notifications.Application.Mail;

/// <summary>The relay in this service's words, and the one place a vendor's adapter would go (§3.1).</summary>
/// <remarks>
/// A fault throws <see cref="MailUnavailableException"/> and is never a <see cref="MailResult"/>, so a dead relay
/// cannot reach a row as a refusal; nothing above this port knows the word SMTP.
/// </remarks>
public interface IMailChannel
{
    Task<MailResult> SendAsync(OutboundMail mail, CancellationToken ct);
}
```

`OutboundMail.cs`:

```csharp
namespace Notifications.Application.Mail;

/// <summary>One message as the worker hands it over: rendered, addressed and identified.</summary>
/// <remarks>Its text names the message alone, so a log or a fault that prints one holds no mailbox (§13.4).</remarks>
public sealed record OutboundMail(
    string Recipient,
    string Subject,
    string Body,
    MailMessageId MessageId,
    IReadOnlyList<string> Languages)
{
    public override string ToString() => $"{nameof(OutboundMail)} {{ {nameof(MessageId)} = {MessageId.LocalPart} }}";
}
```

`MailMessageId.cs`:

```csharp
namespace Notifications.Application.Mail;

/// <summary>A sent message's <c>Message-ID</c> left of the <c>@</c>: the row's event and its template key.</summary>
public sealed record MailMessageId
{
    public MailMessageId(Guid eventId, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(templateKey);

        if (eventId == Guid.Empty)
            throw new ArgumentException("The empty id is no event.", nameof(eventId));

        // Kebab case alone, so nothing but these characters ever reaches a header.
        if (!IsKebabCase(templateKey))
            throw new ArgumentException("A template key is kebab case.", nameof(templateKey));

        EventId = eventId;
        TemplateKey = templateKey;
    }

    public Guid EventId { get; }

    public string TemplateKey { get; }

    public string LocalPart => $"{EventId:N}.{TemplateKey}";

    private static bool IsKebabCase(string key) =>
        key.Length > 0
        && key[0] != '-'
        && key[^1] != '-'
        && !key.Contains("--", StringComparison.Ordinal)
        && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
```

No `[GeneratedRegex]` here, on purpose: the generator's support for a partial
`record` is the one thing in this file a build would have to discover, and
five comparisons say the same thing.

`MailResult.cs`:

```csharp
namespace Notifications.Application.Mail;

/// <summary>The relay's answer to an <see cref="OutboundMail"/>.</summary>
public abstract record MailResult
{
    private MailResult() { }

    public sealed record Accepted : MailResult;

    public sealed record Refused(MailRefusal Reason) : MailResult;
}
```

`MailRefusal.cs`:

```csharp
namespace Notifications.Application.Mail;

/// <summary>Why a send is over for good, each a terminal reason of its own on the row.</summary>
public enum MailRefusal
{
    /// <summary>The relay answered the recipient with a permanent <c>5xx</c>.</summary>
    RecipientRefused,

    /// <summary>The recipient is not one bare mailbox, a line break in it above all, so nothing was sent.</summary>
    NotAMailbox
}
```

`MailFault.cs`:

```csharp
namespace Notifications.Application.Mail;

/// <summary>Why a send met a fault rather than an answer, which decides its retry and its count.</summary>
public enum MailFault
{
    /// <summary>The relay never took the message: a <c>4xx</c>, a refused connection, an early timeout.</summary>
    Transient,

    /// <summary>The connection broke while the message was handed over, so the relay may hold it (§9.7).</summary>
    Unconfirmed,

    /// <summary>A session weaker than configured, which is somebody's decision rather than an outage.</summary>
    Tls,

    /// <summary>The relay refused this host's credential, a deployment's fault and not the customer's.</summary>
    Credential,

    /// <summary>A permanent refusal of the sender or the message rather than of the recipient.</summary>
    Rejected
}
```

`MailUnavailableException.cs`, the three standard constructors as
`CarrierUnavailableException` carries them, and a fourth:

```csharp
namespace Notifications.Application.Mail;

/// <summary>A send that met a fault rather than an answer: the row backs off, and nothing reaches a queue.</summary>
public sealed class MailUnavailableException : Exception
{
    public MailUnavailableException()
    {
    }

    public MailUnavailableException(string message)
        : base(message)
    {
    }

    public MailUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The adapter's form, with no inner exception, as a relay's own can quote the mailbox.</summary>
    public MailUnavailableException(string message, MailFault cause, int? smtpStatus)
        : base(message)
    {
        Cause = cause;
        SmtpStatus = smtpStatus;
    }

    public MailFault Cause { get; }

    /// <summary>The relay's reply code, where it gave one; its words are never kept.</summary>
    public int? SmtpStatus { get; }
}
```

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~MailPortTests"
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~ArchitectureTests"
dotnet build Platform.slnx
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): IMailChannel and its vocabulary, in the domain's words"
```

Expected: green, and the architecture suite stays green — the port names no
package, so Application's reference allow-list is unchanged.

---

### Task 2: The three pins, the package references and Appendix B

**Files:**
- Modify: `Directory.Packages.props` — `Polly.Core`, `MailKit`, `MimeKit`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Notifications.Infrastructure.csproj`
  — `MailKit`, `MimeKit`, `Polly.Core` and `Microsoft.Extensions.Hosting.Abstractions`,
  none with a `Version=`
- Modify: `docs/backend-architecture/appendix-b-licences.md` — three rows

- [ ] **Step 1: Measure the graph before the pin**

```bash
dotnet restore Platform.slnx
dotnet list src/Services/Shipping/Shipping.Infrastructure package --include-transitive | grep -i polly
dotnet list src/BFF/Web.Bff package --include-transitive | grep -i polly
dotnet list src/Services/Payments/Payments.Infrastructure package --include-transitive | grep -i polly
```

Expected: `Polly.Core 8.4.2` in all three, beside `Polly.Extensions` and
`Polly.RateLimiting` at `8.4.2`. Write the three lines down; Step 5 compares
against them. If any reports a higher `Polly.Core`, the pin below takes that
version instead, and the Appendix B row and this plan's Global Constraints
move with it.

- [ ] **Step 2: Pin**

In `Directory.Packages.props`'s `Runtime` group, directly after the
`Microsoft.Extensions.Http.Resilience` line:

```xml
    <!-- The engine under the row above, named directly where no HttpClient carries it: Notifications' relay
         pipeline. The version the row above already resolves, so pinning it moves no other graph. -->
    <PackageVersion Include="Polly.Core" Version="8.4.2" />
```

and after the `Microsoft.Data.SqlClient` line:

```xml
    <!-- Notifications' SMTP client and the message it submits (Appendix B). MimeKit is MailKit's own dependency at
         exactly this version, pinned because the adapter names its types; the two ship as one release. -->
    <PackageVersion Include="MailKit" Version="4.18.1" />
    <PackageVersion Include="MimeKit" Version="4.18.1" />
```

- [ ] **Step 3: Reference**

In `Notifications.Infrastructure.csproj`, in the `PackageReference` group
beside PR-1's references:

```xml
    <!-- The relay's adapter: MailKit submits, MimeKit builds the message and encodes its headers. -->
    <PackageReference Include="MailKit" />
    <PackageReference Include="MimeKit" />
    <!-- The relay's pipeline, built by hand because SMTP is no HttpClient (§9.7's rules, Polly's engine). -->
    <PackageReference Include="Polly.Core" />
    <!-- The hosting abstractions this project names: IHostEnvironment, in each outbound hop's registration. -->
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
```

The last reference and its comment are shared with PR-3: if its line is
already there, it stays as it is and this step adds the other three.

- [ ] **Step 4: Register**

Appendix B's first table gains two rows directly after
`| Polly (`Microsoft.Extensions.Http.Resilience`) | MIT | Resilience |`:

```markdown
| `Polly.Core` | BSD-3 | The resilience engine the row above configures for HTTP, named directly by `Notifications.Infrastructure`, which builds its relay's pipeline by hand because SMTP is no `HttpClient`. Pinned at the version the row above already resolves, so no other project's graph moves; registered because a pinned transitive is still a pin |
| MailKit (`MailKit`, `MimeKit`) | MIT | SMTP for Notifications' mail channel, the one adapter behind its `IMailChannel` port: MailKit submits, MimeKit builds the message and encodes its non-ASCII headers. Two identities, because `Notifications.Infrastructure` names both and MimeKit is MailKit's own dependency pinned at the version MailKit takes |
```

and one row after `OpenTelemetry Collector (contrib)`:

```markdown
| Mailpit | MIT | The SMTP sink of [§14.1](14-local-development.md), and the relay `Notifications.Worker.Tests` starts under Testcontainers. An image rather than a package, so named in prose for the delayed-exchange row's reason: a backticked identity is one the gate expects to find pinned |
```

- [ ] **Step 5: Restore, measure again, and run the licence gate**

```bash
dotnet restore Platform.slnx
dotnet list src/Services/Shipping/Shipping.Infrastructure package --include-transitive | grep -i polly
dotnet list src/BFF/Web.Bff package --include-transitive | grep -i polly
dotnet list src/Services/Payments/Payments.Infrastructure package --include-transitive | grep -i polly
dotnet list src/Services/Notifications/Notifications.Infrastructure package --include-transitive --vulnerable
dotnet build Platform.slnx
cd .github/licence-gate && py -3.12 -m unittest && py -3.12 licence_gate.py && cd ../..
```

Expected: the three Polly lines identical to Step 1's; no vulnerable package
(`BouncyCastle.Cryptography` `2.7.0`, `System.Security.Cryptography.Pkcs` and
`System.Formats.Asn1` `10.0.0` arrive under MimeKit, and none carries an
advisory today — NU1903 would have failed the restore if one did); the build
at 0 warnings; the licence gate exit 0, having matched `Polly.Core`,
`MailKit` and `MimeKit` against the new rows and `BSD-3` against
`allowed-licences.txt`.

- [ ] **Step 6: Commit**

```bash
git add Directory.Packages.props docs/backend-architecture/appendix-b-licences.md \
    src/Services/Notifications/Notifications.Infrastructure/Notifications.Infrastructure.csproj
git commit -m "chore(deps): pin MailKit, MimeKit and Polly.Core for Notifications' relay"
```

The body says that `Polly.Core` `8.4.2` is the version every graph already
resolved, and quotes Step 1's and Step 5's lines as the evidence.

---

### Task 3: The budget, the meter, the options and the pipeline

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailHop.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Observability/OutboundMeter.cs`
  (unless PR-3 landed it)
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailMetrics.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailSecurity.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailOptions.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailOptionsValidator.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/MailPipeline.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/DependencyInjection.cs`
- Modify: `src/Services/Notifications/Notifications.Worker/Program.cs` —
  `builder.Services.AddMailChannel(builder.Configuration, builder.Environment);`
  after the layer's method
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Observability/MetricsInitialiser.cs`
  — `MailMetrics` joins the constructor, with its guard
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs` — six
  relay parameters and four constants
- Create: `tests/Notifications.Worker.Tests/Unreachable.cs` (unless PR-3 landed it)
- Modify: `tests/Notifications.Worker.Tests/MetricsRegistrationTests.cs` —
  `BuildServices()` runs `AddMailChannel` too, and the selector test names
  `MailMetrics`
- Test: `tests/Notifications.Worker.Tests/MailHopTests.cs`
- Test: `tests/Notifications.Worker.Tests/MailOptionsTests.cs`

**Interfaces:**
- Consumes: Task 1's port types; Task 2's packages.
- Produces: `MailHop`'s numbers; `OutboundMeter.Name` of
  `Notifications.Outbound`; `MailMetrics.Unavailable(MailFault)` over
  `Counter<long> notifications.mail.unavailable` with a `cause` attribute;
  `MailOptions` with `SectionName` and the six `…Key` constants; `MailSecurity`;
  `IServiceCollection AddMailChannel(IConfiguration, IHostEnvironment)` in
  `Notifications.Infrastructure.Mail.DependencyInjection`;
  `MetricsInitialiser(MessagingMetrics, RequestMetrics, MailMetrics)`; the
  factory's six new parameters and `UnreachableRelay`, `LocalRelayPort`,
  `LocalFrom` and `NotARelayPassword`; `Unreachable.Sql` and
  `Unreachable.Rabbit`.

**Why the relay registers beside the layer, and where each rule runs.**
ADR-055: an outbound hop registers in a method of its own beside its layer's,
named for the hop, and the host's `Program.cs` calls it after the layer's —
`AddCarrierGateway(IServiceCollection, IConfiguration, IHostEnvironment)` is
the shape, and `AddMailChannel` takes it. The rule that needs the host's
environment runs where `AddCarrierGateway` runs its scheme rule: eagerly in
the registration, so a production host given `None` or no credential throws
an `InvalidOperationException` naming the key before it is built. The rules
that do not need the environment — the annotations, the host name's shape,
one sender mailbox, the user name and password together — are the options
type's, in a validator under `ValidateOnStart`, as Shipping's
`FulfilmentOptions` keeps its own. From this task on every host over this
`Program` reads `Mail` at start, so the factory supplies it — Development,
`None`, no credential, an `.invalid` relay — and every PR-1 suite keeps
starting.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Worker.Tests/Unreachable.cs`, Shipping's with the
service's own names:

```csharp
namespace Notifications.Worker.Tests;

/// <summary>Infrastructure a host can name and never reach (§12.4), for suites driving one outbound adapter.</summary>
internal static class Unreachable
{
    // tcp and a one-second bound, since a bare name falls back to named pipes and fails only at the provider's timeout.
    public const string Sql =
        "Server=tcp:sql.invalid,1433;Database=Notifications;User Id=x;Password=x;" +
        "TrustServerCertificate=true;Connect Timeout=1";

    public const string Rabbit = "amqp://notifications-svc:x@rabbit.invalid:5672";
}
```

`tests/Notifications.Worker.Tests/MailHopTests.cs` — no container, so no
collection and no category:

```csharp
using Notifications.Infrastructure.Mail;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay hop's arithmetic, which <see cref="MailHop"/>'s numbers satisfy together (§9.7).</summary>
public sealed class MailHopTests
{
    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = MailHop.AttemptTimeout * (MailHop.MaxRetryAttempts + 1)
                         + MailHop.MaxRetryDelay * MailHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(MailHop.TotalTimeout,
            "a total that cancels the last retry makes the retry count a fiction");

        MailHop.TotalTimeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout,
            "§9.7: the outbound client total must be strictly below the service operation total");
    }

    [Fact]
    public void The_attempt_timeout_is_outside_the_band_a_waiting_caller_is_sized_to()
    {
        // §9.7's band is a waiting caller's; this hop's row backs off, so it is sized to a third party.
        MailHop.AttemptTimeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_breaker_opens_inside_one_window_at_the_workers_slowest_rate()
    {
        // The breaker sits inside the retry, so a failed send is MaxRetryAttempts + 1 attempts against the throughput.
        int sends = (int)Math.Ceiling(
            (double)MailHop.CircuitBreakerMinimumThroughput / (MailHop.MaxRetryAttempts + 1));

        (MailHop.SendTick * sends).ShouldBeLessThan(MailHop.CircuitBreakerSamplingDuration,
            "a breaker whose throughput a loop at one send a tick never reaches would never open");
    }

    [Fact]
    public void The_breaker_breaks_for_less_than_it_samples()
    {
        MailHop.CircuitBreakerBreakDuration.ShouldBeLessThan(MailHop.CircuitBreakerSamplingDuration,
            "a breaker that forgets its failures while open reopens on the first error after it closes");
    }
}
```

`tests/Notifications.Worker.Tests/MailOptionsTests.cs`, in
`FulfilmentOptionsTests`' shape for the start-time rules and
`HttpCarrierGatewayTests`' for the registration's: the production
registration over a bare container for the messages, and hosts for the
wiring:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay's registration and options refuse to send in the clear or unauthenticated (§15.4).</summary>
public sealed class MailOptionsTests
{
    [Fact]
    public void Outside_development_plain_submission_is_refused_at_registration()
    {
        Should.Throw<InvalidOperationException>(() => Bound(Environments.Production, Relay(security: "None")))
            .Message.ShouldContain($"{MailOptions.SecurityKey} is None outside Development");
    }

    [Theory]
    [InlineData(null, NotificationsWorkerFactory.NotARelayPassword)]
    [InlineData("notifications", null)]
    [InlineData(null, null)]
    public void Outside_development_a_missing_credential_is_refused_at_registration(string? userName, string? password)
    {
        Should.Throw<InvalidOperationException>(() =>
                Bound(Environments.Production, Relay(userName: userName, relayPassword: password)))
            .Message.ShouldContain(
                $"{MailOptions.UserNameKey} and {MailOptions.PasswordKey} are required outside Development");
    }

    [Fact]
    public void Outside_development_starttls_with_a_credential_is_accepted()
    {
        // The control for the two above, so they cannot pass against a registration nothing satisfies.
        using ServiceProvider provider = Bound(Environments.Production, Relay());

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Outside_development_a_host_over_a_plain_relay_does_not_start()
    {
        // The factory's defaults are Development's: plain, anonymous, which Program.cs must refuse elsewhere.
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        Should.Throw<InvalidOperationException>(() => production.Services)
            .Message.ShouldContain("is None outside Development");
    }

    [Fact]
    public void In_development_plain_unauthenticated_submission_is_accepted()
    {
        using ServiceProvider provider = Bound(
            Environments.Development, Relay(security: "None", userName: null, relayPassword: null));

        Should.NotThrow(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("notifications", null)]
    [InlineData(null, NotificationsWorkerFactory.NotARelayPassword)]
    public void A_user_name_and_a_password_come_together_in_every_environment(string? userName, string? password)
    {
        using ServiceProvider provider = Bound(
            Environments.Development, Relay(security: "None", userName: userName, relayPassword: password));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("are set together or not at all");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a@example.test, b@example.test")]
    [InlineData("a@example.test\r\nBcc: b@example.test")]
    public void A_sender_that_is_not_one_mailbox_stops_the_host(string from)
    {
        using ServiceProvider provider = Bound(Environments.Production, Relay(from: from));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("From");
    }

    [Theory]
    [InlineData("smtp://relay.example.test")]
    [InlineData("relay.example.test:587")]
    [InlineData(" ")]
    public void A_host_that_is_not_a_host_name_stops_the_host(string host)
    {
        using ServiceProvider provider = Bound(Environments.Production, Relay(host: host));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Host");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("")]
    public void A_port_outside_the_range_stops_the_host(string port)
    {
        using ServiceProvider provider = Bound(Environments.Production, Relay(port: port));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Port");
    }

    [Fact]
    public void A_host_that_names_no_relay_does_not_start()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, mailHost: "");

        // The factory builds the host on first use, and a host refusing to
        // start races its disposal, so no exception type is asserted.
        Should.Throw<Exception>(() => factory.CreateClient());
    }

    private static Dictionary<string, string?> Relay(
        string host = "relay.example.test",
        string port = "587",
        string from = NotificationsWorkerFactory.LocalFrom,
        string security = "StartTls",
        string? userName = "notifications",
        string? relayPassword = NotificationsWorkerFactory.NotARelayPassword) =>
        new()
        {
            [MailOptions.HostKey] = host,
            [MailOptions.PortKey] = port,
            [MailOptions.FromKey] = from,
            [MailOptions.SecurityKey] = security,
            [MailOptions.UserNameKey] = userName,
            [MailOptions.PasswordKey] = relayPassword
        };

    // The production registration over the given environment; the host facts above are what still fail if
    // Program.cs drops the call.
    private static ServiceProvider Bound(string environment, Dictionary<string, string?> relay)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(relay).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddMailChannel(configuration, new TestEnvironment { EnvironmentName = environment });

        return services.BuildServiceProvider();
    }

    /// <summary>A minimal <see cref="IHostEnvironment"/>; the registration reads only its name.</summary>
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "Notifications.Worker.Tests";

        public string EnvironmentName { get; set; } = Environments.Production;

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
```

`MetricsRegistrationTests.cs`: `BuildServices()`'s configuration gains the
relay's six keys, its body calls the hop's registration as `Program.cs` does,
and the selector test names the new type. With
`using Notifications.Infrastructure.Mail;`, `using Notifications.TestSupport;`
and `using Microsoft.Extensions.FileProviders;` in sorted position, each where
absent, in the dictionary:

```csharp
                    // Read eagerly by AddMailChannel; StartTls and a credential, as the environment is not Development.
                    [MailOptions.HostKey] = "notifications-relay.invalid",
                    [MailOptions.PortKey] = "587",
                    [MailOptions.FromKey] = NotificationsWorkerFactory.LocalFrom,
                    [MailOptions.SecurityKey] = "StartTls",
                    [MailOptions.UserNameKey] = "notifications",
                    [MailOptions.PasswordKey] = NotificationsWorkerFactory.NotARelayPassword
```

after `services.AddNotificationsInfrastructure(configuration);`:

```csharp
        services.AddMailChannel(configuration, new TestEnvironment());
```

the summary over `BuildServices`, a touched block rewritten whole, in PR-3's
words so either order leaves one text:

```csharp
    /// <summary>The registration helpers the worker's <c>Program</c> calls, over unreachable configuration.</summary>
```

a `TestEnvironment` class at the foot of the file — Production, Shipping's
`MetricsRegistrationTests.TestEnvironment` verbatim — unless PR-1's render or
PR-3 already put one there; and in
`The_metrics_selector_actually_selects_something`:

```csharp
        registered.ShouldContain(typeof(MailMetrics));
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailHopTests|FullyQualifiedName~MailOptionsTests|FullyQualifiedName~MetricsRegistrationTests"`
Expected: compile failure on `MailHop`, `MailOptions`, `MailMetrics`,
`AddMailChannel` and the factory's new parameters. Once steps 3–5 give the
suite `MailMetrics` and the registration, and before step 5's initialiser
edit, `Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be` fails
on "add it to MetricsInitialiser, or to NotForced with a reason" — the red
that proves the suite sees the relay's type.

- [ ] **Step 3: Write the budget and the meter**

`MailHop.cs`:

```csharp
namespace Notifications.Infrastructure.Mail;

/// <summary>The relay call's budget and the send worker's tick, sized to a worker's leased row (§9.7).</summary>
/// <remarks>
/// A retry repeats only an attempt the relay never took the message in, §9.7's fourth rule for a send that
/// carries no idempotency key. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public static class MailHop
{
    /// <summary>Above §9.7's band, as a third party's is: nothing waits on this hop, and the row backs off.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt, so one more connection than this.</summary>
    public const int MaxRetryAttempts = 1;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Strictly below <c>ServiceOptions.OperationTimeout</c> (§9.7); the worker's lease is above.</summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(19);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Sized to a worker's call rate: two failed sends open it, where a hundred never would.</summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>Shorter than the window, so the breaker keeps its failures while open.</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>How often the send worker claims, paced by new events rather than by the relay.</summary>
    public static readonly TimeSpan SendTick = TimeSpan.FromSeconds(5);
}
```

`Observability/OutboundMeter.cs`, unless PR-3 landed it, in the text PR-3's
plan writes, so either order leaves one file:

```csharp
namespace Notifications.Infrastructure.Observability;

/// <summary>The meter for the work that leaves this service (§13.2).</summary>
public static class OutboundMeter
{
    public const string Name = "Notifications.Outbound";
}
```

`Mail/MailMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Mail;

/// <summary>Attempts that met a failing relay, by cause, so a refusal that is a decision reads apart.</summary>
/// <remarks>A fact about the relay, not a notification, so §13.3's claim rule does not reach it.</remarks>
public sealed class MailMetrics
{
    private readonly Counter<long> _unavailable;

    public MailMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);
        _unavailable = meter.CreateCounter<long>(
            "notifications.mail.unavailable",
            unit: "{attempt}",
            description: "Relay attempts that ended in a fault rather than an answer; the row backs off.");
    }

    public void Unavailable(MailFault cause) =>
        _unavailable.Add(1, new KeyValuePair<string, object?>("cause", Cause(cause)));

    // Spelled out rather than ToString(), so the attribute's vocabulary is this switch and not an enum's casing.
    private static string Cause(MailFault cause) => cause switch
    {
        MailFault.Transient => "transient",
        MailFault.Unconfirmed => "unconfirmed",
        MailFault.Tls => "tls",
        MailFault.Credential => "credential",
        MailFault.Rejected => "rejected",
        _ => "unknown"
    };
}
```

- [ ] **Step 4: Write the options and their validator**

`Mail/MailSecurity.cs`:

```csharp
namespace Notifications.Infrastructure.Mail;

/// <summary>How the session to the relay is protected; <see cref="None"/> is Development's alone (§15.4).</summary>
public enum MailSecurity
{
    None,
    StartTls
}
```

`Mail/MailOptions.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Mail;

/// <summary>Where the relay is, who sends, and how the session is protected (§15.4).</summary>
/// <remarks>Earns its options type as <c>Identity:Client</c> does, by a credential per environment (§15.4).</remarks>
public sealed class MailOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Mail";

    public const string HostKey = $"{SectionName}:{nameof(Host)}";
    public const string PortKey = $"{SectionName}:{nameof(Port)}";
    public const string FromKey = $"{SectionName}:{nameof(From)}";
    public const string SecurityKey = $"{SectionName}:{nameof(Security)}";
    public const string UserNameKey = $"{SectionName}:{nameof(UserName)}";

    /// <summary>The setting's name, never its value.</summary>
    public const string PasswordKey = $"{SectionName}:{nameof(Password)}";

    [Required]
    public string? Host { get; init; }

    [Required]
    [Range(1, 65535)]
    public int? Port { get; init; }

    /// <summary>One mailbox, a display name allowed; every <c>Message-ID</c> is minted under its domain.</summary>
    [Required]
    public string? From { get; init; }

    /// <summary>Nullable, so <c>[Required]</c> sees a missing key rather than the first member.</summary>
    [Required]
    public MailSecurity? Security { get; init; }

    public string? UserName { get; init; }

    public string? Password { get; init; }
}
```

`Mail/MailOptionsValidator.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Notifications.Infrastructure.Mail;

/// <summary>Runs <see cref="MailOptions"/>' annotations and the rules they cannot state, at start (§15.4).</summary>
internal sealed class MailOptionsValidator : IValidateOptions<MailOptions>
{
    public ValidateOptionsResult Validate(string? name, MailOptions options)
    {
        List<ValidationResult> annotated = [];

        // No message echoes a configured value: a failed start is logged, and the password sits beside the rest.
        List<string> failures =
            Validator.TryValidateObject(options, new ValidationContext(options), annotated, validateAllProperties: true)
                ? []
                : [.. annotated.Select(a => a.ErrorMessage ?? "Invalid.")];

        if (options.Host is { } host && Uri.CheckHostName(host) == UriHostNameType.Unknown)
            failures.Add($"{MailOptions.HostKey} is not a host name.");

        if (options.From is { } from && !OneMailbox(from))
            failures.Add($"{MailOptions.FromKey} is not one mailbox.");

        bool hasUserName = !string.IsNullOrWhiteSpace(options.UserName);
        bool hasPassword = !string.IsNullOrWhiteSpace(options.Password);

        if (hasUserName != hasPassword)
            failures.Add($"{MailOptions.UserNameKey} and {MailOptions.PasswordKey} are set together or not at all.");

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    // A display name is allowed; a second address, a group or a line break is not.
    private static bool OneMailbox(string text) =>
        !text.Any(char.IsControl)
        && MailboxAddress.TryParse(text, out MailboxAddress? parsed)
        && parsed is { Domain.Length: > 0 };
}
```

- [ ] **Step 5: Write the pipeline, the registration and the initialiser's parameter**

`Mail/MailPipeline.cs`:

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
    public MailPipeline(MailMetrics metrics) =>
        Pipeline = new ResiliencePipelineBuilder()
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
                BreakDuration = MailHop.CircuitBreakerBreakDuration
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

    public ResiliencePipeline Pipeline { get; }
}
```

Polly validates every strategy's options in `Build()`, so a number in
`MailHop` the library refuses — a sampling window under half a second, a
throughput under two — fails the host when the singleton is first resolved,
which the host smoke suite does at start.

`Mail/DependencyInjection.cs` — the channel's own line arrives in Task 4:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Notifications.Infrastructure.Mail;

/// <summary>The relay's registration, beside the layer's, as its environment rule needs the host (ADR-055).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddMailChannel(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Development's alone, refused before the host is built, as AddCarrierGateway refuses plain HTTP (§15.4).
        if (!environment.IsDevelopment())
            RefusePlainOrAnonymous(configuration.GetSection(MailOptions.SectionName).Get<MailOptions>());

        // Validated at start beside its consumer (§15.4): IOptions<T> always resolves, so a forgotten bind is silent.
        services
            .AddOptions<MailOptions>()
            .BindConfiguration(MailOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MailOptions>, MailOptionsValidator>();

        services.AddSingleton<MailMetrics>();

        // A singleton, so the breaker inside it is the host's and not a scope's.
        services.AddSingleton<MailPipeline>();

        return services;
    }

    // No message echoes a configured value: a failed start is logged, and the password sits beside the rest.
    private static void RefusePlainOrAnonymous(MailOptions? relay)
    {
        if (relay?.Security == MailSecurity.None)
        {
            throw new InvalidOperationException(
                $"{MailOptions.SecurityKey} is None outside Development; " +
                "the credential and every message would travel in the clear.");
        }

        if (string.IsNullOrWhiteSpace(relay?.UserName) || string.IsNullOrWhiteSpace(relay?.Password))
        {
            throw new InvalidOperationException(
                $"{MailOptions.UserNameKey} and {MailOptions.PasswordKey} are required outside Development; " +
                "a host does not submit to a relay anonymously.");
        }
    }
}
```

A missing `Mail:Security` passes the check above and fails the validator's
`[Required]` at start, so the eager rule needs only to refuse what is
present and wrong.

In `Notifications.Worker/Program.cs`, after
`builder.Services.AddNotificationsInfrastructure(builder.Configuration);`
and before PR-3's `AddContactSource` line if it is there, so the order is
the same whichever lands first, with
`using Notifications.Infrastructure.Mail;` in sorted position:

```csharp
// The relay behind IMailChannel (ADR-055); plain or anonymous submission is refused outside Development.
builder.Services.AddMailChannel(builder.Configuration, builder.Environment);
```

`AddNotificationsInfrastructure` is not edited: the layer's method holds what
the layer holds for itself, and the hop is the host's to call.

`MetricsInitialiser.cs` gains `MailMetrics` after PR-1's parameters — and
beside PR-3's `ContactMetrics` if it is there, each with its guard — in the
shape Shipping's takes `CarrierMetrics`, with
`using Notifications.Infrastructure.Mail;` in sorted position. Before, on
PR-1's render alone:

```csharp
    public MetricsInitialiser(MessagingMetrics messaging, RequestMetrics requests)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
    }
```

After:

```csharp
    public MetricsInitialiser(MessagingMetrics messaging, RequestMetrics requests, MailMetrics mail)
    {
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(mail);
    }
```

The file's own membership rule — can the service run for an hour without
constructing it — is met: a Notifications that has sent nothing constructs
`MailMetrics` never.

`NotificationsWorkerFactory` gains six parameters after PR-1's two, each
defaulted so every existing caller compiles unchanged, with
`using Notifications.Infrastructure.Mail;` and
`using System.Globalization;` in sorted position:

```csharp
public class NotificationsWorkerFactory(
    string connectionString,
    string rabbitConnectionString,
    string mailHost = NotificationsWorkerFactory.UnreachableRelay,
    int mailPort = NotificationsWorkerFactory.LocalRelayPort,
    string mailSecurity = "None",
    string? mailUserName = null,
    string? mailPassword = null,
    string mailFrom = NotificationsWorkerFactory.LocalFrom)
    : WebApplicationFactory<Program>
```

the four constants beside `UnreachableAuthority`:

```csharp
    /// <summary>The relay a host names when a test gives none; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableRelay = "relay.invalid";

    /// <summary>Mailpit's SMTP port, the one §14.1's unit reaches on the Compose network.</summary>
    public const int LocalRelayPort = 1025;

    /// <summary>The sender a host names when a test gives none, on a domain RFC 2606 reserves.</summary>
    public const string LocalFrom = "Commerce <no-reply@commerce.test>";

    /// <summary>A relay credential that satisfies the rule and is unmistakably not one (docs/secrets.md).</summary>
    public const string NotARelayPassword = "not-a-real-relay-password";
```

and six settings in `ConfigureWebHost`, after the authority's:

```csharp
            .UseSetting(MailOptions.HostKey, mailHost)
            .UseSetting(MailOptions.PortKey, mailPort.ToString(CultureInfo.InvariantCulture))
            .UseSetting(MailOptions.FromKey, mailFrom)
            .UseSetting(MailOptions.SecurityKey, mailSecurity)
            .UseSetting(MailOptions.UserNameKey, mailUserName)
            .UseSetting(MailOptions.PasswordKey, mailPassword)
```

`WebApplicationFactory` runs the host as Development by default, which is the
one environment where `None` and no credential start; a test that needs
Production says so with `WithWebHostBuilder`, as Shipping's do.

- [ ] **Step 6: Run the tests and the suite**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailHopTests|FullyQualifiedName~MailOptionsTests|FullyQualifiedName~MetricsRegistrationTests"
dotnet test Platform.slnx --filter "Category!=Integration"
dotnet build Platform.slnx
```

Expected: green; the registration suite green in both directions, because
`BuildServices()` calls what `Program.cs` calls; every PR-1 host still
starting, because the factory supplies a Development relay; 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Notifications tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "feat(notifications): AddMailChannel with MailHop, its options and its pipeline"
```

---

### Task 4: The adapter, against a real relay

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Mail/SmtpMailChannel.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Mail/DependencyInjection.cs`
  — `services.AddSingleton<IMailChannel, SmtpMailChannel>();`
- Create: `tests/Notifications.TestSupport/Mailpit.cs`
- Create: `tests/Notifications.Worker.Tests/MailpitFixture.cs` — the fixture
  and its collection
- Create: `tests/Notifications.Worker.Tests/MailCounter.cs`
- Create: `tests/Notifications.Worker.Tests/RepositoryRoot.cs` (unless PR-3
  landed it, with the same text)
- Test: `tests/Notifications.Worker.Tests/SmtpMailChannelTests.cs` — the rows
  that end in an answer, over one shared host
- Test: `tests/Notifications.Worker.Tests/MailFaultTests.cs` — the rows that
  end in a fault, a host each
- Test: `tests/Notifications.Worker.Tests/MailTlsTests.cs`
- Test: `tests/Notifications.Worker.Tests/NoCertificateBypassTests.cs`

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: `IMailChannel` resolvable from the host; `Mailpit` with
  `Image`, `SmtpPort`, `ApiPort`, `CertificateName`, `Plain()`,
  `SelfSigned()`, `Host`, `Port`, `StartAsync`, `ResetAsync`,
  `RefuseRecipientsAsync`, `RefuseSendersAsync`, `MessagesAsync`,
  `SingleAsync`, `HeadersAsync`, and the records `MailpitAddress`,
  `MailpitSummary` and `MailpitMessage` — PR-5's `ServiceFixture` starts the
  same type; `MailpitFixture`, `MailpitCollection`; `MailCounter.Unavailable`
  and `MailCount.Of(string cause)`; `RepositoryRoot.Locate()`.

**Why Mailpit and not WireMock.Net.** §12.7 gives a third-party *API*
WireMock.Net, and SMTP is not HTTP: nothing but a real SMTP server can show
the bytes a relay receives, which is where the encoding and header rules
live. Mailpit's Chaos triggers — `PUT /api/v1/chaos`, on when
`MP_ENABLE_CHAOS` is — stage a `4xx` or `5xx` to the sender or the recipient
at a probability, so every row of section 4's table is the relay's own answer
rather than a stub's. Task 7 gives §12.7 the row.

**Why the fault rows get a host each.** As in Shipping's
`CarrierFaultTests`: `MailHop.CircuitBreakerMinimumThroughput` is four
attempts, two failed sends, so behind one shared host the first two fault
rows would open the breaker and every test after them would be refused
without a connection leaving the process. The rows that end in an answer
share one host, because an answer is a success to the breaker; the relay is
shared by all of them, because the collection runs its classes one after
another and each resets the sink first.

- [ ] **Step 1: Write the relay's test double**

`tests/Notifications.TestSupport/Mailpit.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Notifications.TestSupport;

/// <summary>The relay, in a container, read back through its HTTP API (§12.7).</summary>
/// <remarks>The tag is the Compose unit's, held equal by a test, so a person watches this sink (§14.1).</remarks>
public sealed class Mailpit : IAsyncDisposable
{
    public const string Image = "axllent/mailpit:v1.31.3";

    public const int SmtpPort = 1025;

    public const int ApiPort = 8025;

    /// <summary>The self-signed certificate's name, so its refusal is the trust's and not the name's.</summary>
    public const string CertificateName = "localhost";

    /// <summary>How long a message may take to appear; a deadline, not a sleep.</summary>
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IContainer _container;
    private HttpClient? _api;

    private Mailpit(IContainer container) => _container = container;

    /// <summary>Plain SMTP with the Chaos API on, as Compose runs it.</summary>
    public static Mailpit Plain() => new(Builder().Build());

    /// <summary>STARTTLS under a certificate Mailpit signs itself, which no trust store holds.</summary>
    public static Mailpit SelfSigned() =>
        new(Builder()
            .WithEnvironment("MP_SMTP_TLS_CERT", $"sans:{CertificateName}")
            .WithEnvironment("MP_SMTP_TLS_KEY", $"sans:{CertificateName}")
            .Build());

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(SmtpPort);

    private HttpClient Api => _api ?? throw new InvalidOperationException("Mailpit has not been started.");

    public async Task StartAsync(CancellationToken ct)
    {
        await _container.StartAsync(ct);
        _api = new HttpClient
        {
            BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}/api/v1/")
        };
    }

    /// <summary>No messages and no Chaos trigger, so a test sees only what it sent and what it staged.</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        using HttpResponseMessage cleared = await Api.DeleteAsync("messages", ct);
        cleared.EnsureSuccessStatusCode();
        await ChaosAsync(new { }, ct);
    }

    /// <summary>Every recipient answered with <paramref name="code"/>, as a relay refusing them would.</summary>
    public Task RefuseRecipientsAsync(int code, CancellationToken ct) =>
        ChaosAsync(new { Recipient = new { ErrorCode = code, Probability = 100 } }, ct);

    /// <summary>The sender answered with <paramref name="code"/>, as a relay refusing this deployment would.</summary>
    public Task RefuseSendersAsync(int code, CancellationToken ct) =>
        ChaosAsync(new { Sender = new { ErrorCode = code, Probability = 100 } }, ct);

    public async Task<IReadOnlyList<MailpitSummary>> MessagesAsync(CancellationToken ct)
    {
        MailpitPage? page = await Api.GetFromJsonAsync<MailpitPage>("messages", Json, ct);
        return page?.Messages ?? throw new InvalidOperationException("Mailpit answered its message list with no body.");
    }

    /// <summary>The one message the sink holds, waited for, read whole.</summary>
    public async Task<MailpitMessage> SingleAsync(CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + ArrivalDeadline;

        while (DateTimeOffset.UtcNow < deadline)
        {
            IReadOnlyList<MailpitSummary> messages = await MessagesAsync(ct);
            if (messages.Count > 1)
                throw new InvalidOperationException($"Mailpit holds {messages.Count} messages where one was sent.");

            if (messages.Count == 1)
            {
                return await Api.GetFromJsonAsync<MailpitMessage>(
                           $"message/{Uri.EscapeDataString(messages[0].Id)}", Json, ct)
                       ?? throw new InvalidOperationException("Mailpit answered a message read with no body.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }

        throw new TimeoutException($"No message reached Mailpit within {ArrivalDeadline}.");
    }

    /// <summary>A message's headers as the relay received them, by name in any case.</summary>
    public async Task<IReadOnlyDictionary<string, string[]>> HeadersAsync(string id, CancellationToken ct)
    {
        Dictionary<string, string[]> headers =
            await Api.GetFromJsonAsync<Dictionary<string, string[]>>(
                $"message/{Uri.EscapeDataString(id)}/headers", Json, ct)
            ?? throw new InvalidOperationException("Mailpit answered a header read with no body.");

        return new Dictionary<string, string[]>(headers, StringComparer.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await _container.DisposeAsync();
    }

    private async Task ChaosAsync(object triggers, CancellationToken ct)
    {
        using HttpResponseMessage response = await Api.PutAsJsonAsync("chaos", triggers, ct);
        response.EnsureSuccessStatusCode();
    }

    private static ContainerBuilder Builder() =>
        new ContainerBuilder()
            .WithImage(Image)
            .WithPortBinding(SmtpPort, assignRandomHostPort: true)
            .WithPortBinding(ApiPort, assignRandomHostPort: true)
            .WithEnvironment("MP_ENABLE_CHAOS", "true")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort((ushort)ApiPort).ForPath("/readyz")));

    private sealed record MailpitPage(int Total, IReadOnlyList<MailpitSummary> Messages);
}

public sealed record MailpitAddress(string Name, string Address);

public sealed record MailpitSummary(string Id, string MessageId, string Subject, IReadOnlyList<MailpitAddress> To);

public sealed record MailpitMessage(
    string Id,
    string MessageId,
    string Subject,
    string Text,
    MailpitAddress From,
    IReadOnlyList<MailpitAddress> To);
```

`JsonSerializerDefaults.Web` matches property names without regard to case,
which is what binds Mailpit's `ID` and `MessageID` to `Id` and `MessageId`;
the Chaos body is sent in camel case and Mailpit's Go decoder matches field
names the same way. Mailpit generates the self-signed pair itself from
`sans:` — measured in `config/config.go` at `v1.31.3` — so no key is written
to the tree or to disk by the suite.

`tests/Notifications.Worker.Tests/MailpitFixture.cs`:

```csharp
using Notifications.TestSupport;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A plain relay, one behind an untrusted certificate, and a Development host over the first.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    public Mailpit Plain { get; } = Mailpit.Plain();

    public Mailpit SelfSigned { get; } = Mailpit.SelfSigned();

    /// <summary>Shared by the rows that end in an answer; a fault row builds its own.</summary>
    public NotificationsWorkerFactory Host { get; private set; } = null!;

    /// <summary>A Development host over one sink, with a pipeline and so a breaker of its own.</summary>
    public static NotificationsWorkerFactory Development(
        Mailpit sink,
        string from = NotificationsWorkerFactory.LocalFrom) =>
        new(Unreachable.Sql, Unreachable.Rabbit, mailHost: sink.Host, mailPort: sink.Port, mailFrom: from);

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (§12.4).
    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(Plain.StartAsync(ct), SelfSigned.StartAsync(ct));
        Host = Development(Plain);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Host?.Dispose();
        }
        finally
        {
            await Plain.DisposeAsync();
            await SelfSigned.DisposeAsync();
        }
    }
}

/// <summary>§12.4's per-assembly collection: one pair of relays, and a category every member class inherits.</summary>
[CollectionDefinition(nameof(MailpitCollection))]
[Trait("Category", "Integration")]
public sealed class MailpitCollection : ICollectionFixture<MailpitFixture>;
```

`tests/Notifications.Worker.Tests/MailCounter.cs`, `OutboundCounter`'s shape
with the attribute kept:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Infrastructure.Mail;
using Notifications.Infrastructure.Observability;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>One host's relay counter, matched by meter instance as a <c>MeterListener</c> is process-wide.</summary>
internal static class MailCounter
{
    public static MailCount Unavailable(IServiceProvider services)
    {
        // The counter is created in MailMetrics' constructor; a listener started first would see nothing published.
        services.GetRequiredService<MailMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(OutboundMeter.Name);
        MailCount count = new(mine);
        count.Enabled.ShouldBeTrue("no counter on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }
}

internal sealed class MailCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<string?> _causes = new();

    public MailCount(Meter mine)
    {
        _listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == "notifications.mail.unavailable")
            {
                l.EnableMeasurementEvents(published);
                Enabled = true;
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? cause = null;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "cause")
                    cause = tag.Value as string;
            }

            for (long i = 0; i < value; i++)
                _causes.Enqueue(cause);
        });
        _listener.Start();
    }

    public bool Enabled { get; private set; }

    public long Value => _causes.Count;

    public long Of(string cause) => _causes.Count(c => c == cause);

    public void Dispose() => _listener.Dispose();
}
```

`tests/Notifications.Worker.Tests/RepositoryRoot.cs`, unless PR-3 landed it —
PR-3's plan writes this text exactly:

```csharp
namespace Notifications.Worker.Tests;

/// <summary>The directory holding <c>Platform.slnx</c>, walked up to from the test's own output.</summary>
internal static class RepositoryRoot
{
    public static string Locate()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException($"No Platform.slnx above {AppContext.BaseDirectory}.");
    }
}
```

- [ ] **Step 2: Write the failing adapter tests**

`tests/Notifications.Worker.Tests/SmtpMailChannelTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay's rows that end in an answer, over one host and a real relay (§12.7).</summary>
[Collection(nameof(MailpitCollection))]
public sealed class SmtpMailChannelTests(MailpitFixture fixture) : IAsyncLifetime
{
    private const string Customer = "aigerim@example.test";

    public async ValueTask InitializeAsync() => await fixture.Plain.ResetAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IMailChannel Channel() => fixture.Host.Services.GetRequiredService<IMailChannel>();

    private static OutboundMail Mail(
        string recipient = Customer,
        string subject = "Your order is placed",
        string body = "Order 42 is placed.",
        MailMessageId? id = null,
        IReadOnlyList<string>? languages = null) =>
        new(
            recipient,
            subject,
            body,
            id ?? new MailMessageId(Guid.CreateVersion7(), "order-placed"),
            languages ?? ["en"]);

    [Fact]
    public async Task A_message_arrives_as_plain_utf8_text_under_the_message_id_it_was_given()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        MailMessageId id = new(Guid.CreateVersion7(), "order-placed");

        MailResult result = await Channel().SendAsync(Mail(id: id, languages: ["kk", "ru"]), ct);

        result.ShouldBe(new MailResult.Accepted());
        MailpitMessage arrived = await fixture.Plain.SingleAsync(ct);
        arrived.MessageId.Trim('<', '>').ShouldBe($"{id.LocalPart}@commerce.test");
        arrived.To.ShouldHaveSingleItem().Address.ShouldBe(Customer);
        arrived.From.Address.ShouldBe("no-reply@commerce.test");

        IReadOnlyDictionary<string, string[]> headers = await fixture.Plain.HeadersAsync(arrived.Id, ct);
        headers["Content-Type"].ShouldHaveSingleItem()
            .ShouldBe("text/plain; charset=utf-8", StringCompareShould.IgnoreCase);
        headers["Content-Language"].ShouldHaveSingleItem().ShouldBe("kk, ru");
    }

    [Fact]
    public async Task A_Kazakh_subject_body_and_sender_name_arrive_intact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        const string subject = "Тапсырысыңыз қабылданды: ә ғ қ ң ө ұ ү һ і";
        const string body = "Сәлеметсіз бе!\nТапсырыс қабылданды: Ә Ғ Қ Ң Ө Ұ Ү Һ І.\nРақмет.";

        // A host of its own, because the sender is configuration and this one is named in Kazakh too.
        using NotificationsWorkerFactory host = MailpitFixture.Development(
            fixture.Plain, from: "Дүкен <no-reply@commerce.test>");

        MailResult result = await host.Services.GetRequiredService<IMailChannel>()
            .SendAsync(Mail(subject: subject, body: body, languages: ["kk"]), ct);

        result.ShouldBe(new MailResult.Accepted());
        MailpitMessage arrived = await fixture.Plain.SingleAsync(ct);
        arrived.Subject.ShouldBe(subject);
        arrived.From.Name.ShouldBe("Дүкен");

        // SMTP's line ending is CRLF whatever the body's was (RFC 5321 section 2.3.8), so lines are compared.
        arrived.Text.ReplaceLineEndings("\n").TrimEnd('\n').ShouldBe(body);
    }

    [Theory]
    [InlineData("aigerim@example.test\r\nBcc: someone@example.test")]
    [InlineData("aigerim@example.test\n")]
    [InlineData("aigerim\r@example.test")]
    public async Task A_mailbox_carrying_a_line_break_is_refused_and_nothing_is_sent(string recipient)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        MailResult result = await Channel().SendAsync(Mail(recipient: recipient), ct);

        result.ShouldBe(new MailResult.Refused(MailRefusal.NotAMailbox));
        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Aigerim <aigerim@example.test>")]
    [InlineData("aigerim@example.test, someone@example.test")]
    [InlineData("aigerim@example.test (a comment)")]
    [InlineData("aigerim")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_recipient_that_is_not_one_bare_mailbox_is_refused(string recipient)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        MailResult result = await Channel().SendAsync(Mail(recipient: recipient), ct);

        result.ShouldBe(new MailResult.Refused(MailRefusal.NotAMailbox));
        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_subject_carrying_a_line_break_is_a_defect_and_nothing_is_sent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentException>(() =>
            Channel().SendAsync(Mail(subject: "Placed\r\nBcc: someone@example.test"), ct));

        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("en\r\nBcc: someone@example.test")]
    [InlineData("english")]
    [InlineData("")]
    public async Task A_language_that_is_not_a_tag_is_a_defect_and_nothing_is_sent(string language)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ArgumentException>(() => Channel().SendAsync(Mail(languages: [language]), ct));

        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recipient_the_relay_refuses_for_good_is_an_answer_and_is_not_counted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(550, ct);
        using MailCount counted = MailCounter.Unavailable(fixture.Host.Services);

        MailResult result = await Channel().SendAsync(Mail(), ct);

        result.ShouldBe(new MailResult.Refused(MailRefusal.RecipientRefused));
        counted.Value.ShouldBe(0, "a refusal is an answer, so the pipeline neither retries it nor counts it");
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_counted_against_the_relay()
    {
        using MailCount counted = MailCounter.Unavailable(fixture.Host.Services);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Channel().SendAsync(Mail(), cancelled.Token));

        counted.Value.ShouldBe(0, "a pass cancelled at shutdown is not a relay incident");
    }
}
```

`tests/Notifications.Worker.Tests/MailFaultTests.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.Infrastructure.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The relay's fault rows, a host each, because the breaker they fill is sized to open.</summary>
[Collection(nameof(MailpitCollection))]
public sealed class MailFaultTests(MailpitFixture fixture) : IAsyncLifetime
{
    private NotificationsWorkerFactory _host = null!;

    public async ValueTask InitializeAsync()
    {
        await fixture.Plain.ResetAsync(TestContext.Current.CancellationToken);
        _host = MailpitFixture.Development(fixture.Plain);
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private IMailChannel Channel() => _host.Services.GetRequiredService<IMailChannel>();

    private static OutboundMail Mail(string recipient = "aigerim@example.test", MailMessageId? id = null) =>
        new(recipient, "Your order is placed", "Order 42 is placed.",
            id ?? new MailMessageId(Guid.CreateVersion7(), "order-placed"), ["en"]);

    [Fact]
    public async Task A_relay_declining_for_now_is_retried_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        using MailCount counted = MailCounter.Unavailable(_host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            Channel().SendAsync(Mail(), ct));

        thrown.Cause.ShouldBe(MailFault.Transient);
        thrown.SmtpStatus.ShouldBe(451);
        counted.Of("transient").ShouldBe(MailHop.MaxRetryAttempts + 1,
            "one per attempt, and a 4xx before the data is the relay never having taken the message");
    }

    [Fact]
    public async Task A_sender_the_relay_refuses_for_good_backs_off_and_is_not_retried()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseSendersAsync(550, ct);
        using MailCount counted = MailCounter.Unavailable(_host.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            Channel().SendAsync(Mail(), ct));

        // This deployment's own From refused, which no customer's row should end on.
        thrown.Cause.ShouldBe(MailFault.Rejected);
        thrown.SmtpStatus.ShouldBe(550);
        counted.Of("rejected").ShouldBe(1, "a permanent refusal is not retried in the client");
    }

    [Fact]
    public async Task The_fault_names_the_message_and_never_the_mailbox_or_the_relays_words()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        MailMessageId id = new(Guid.CreateVersion7(), "payment-declined");

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            Channel().SendAsync(Mail(recipient: "aigerim.private@example.test", id: id), ct));

        string everything = thrown.ToString();
        everything.ShouldContain(id.LocalPart);
        everything.ShouldNotContain("aigerim.private", Case.Insensitive, "§13.4: a fault carries an id alone");
        everything.ShouldNotContain("Chaos", Case.Insensitive, "the relay's reply text is the relay's words");
        thrown.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable_rather_than_a_refusal()
    {
        // The factory's default relay, whose .invalid name never resolves.
        using NotificationsWorkerFactory unreachable = new(Unreachable.Sql, Unreachable.Rabbit);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            unreachable.Services.GetRequiredService<IMailChannel>()
                .SendAsync(Mail(), TestContext.Current.CancellationToken));

        thrown.Cause.ShouldBe(MailFault.Transient);
    }

    [Fact]
    public async Task A_relay_that_never_greets_is_unavailable_within_the_total_budget_and_its_timeouts_count()
    {
        // Accepts the connection into its backlog and never says a word, which is a relay that has hung.
        TcpListener silent = new(IPAddress.Loopback, 0);
        silent.Start();

        try
        {
            int port = ((IPEndPoint)silent.LocalEndpoint).Port;
            using NotificationsWorkerFactory host = new(
                Unreachable.Sql, Unreachable.Rabbit, mailHost: "127.0.0.1", mailPort: port);
            using MailCount counted = MailCounter.Unavailable(host.Services);
            long started = Stopwatch.GetTimestamp();

            await Should.ThrowAsync<MailUnavailableException>(() => host.Services
                .GetRequiredService<IMailChannel>()
                .SendAsync(Mail(), TestContext.Current.CancellationToken));

            Stopwatch.GetElapsedTime(started).ShouldBeLessThan(MailHop.TotalTimeout + TimeSpan.FromSeconds(2));
            counted.Of("transient").ShouldBeGreaterThanOrEqualTo(1,
                "an attempt timeout before the data is the relay's, counted by the pipeline's OnTimeout");
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.RefuseRecipientsAsync(451, ct);
        using MailCount counted = MailCounter.Unavailable(_host.Services);

        while (counted.Value < MailHop.CircuitBreakerMinimumThroughput)
        {
            await Should.ThrowAsync<MailUnavailableException>(() => Channel().SendAsync(Mail(), ct));
        }

        // The relay is healthy again, so a send that left this process now would be delivered.
        await fixture.Plain.ResetAsync(ct);

        await Should.ThrowAsync<MailUnavailableException>(() => Channel().SendAsync(Mail(), ct));

        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty(
            "once open, the breaker refuses without a connection, which is what stops a pass hammering a dead relay");
    }
}
```

The stalled row takes about seventeen seconds — two eight-second attempts and
a jittered delay — by design, as Shipping's stalled carrier takes nineteen.

`tests/Notifications.Worker.Tests/MailTlsTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Mail;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A session weaker than configured is refused before a credential or a message crosses it.</summary>
[Collection(nameof(MailpitCollection))]
public sealed class MailTlsTests(MailpitFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await fixture.Plain.ResetAsync(ct);
        await fixture.SelfSigned.ResetAsync(ct);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static OutboundMail Mail() =>
        new("aigerim@example.test", "Your order is placed", "Order 42 is placed.",
            new MailMessageId(Guid.CreateVersion7(), "order-placed"), ["en"]);

    private static NotificationsWorkerFactory StartTls(string host, int port) =>
        new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: host,
            mailPort: port,
            mailSecurity: "StartTls",
            mailUserName: "notifications",
            mailPassword: NotificationsWorkerFactory.NotARelayPassword);

    [Fact]
    public async Task Outside_development_a_relay_whose_certificate_nothing_trusts_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using NotificationsWorkerFactory factory = StartTls(Mailpit.CertificateName, fixture.SelfSigned.Port);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using MailCount counted = MailCounter.Unavailable(production.Services);

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            production.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        thrown.Cause.ShouldBe(MailFault.Tls);
        counted.Of("tls").ShouldBe(1, "a TLS refusal is a deployment's decision: counted apart, and not retried");
        (await fixture.SelfSigned.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Outside_development_a_relay_offering_no_starttls_is_refused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using NotificationsWorkerFactory factory = StartTls(fixture.Plain.Host, fixture.Plain.Port);
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            production.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        // A relay that stops offering STARTTLS is the downgrade the setting exists to refuse.
        thrown.Cause.ShouldBe(MailFault.Tls);
        (await fixture.Plain.MessagesAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task In_development_the_untrusted_certificate_is_refused_too()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using NotificationsWorkerFactory development = new(
            Unreachable.Sql,
            Unreachable.Rabbit,
            mailHost: Mailpit.CertificateName,
            mailPort: fixture.SelfSigned.Port,
            mailSecurity: "StartTls");

        MailUnavailableException thrown = await Should.ThrowAsync<MailUnavailableException>(() =>
            development.Services.GetRequiredService<IMailChannel>().SendAsync(Mail(), ct));

        // Development relaxes None and the credential, and never the trust store.
        thrown.Cause.ShouldBe(MailFault.Tls);
    }
}
```

`tests/Notifications.Worker.Tests/NoCertificateBypassTests.cs` — a test whose
subject is the source the rule is about, because a callback added to one
call site would pass every behaviour test that does not reach it:

```csharp
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The service never replaces certificate validation, so the trust store decides everywhere.</summary>
public sealed class NoCertificateBypassTests
{
    [Fact]
    public void Nothing_in_the_service_names_a_certificate_validation_callback()
    {
        string service = Path.Combine(RepositoryRoot.Locate(), "src", "Services", "Notifications");
        string[] files = Directory.GetFiles(service, "*.cs", SearchOption.AllDirectories);

        files.ShouldNotBeEmpty("a scan that read nothing reports exactly what a clean tree reports");
        files
            .Where(f => File.ReadAllText(f).Contains("CertificateValidationCallback", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }
}
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SmtpMailChannelTests|FullyQualifiedName~MailFaultTests|FullyQualifiedName~MailTlsTests|FullyQualifiedName~NoCertificateBypassTests"`
Expected: `NoCertificateBypassTests` green already — the tree has no
callback — and every other test failing on
`No service for type 'Notifications.Application.Mail.IMailChannel'`, after
both Mailpit containers have started, which proves the fixture before the
adapter exists.

- [ ] **Step 4: Write the adapter**

`Mail/SmtpMailChannel.cs`:

```csharp
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Notifications.Application.Mail;
using Polly;

namespace Notifications.Infrastructure.Mail;

/// <summary>The one place that speaks SMTP, and the platform's first output encoding.</summary>
/// <remarks>
/// A relay's exception can quote the mailbox, in its message or in the server's reply, so none leaves this class:
/// each becomes a <see cref="MailUnavailableException"/> naming the message, the phase and the reply code (§13.4).
/// </remarks>
internal sealed partial class SmtpMailChannel(
    IOptions<MailOptions> options,
    MailPipeline pipeline,
    MailMetrics metrics,
    TimeProvider clock) : IMailChannel
{
    // RFC 5321 section 4.5.3.1.3's path limit, less its angle brackets.
    private const int MaxMailboxLength = 254;

    private static readonly ParserOptions StrictAddresses = new()
    {
        AddressParserComplianceMode = RfcComplianceMode.Strict,
        AllowAddressesWithoutDomain = false
    };

    private enum Phase
    {
        Connecting,
        LoggingIn,
        Sending
    }

    public async Task<MailResult> SendAsync(OutboundMail mail, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mail);
        RefuseUnsafeHeaders(mail);

        // Refused before any connection: a line break here would be a header of the customer's choosing.
        if (Mailbox(mail.Recipient) is not { } recipient)
            return new MailResult.Refused(MailRefusal.NotAMailbox);

        using MimeMessage message = Compose(mail, recipient);

        try
        {
            return await pipeline.Pipeline.ExecuteAsync(
                attempt => AttemptAsync(message, mail.MessageId, ct, attempt),
                ct);
        }
        // The pipeline's own refusals, an open circuit or a timeout, carry no word of the relay's.
        catch (ExecutionRejectedException e)
        {
            throw new MailUnavailableException(
                $"Message {mail.MessageId.LocalPart} was not sent: {e.GetType().Name}.",
                MailFault.Transient,
                smtpStatus: null);
        }
    }

    private async ValueTask<MailResult> AttemptAsync(
        MimeMessage message,
        MailMessageId id,
        CancellationToken caller,
        CancellationToken attempt)
    {
        // Validated at start (§15.4), so none of these is null here.
        MailOptions relay = options.Value;
        Phase phase = Phase.Connecting;
        using SmtpClient client = new();

        try
        {
            await client.ConnectAsync(relay.Host!, relay.Port!.Value, Socket(relay.Security), attempt);

            phase = Phase.LoggingIn;
            if (relay.UserName is { Length: > 0 } userName && relay.Password is { Length: > 0 } password)
                await client.AuthenticateAsync(userName, password, attempt);

            phase = Phase.Sending;
            await client.SendAsync(message, attempt);
        }
        // The caller's, or an attempt timeout before the data, which the pipeline converts, counts and may retry.
        catch (OperationCanceledException) when (caller.IsCancellationRequested || phase != Phase.Sending)
        {
            throw;
        }
        catch (SmtpCommandException e)
            when (e.ErrorCode == SmtpErrorCode.RecipientNotAccepted && (int)e.StatusCode >= 500)
        {
            return new MailResult.Refused(MailRefusal.RecipientRefused);
        }
        catch (Exception e)
        {
            (MailFault cause, int? status) = Classify(e, phase);
            metrics.Unavailable(cause);

            string code = status is { } reply ? $" {reply}" : "";
            throw new MailUnavailableException(
                $"Message {id.LocalPart} met {e.GetType().Name}{code} while {Describe(phase)}.", cause, status);
        }

        await QuitAsync(client, attempt);
        return new MailResult.Accepted();
    }

    private MimeMessage Compose(OutboundMail mail, MailboxAddress recipient)
    {
        // Every header is configuration, a checked value or the clock, so no rendered value reaches one.
        MailboxAddress from = MailboxAddress.Parse(options.Value.From!);
        TextPart body = new(TextFormat.Plain);
        body.SetText(Encoding.UTF8, mail.Body);

        MimeMessage message = new()
        {
            Subject = mail.Subject,
            Date = clock.GetUtcNow(),
            MessageId = $"{mail.MessageId.LocalPart}@{from.Domain}",
            Body = body
        };
        message.From.Add(from);
        message.To.Add(recipient);
        message.Headers.Add(HeaderId.ContentLanguage, string.Join(", ", mail.Languages));

        return message;
    }

    // A subject takes no placeholder and the languages are the deployment's, so either failing is a defect upstream
    // rather than a stranger's input, and it throws rather than refusing a customer.
    private static void RefuseUnsafeHeaders(OutboundMail mail)
    {
        if (string.IsNullOrWhiteSpace(mail.Subject) || mail.Subject.Any(char.IsControl))
            throw new ArgumentException("A subject is one line of text.", nameof(mail));

        if (mail.Languages.Count == 0 || !mail.Languages.All(l => LanguageTag().IsMatch(l)))
            throw new ArgumentException("Each language is a BCP 47 tag.", nameof(mail));
    }

    // The parser's answer compared back to the input, so a display name, a comment or a second address is refused.
    private static MailboxAddress? Mailbox(string recipient) =>
        !string.IsNullOrWhiteSpace(recipient)
        && recipient.Length <= MaxMailboxLength
        && !recipient.Any(char.IsControl)
        && MailboxAddress.TryParse(StrictAddresses, recipient, out MailboxAddress? parsed)
        && parsed is { Name: null or "", Route.Count: 0 }
        && string.Equals(parsed.Address, recipient, StringComparison.Ordinal)
            ? parsed
            : null;

    // No silent fallback: an unbound value is a host the validator should not have started.
    private static SecureSocketOptions Socket(MailSecurity? security) => security switch
    {
        MailSecurity.StartTls => SecureSocketOptions.StartTls,
        MailSecurity.None => SecureSocketOptions.None,
        _ => throw new InvalidOperationException($"{MailOptions.SecurityKey} reached the relay unvalidated.")
    };

    // A 4xx is the relay declining for now; a break mid-send leaves the message's fate unknown, so is never retried.
    private static (MailFault Cause, int? Status) Classify(Exception e, Phase phase) => e switch
    {
        SmtpCommandException c when (int)c.StatusCode < 500 => (MailFault.Transient, (int)c.StatusCode),
        SmtpCommandException c when phase == Phase.LoggingIn => (MailFault.Credential, (int)c.StatusCode),
        SmtpCommandException c => (MailFault.Rejected, (int)c.StatusCode),
        SslHandshakeException => (MailFault.Tls, null),
        NotSupportedException when phase == Phase.Connecting => (MailFault.Tls, null),
        AuthenticationException or NotSupportedException when phase == Phase.LoggingIn => (MailFault.Credential, null),
        _ when phase == Phase.Sending => (MailFault.Unconfirmed, null),
        _ => (MailFault.Transient, null)
    };

    private static string Describe(Phase phase) => phase switch
    {
        Phase.Connecting => "connecting",
        Phase.LoggingIn => "logging in",
        _ => "sending"
    };

    // The relay has answered the data with 250, so a QUIT that fails is no failed send.
    private static async Task QuitAsync(SmtpClient client, CancellationToken attempt)
    {
        try
        {
            await client.DisconnectAsync(quit: true, attempt);
        }
        catch (Exception e)
            when (e is IOException or SocketException or SmtpProtocolException or OperationCanceledException)
        {
        }
    }

    [GeneratedRegex("^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageTag();
}
```

What each branch is for, against section 4's table:

| The relay | Where | The port |
|---|---|---|
| `250` to the data | `SendAsync` returns | `Accepted` |
| `5xx` to `RCPT TO` | `RecipientNotAccepted`, `>= 500` | `Refused(RecipientRefused)` |
| a mailbox with CR, LF, a name, a comment or a second address | `Mailbox` | `Refused(NotAMailbox)`, no connection |
| `4xx` at any command, the data's included | `Transient` | retried, then thrown |
| refused connection, DNS failure, timeout before the data | `Transient` or the pipeline's `TimeoutRejectedException` | retried, then thrown |
| a break or a timeout during the data | `Unconfirmed` | thrown, not retried |
| an untrusted certificate, or no `STARTTLS` under `StartTls` | `Tls` | thrown, not retried |
| `AUTH` refused or not offered | `Credential` | thrown, not retried |
| `5xx` to the sender or to the data | `Rejected` | thrown, not retried |
| an open circuit | `ExecutionRejectedException` | thrown, no connection, not counted |

A `4xx` answering the data is `transient` too, though the data was sent: the
relay has said it holds nothing, so a retry cannot be the duplicate §9.7's
fourth rule guards against, and the spec's "before the message was handed
over" is read as "before the relay could have taken it".

`MailKit.Security.AuthenticationException` is the one `Classify` names: the
file imports `MailKit.Security` and not `System.Security.Authentication`, and
the SSL stream's own exception reaches it wrapped in `SslHandshakeException`.

In `Mail/DependencyInjection.cs`, after the pipeline's line, with
`using Notifications.Application.Mail;` in sorted position:

```csharp
        // A singleton, so every scope shares the pipeline above rather than meeting a dead relay afresh.
        services.AddSingleton<IMailChannel, SmtpMailChannel>();
```

- [ ] **Step 5: Run the adapter suites and the solution**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~SmtpMailChannelTests|FullyQualifiedName~MailFaultTests|FullyQualifiedName~MailTlsTests|FullyQualifiedName~NoCertificateBypassTests"
dotnet test Platform.slnx --filter "Category!=Integration"
dotnet build Platform.slnx
```

Expected: green, with a Docker daemon running — these are
`Category=Integration` through their collection and are never skipped, so
without one they fail on `Failed to connect to Docker endpoint`, which is the
correct failure; the stalled row about seventeen seconds and the open-circuit
row about four; the solution green without containers; 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Services/Notifications tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "feat(notifications): the SMTP adapter behind IMailChannel, against Mailpit"
```

---

### Task 5: The relay in Compose

**Files:**
- Modify: `deploy/compose/services/notifications.yml` — the `mailpit` service,
  the worker's four `Mail__*` keys and its `depends_on`
- Test: `tests/Notifications.Worker.Tests/MailpitImageTests.cs`

The relay mounts nothing, so it has no directory of its own beside
`carrier-simulator/` and `psp-simulator/`, and `.gitattributes` gains no line.
`docker-compose.infra-only.yml` gains nothing either: Mailpit is a dependency,
not an application service, so the override keeps it running, as it keeps the
two simulators, and its loopback SMTP port is what a host-run worker submits
to.

- [ ] **Step 1: Write the failing test**

`tests/Notifications.Worker.Tests/MailpitImageTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The sink Compose runs is the one the suite asserts against, and its ports bind loopback alone.</summary>
public sealed partial class MailpitImageTests
{
    private static string Unit() =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Locate(), "deploy", "compose", "services", "notifications.yml"));

    [Fact]
    public void The_compose_sink_is_the_image_the_suite_starts()
    {
        MatchCollection images = MailpitImage().Matches(Unit());

        images.Count.ShouldBe(1);
        images[0].Groups["image"].Value.ShouldBe(Mailpit.Image);
    }

    [Theory]
    [InlineData(Mailpit.SmtpPort)]
    [InlineData(Mailpit.ApiPort)]
    public void Each_of_the_sinks_ports_is_published_on_loopback_and_nowhere_wider(int port)
    {
        string[] mappings =
        [
            .. PortsLine().Matches(Unit())
                .SelectMany(m => m.Groups["ports"].Value.Split(','))
                .Select(p => p.Trim().Trim('"'))
                .Where(p => p.EndsWith($":{port}", StringComparison.Ordinal))
        ];

        // The UI shows every message to whoever reaches it, and SMTP takes anyone's submission (§14.1).
        mappings.ShouldHaveSingleItem().ShouldBe($"127.0.0.1:{port}:{port}");
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>axllent/mailpit:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex MailpitImage();

    [GeneratedRegex(@"^\s*ports:\s*\[(?<ports>[^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex PortsLine();
}
```

Run: `dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailpitImageTests"`
Expected: `The_compose_sink_is_the_image_the_suite_starts` fails on a count of
0, and both port rows fail on `ShouldHaveSingleItem` — PR-1's unit has no
sink.

- [ ] **Step 2: The Compose unit**

In `services/notifications.yml`, beside the migrator and the worker:

```yaml
  # Notifications' relay, a sink: every message the stack sends lands here and nowhere else. Both ports bind
  # loopback alone: the UI shows every message to whoever reaches it, and SMTP is what a worker run on the host
  # under docker-compose.infra-only.yml submits to. The tag is the one the suite starts (§14.1).
  mailpit:
    image: axllent/mailpit:v1.31.3
    environment:
      # The API that stages a refusing or failing relay, at PUT /api/v1/chaos.
      MP_ENABLE_CHAOS: "true"
    ports: [ "127.0.0.1:1025:1025", "127.0.0.1:8025:8025" ]
```

On the worker's environment, after `OTEL_EXPORTER_OTLP_ENDPOINT`:

```yaml
      # The relay, read and validated at start (§15.4). None and no credential are Development's alone: the
      # sink takes plain, unauthenticated submission, so this unit carries no Mail__UserName or Mail__Password.
      Mail__Host: "mailpit"
      Mail__Port: "1025"
      Mail__From: "Commerce <no-reply@commerce.test>"
      Mail__Security: "None"
```

and under its `depends_on`, `mailpit: { condition: service_healthy }` — the
image declares its own `HEALTHCHECK`, `/mailpit readyz`, so the condition has
something to wait on.

Host ports 1025 and 8025 are free: no other unit and nothing in
`infrastructure.yml` publishes either, which
`grep -E 'ports:' deploy/compose/*.yml deploy/compose/services/*.yml` shows.

- [ ] **Step 3: Prove the relay under Compose**

```bash
docker compose -f deploy/compose/docker-compose.yml config -q
docker compose -f deploy/compose/docker-compose.yml up -d --wait
curl -s http://localhost:8025/api/v1/info
docker compose -f deploy/compose/docker-compose.yml port mailpit 1025
docker compose -f deploy/compose/docker-compose.yml port mailpit 8025
curl -s -X POST http://localhost:8025/api/v1/send -H 'Content-Type: application/json' \
    -d '{"From":{"Email":"no-reply@commerce.test"},"To":[{"Email":"aigerim@example.test"}],"Subject":"ә ғ қ ң ө ұ ү һ і","Text":"probe"}'
curl -s http://localhost:8025/api/v1/messages
docker compose -f deploy/compose/docker-compose.yml ps notifications-worker
docker compose -f deploy/compose/docker-compose.yml down -v
```

Expected: `config -q` silent; the stack up with `mailpit` healthy; the info
call answering a JSON object naming `v1.31.3`; the two `port` calls printing
`127.0.0.1:1025` and `127.0.0.1:8025`; the message list holding the probe with
its subject intact; `notifications-worker` running, which is its `Mail`
options validating under Development with Compose's four keys. Where a native
broker on this machine holds 5672, bring the stack up with the usual
scratchpad-only port override for that service and nothing else.

Then the image test:

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~MailpitImageTests"
```

Expected: green.

- [ ] **Step 4: Commit**

```bash
git add deploy/compose/services/notifications.yml tests/Notifications.Worker.Tests/MailpitImageTests.cs
git commit -m "feat(notifications): Mailpit as the Compose relay, SMTP and UI on loopback"
```

---

### Task 6: The meter's export

**Files:**
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` — `Notifications.Outbound`
  joins the `Required` list; written first and seen to fail
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` —
  `.AddMeter("Notifications.Outbound")`

Skip both steps if PR-3 already landed the line and its entry, and say so in
the PR body.

- [ ] **Step 1: The failing assertion**

Add `"Notifications.Outbound",` to `ObservabilityTests`' `Required` list,
after `"Shipping.Outbox"`. Run
`dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~ObservabilityTests"`
and see `Every_meter_an_alert_reads_from_is_collected` fail. The test holds
its own copy of the list on purpose, which is why the assertion is written
before the registration and not with it.

- [ ] **Step 2: The registration**

In `ObservabilityExtensions`, after `.AddMeter("Shipping.Outbox")`, the comment
aligned with its neighbours':

```csharp
                .AddMeter("Notifications.Outbound")                // Notifications' outbound calls (§3.2)
```

Run the same filter and see it pass.

- [ ] **Step 3: Commit**

```bash
git add src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs tests/Common.Web.Tests/ObservabilityTests.cs
git commit -m "feat(observability): export the Notifications.Outbound meter"
```

---

### Task 7: The chapters, `docs/secrets.md` and the scan

**Files:**
- Modify: `docs/backend-architecture/14-local-development.md` — the endpoint
  table's two rows, the paragraph after it, and the relay's paragraph
- Modify: `docs/backend-architecture/09-messaging.md` — §9.7's two lists of
  third-party hops gain `MailHop`
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's six rows
  and its options-type sentence
- Modify: `docs/backend-architecture/12-test-strategy.md` — §12.7's row
- Modify: `docs/secrets.md` — the rotation sentence and the local-development
  paragraph
- Modify: `.github/secret-scan/allowed/tests.txt` — the fixture's relay
  password

- [ ] **Step 1: §14.1**

The endpoint table gains two rows after Grafana's:

```markdown
| Mail sink (Mailpit) | http://localhost:8025 — every message the stack sends; no login |
| Mail relay (SMTP) | `localhost:1025` — plain and unauthenticated, Development's alone |
```

The paragraph after it ends "the Keycloak, RabbitMQ and Grafana rows have
been true since PR-06." and gains, as its last sentence: "The two mail rows
arrived with Notifications' relay."

After the paragraph that ends "that port is published to no host and reached
by no route.", a new paragraph:

```markdown
**Notifications' relay is a sink, and both its ports bind loopback alone.**
Mailpit runs in Notifications' own unit beside the worker that submits to it,
which reaches it as `mailpit:1025` on the Compose network. Its SMTP port and
its web UI and HTTP API are published on `127.0.0.1:1025` and
`127.0.0.1:8025`, the endpoint table's two rows: the UI because it shows
every message the stack has sent to whoever reaches the port, and SMTP so a
worker run on the host under the override below has a relay, at
`Mail__Host` `localhost`. The relay's local defaults are `Mail__Host`
`mailpit`, `Mail__Port` `1025` and `Mail__Security` `None`, with no
`Mail__UserName` and no `Mail__Password`: the sink takes unauthenticated
submission, and plain, unauthenticated submission is Development's alone —
the host refuses either anywhere else ([§15.4](15-cicd-deployment.md)). The
image's tag is the unit file's, and the suite starts the same one, so the
sink a person watches is the sink the tests read.
```

§14.2 is not edited: it is the Aspire sample, which runs no Notifications
resource and by its own rule declares nothing for a service it does not run.

- [ ] **Step 2: §15.4**

The configuration table gains six rows after `Fulfilment__GiveUpAge`, in
its existing column form:

```markdown
| `Mail__Host` | Config | ConfigMap | ✓ — **Notifications only**; the relay's host name, and the host refuses to start without it |
| `Mail__Port` | Config | ConfigMap | ✓ — **Notifications only**; the relay's submission port |
| `Mail__From` | Config | ConfigMap | ✓ — **Notifications only**; the one sender every message carries, and the domain each `Message-ID` is minted under |
| `Mail__Security` | Config | ConfigMap | ✓ — **Notifications only**; `StartTls` or `None`, and `None` refuses to start outside Development |
| `Mail__UserName` | Config | ConfigMap | ✓ **outside Development** — **Notifications only**; set with the password or not at all |
| `Mail__Password` | Secret | External Secrets | ✓ **outside Development** — **Notifications only**; the relay's credential, and absent in Compose, where the sink takes unauthenticated submission |
```

In the paragraph that opens "**Every options type in the solution had to
earn it.**", the list gains `Mail` after `Identity:Client`'s clause:
"`Identity:Client` holds a secret that differs per environment, `Mail` holds
Notifications' relay and its credential on the same terms, `Jurisdiction`
holds …". Nothing else in the paragraph moves.

- [ ] **Step 3: §12.7 and §9.7**

The test-doubles table gains a row after "Third-party API":

```markdown
| Third-party relay (SMTP) | Mailpit — a real SMTP server in a container, read back through its HTTP API |
```

§9.7 names the third-party hops twice, and each list gains `MailHop`. In
*Rules for every synchronous call*, rule 1's last two sentences, which read
"A third party sits outside the band, and `ProviderHop` and `CarrierHop` are
each sized to theirs: a third party's latency is not a peer's, and neither
call has a waiting caller — the provider's fits the saga's payment wait
(§9.6), and the carrier's a worker's leased row.", become:

```markdown
   taste — the attempts plus their backoff have to fit the client total. A
   third party sits outside the band, and `ProviderHop`, `CarrierHop` and
   `MailHop` are each sized to theirs: a third party's latency is not a
   peer's, and none of the three calls has a waiting caller — the provider's
   fits the saga's payment wait (§9.6), and the carrier's and the relay's a
   worker's leased row.
```

And in the paragraph after the rules, the sentence "The outbound clients of
the other kind call third parties — Payments' `ProviderHop`, behind the
anti-corruption layer §3.1 gives Payments, and Shipping's `CarrierHop`, to
the carrier — and each is registered by the service that makes the call."
becomes, its neighbours' wrapping unchanged:

```markdown
the other kind call third parties — Payments' `ProviderHop`, behind the
anti-corruption layer §3.1 gives Payments, Shipping's `CarrierHop`, to the
carrier, and Notifications' `MailHop`, to its relay — and each is registered
by the service that makes the call.
```

Rule 4, "Retry only idempotent operations", is not edited: `MailHop`'s
pipeline is built to it, retrying nothing the relay may already hold, and
`MailHop`'s own remarks cite the rule.

- [ ] **Step 4: `docs/secrets.md`**

In *Rotation*, the first sentence's list ends "…`PaymentProvider__ApiKey`,
for Payments' provider behind §3.2's anti-corruption layer, `Carrier__ApiKey`,
for Shipping's carrier behind the same, and `Mail__Password`, for
Notifications' relay ([§15.4](backend-architecture/15-cicd-deployment.md))."
— the `and` moves from before `Carrier__ApiKey` to before `Mail__Password`.

In *Local development is a deliberate exception*, after the paragraph ending
"a variable would override a value no local party compares.", a new
paragraph:

```markdown
**The relay has no row, because Compose carries no relay credential at all.**
Mailpit takes unauthenticated submission and the host allows that in
Development alone, so `Mail__UserName` and `Mail__Password` are absent from
the unit rather than defaulted, and the one place a relay password is a fake
is the test fixture, for the hosts it runs as Production.
```

The five places, accounted for: Compose is Task 5's deliberate absence,
stated in the unit; §15.4's row is Step 2; the fixture is Task 3's
`NotificationsWorkerFactory.NotARelayPassword`; §14.2's Aspire host is not
adopted; the chart's values are PR-6's, with the chart.

- [ ] **Step 5: The scan**

```bash
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

`NotARelayPassword`'s literal in `NotificationsWorkerFactory.cs` is flagged
under `credential-assignment`, and nothing else is: the validator's
`hasPassword`, the `Relay` helper's `relayPassword` and the factory's
`mailPassword` are named so that `connection-string-password`, which wants
`password` as a word of its own, passes them, and `src/` owes no entry.
Add the entry to
`.github/secret-scan/allowed/tests.txt` in the file's four-column form, under
the fixtures heading, with the fingerprint the gate prints and the reason
"A relay password that says what it is, for the hosts run as Production;
nothing accepts it." Run both again; both exit 0. Compose adds no finding,
because it carries no credential. This plan's own text prints the same
literal, which is a finding under `.github/secret-scan/allowed/docs.txt`
owed by whichever pull request commits the plan file.

- [ ] **Step 6: Check the chapters and commit**

Run `/check-links` and `/validate-blueprint`.

```bash
git add docs/backend-architecture/09-messaging.md docs/backend-architecture/12-test-strategy.md \
    docs/backend-architecture/14-local-development.md docs/backend-architecture/15-cicd-deployment.md \
    docs/secrets.md .github/secret-scan/allowed/tests.txt
git commit -m "docs: §14.1's mail relay, §15.4's six Mail keys, MailHop in §9.7, §12.7's relay row"
```

---

### Task 8: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green, with a Docker daemon running.
- [ ] `cd .github/licence-gate && py -3.12 -m unittest && py -3.12 licence_gate.py` — exit 0.
- [ ] `py -3.12 -m unittest discover -s .github/secret-scan` then
  `py -3.12 .github/secret-scan/secret_scan.py` — both exit 0.
- [ ] `git fetch origin main` then
  `py -3.12 .github/comment-gate/comment_gate.py --base origin/main` — exit 0,
  run after committing because the gate judges `HEAD`; no comment block
  added here runs past five lines, names a pull request or a test, or
  stresses a word.
- [ ] PR body: `| Class | A+D+E |`, the touch set as paths alone with the
  reasons under the table, Task 2's two Polly measurements and Task 5's
  Compose answers as evidence, and a line saying whether Task 6 found PR-3's
  meter line already present. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 1, email only through one port, MailKit and MimeKit, Mailpit pinned
  and never `latest`, its SMTP port and UI on loopback alone, the same image
  under
  Testcontainers read through its API, `text/plain; charset=utf-8` → Tasks 1,
  2, 4 and 5.
- Section 2's §14.1, §12.7 and §9.7 amendments and Appendix B's three rows,
  `Polly.Core` among them → Tasks 2 and 7; §15.4's rows and the options-type
  sentence → Task 7.
- Section 3's PR-2 row — the port, the adapter, `MailHop` and the breaker,
  `MailOptions` with the STARTTLS refusal, the counter and its `AddMeter`
  line, the Mailpit unit, §14.1's, §12.7's and §9.7's amendments, §15.4,
  Appendix B, the Kazakh-script subject through the sink → Tasks 1–7.
- Section 4's table, row by row → Task 4's table and suites: `250` to
  `Accepted`, a recipient's `5xx` to `Refused(RecipientRefused)`, a mailbox
  that does not parse to `Refused(NotAMailbox)` with no connection, and every
  other row a `MailUnavailableException` whose cause is `transient` (retried,
  but for the open circuit, which is thrown at once), `unconfirmed`, `tls`,
  `credential` or `rejected` (none retried). `MailHop`'s numbers,
  strictly below `ServiceOptions.OperationTimeout` and above §9.7's band,
  the breaker sized to the tick and asserted able to open, and the tick
  itself → Task 3, with the opened circuit in Task 4. The `Message-ID` form
  `<{EventId:N}.{TemplateKey}@{domain of Mail:From}>` → Tasks 1 and 4.
- Section 8, no interpolated value in a header and a CR or LF mailbox refused
  → Task 4, each header's source named in `Compose` and each refusal tested.
- Section 9's mail port, the exception that carries no mailbox and no server
  text, `MailOptions`' six keys, `None` and a missing credential refused
  outside Development, no certificate callback anywhere and the self-signed
  relay refused → Tasks 1, 3 and 4.
- Section 11's six `Mail__*` keys → Task 3's factory, Task 5's unit (four,
  the credential pair absent by design) and Task 7's rows.
- Section 12's `Notifications.Outbound` meter, `notifications.mail.unavailable`
  with its `cause` attribute in section 4's five lowercase words, and the
  `AddMeter` line → Tasks 3 and 6.
- Section 13's mail-adapter rows, the TLS refusal, the fault that carries no
  mailbox, the Kazakh-script subject and body, the inequality and the opened
  circuit → Tasks 3 and 4.
- Section 14's PR-2 places — §14.1, §12.7, §9.7, Appendix B, §15.4, and
  `docs/secrets.md`'s relay password → Tasks 2 and 7; §14.2 is not moved,
  by section 2.

**Type consistency.** `IMailChannel`, `OutboundMail`, `MailMessageId`,
`MailResult.Accepted/Refused`, `MailRefusal`, `MailFault` and
`MailUnavailableException` are Task 1's and consumed by Tasks 3 and 4 under
those spellings. `MailHop`, `OutboundMeter.Name`, `MailMetrics`,
`MailSecurity`, `MailOptions` with its six keys, `MailOptionsValidator`,
`MailPipeline` and `AddMailChannel(IConfiguration, IHostEnvironment)`, called
from `Program.cs` after the layer's method (ADR-055), are Task 3's; `SmtpMailChannel` and its
registration are Task 4's. `MetricsInitialiser` leaves Task 3 with three
parameters — PR-1's two and `MailMetrics` — which is the signature PR-3 and
PR-5 each extend. `NotificationsWorkerFactory`'s six parameters and four
constants are Task 3's and read by Tasks 3 and 4; `Mailpit` and its records
are Task 4's and read by Task 5's image test and, later, PR-5's fixture.

**Left to a later PR.**

- Any caller of the port: the send worker, its claim and lease, the intent
  stamp and `notifications.mail.resent`, the resend under one `Message-ID`
  after a staged crash, the breaker's park of the claim and the lease above
  `MailHop.TotalTimeout` plus `ContactHop`'s are PR-5's. `MailHop.SendTick`
  is declared here because the budget and the tick are one class, and
  nothing reads it yet.
- PR-5 records `MailRefusal.NotAMailbox` as `Undeliverable: not_a_mailbox`
  and `RecipientRefused` as `Undeliverable: recipient_refused`, logs a `tls`,
  `credential` or `rejected` fault as an error, resends an `unconfirmed` one
  over its intent stamp, and reads the exported logs and faults of a run
  over a known mailbox for section 12's no-mailbox rule; this PR proves the
  exception's half of that.
- The chart's `mail` capability and the relay's values, which are the fifth
  of `docs/secrets.md`'s places, are PR-6's.
- ADR-053 rule 3's naming of a real relay's country is owed with the first
  real relay, by the spec's own deferral.

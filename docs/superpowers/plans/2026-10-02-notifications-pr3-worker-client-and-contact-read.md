# Notifications PR-3 — the notifications-worker client and the contact read — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mint the realm's `notifications-worker` client — a confidential
service account holding `view-users` on `realm-management` and nothing else,
taking no `commerce-api` scope — and give Notifications the reader ADR-052
decides: `IContactSource`, a Keycloak admin-API adapter under `ContactHop`'s
budget, the grant-checked token cache over `resource_access.realm-management.roles`,
the `ContactRecords` table and its store, and `ContactOptions`. Every asserted
test ADR-052's table names that a third credentialed client turns red is turned
green here, by naming the set. Nothing calls the source yet.

**Architecture:** Two halves, as Shipping's PR-4 and PR-5 split them, in one
pull request because the reader and the client are each other's only consumer.
The **realm half** is a client object, its service-account user and the two
static suites that own the realm's closed sets, plus a predicate in
`realm_check.py` for everything a client object states. The **service half**
follows ADR-055: `IContactSource` and its vocabulary in
`Notifications.Application.Contacts`, the adapter and its hop in
`Notifications.Infrastructure.Contacts` registered by `AddContactSource`, which
`Program.cs` calls beside the host's own client-credential bindings. The adapter
is a typed `HttpClient` — resilience outermost, `ClientCredentialsHandler`
inside it, a body buffer innermost — and binds three members of Keycloak's user
representation by `JsonDocument`, never by a type that could hold the rest. A
refusal is decided in one of two places and counted where it is decided: the
grant check on the token this host was issued, or the owner's `401`/`403`.

**Tech Stack:** `Microsoft.Extensions.Http.Resilience` (pinned), `System.IdentityModel.Tokens.Jwt`
(pinned), Dapper and EF Core over SQL Server, `System.Diagnostics.Metrics`,
Keycloak 26.0's realm export, stdlib Python 3.12 for the realm gate, xUnit with
Shouldly, Testcontainers (`Testcontainers.Keycloak`, pinned) and WireMock.Net
in process (pinned).

**Spec:** `docs/superpowers/specs/2026-10-02-notifications-service-design.md`,
sections 1 (the realm keeps internationalisation off), 2 (the realm gains
`notifications-worker`), 3 (PR-3's row and its order), 6 (`ContactRecords`,
its erasure path, `AddContactRecords`), 9 (the contact port, its adapter, the
refusals and the locale's bound; the dependency table's two Keycloak rows), 11
(`ContactSource__BaseUrl`, `ContactSource__Realm`, the three
`Identity__Client__*` keys and the client secret's places), 12
(`notifications.contact.refused`, no mailbox in a log), 13 (the contact
adapter's suite against a real Keycloak, PR-3's realm half) and 14 (the rows of
ADR-052's table PR-3 takes), read with
[ADR-052](../../backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
in full and
[ADR-055](../../backend-architecture/adr/ADR-055-an-outbound-hop-registers-beside-its-layer-and-the-host-calls-it.md).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set:

  `src/Services/Notifications/**`, `src/BuildingBlocks/Common.Infrastructure/Identity/ServiceIdentityOptions.cs`, `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`, `tests/Notifications.*`, `tests/Common.Web.Tests/RealmImportTests.cs`, `tests/Common.Web.Tests/ObservabilityTests.cs`, `tests/Web.Bff.Tests/RealmClientTests.cs`, `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, `tests/Web.Bff.Tests/KeycloakFixture.cs`, `Directory.Packages.props`, `deploy/compose/keycloak/realm-export.json`, `deploy/compose/services/notifications.yml`, `deploy/compose/.env.example`, `deploy/compose/README.md`, `deploy/keycloak/realm_check.py`, `deploy/keycloak/test_realm_check.py`, `deploy/keycloak/README.md`, `.github/secret-scan/allowed/deploy.txt`, `.github/secret-scan/allowed/tests.txt`, `docs/backend-architecture/02-architecture-at-a-glance.md`, `docs/backend-architecture/03-bounded-contexts.md`, `docs/backend-architecture/04-solution-structure.md`, `docs/backend-architecture/09-messaging.md`, `docs/backend-architecture/11-identity-authorization.md`, `docs/backend-architecture/12-test-strategy.md`, `docs/backend-architecture/14-local-development.md`, `docs/backend-architecture/15-cicd-deployment.md`, `docs/backend-architecture/appendix-b-licences.md`, `docs/secrets.md`

  Why each, since the row is paths only: the service's code and its three
  test projects are A, and so are the two building-block files — the one
  sentence of `ServiceIdentityOptions` that this PR makes false, and
  `Common.Web`'s `AddMeter` line with its test, under A's
  `src/BuildingBlocks/**` and `tests/**`; `RealmImportTests` and
  `RealmClientTests` are A, the two suites ADR-052 names as asserted, and so
  are `Web.Bff.Tests.csproj`'s comment and `KeycloakFixture`'s summary, which
  call that suite's Keycloak the only one; `Directory.Packages.props` is E for
  the same claim in the comment over `Testcontainers.Keycloak`'s pin, which
  does not move (Task 8, Step 9). The realm, the Compose unit, `.env.example`,
  the Compose README, the realm gate and its README, the two allow-lists, the
  chapters and `docs/secrets.md` are D. **The E letter is owed for package
  references, not pins**: `Notifications.Infrastructure.csproj` takes
  `Microsoft.Extensions.Http.Resilience` and `System.IdentityModel.Tokens.Jwt`
  (and `Microsoft.Extensions.Hosting.Abstractions` unless PR-2 landed it), and
  `Notifications.Worker.Tests.csproj` takes `Testcontainers.Keycloak`,
  `WireMock.Net`, `System.IdentityModel.Tokens.Jwt` and
  `Microsoft.Extensions.Http.Resilience`, none with a `Version=`; and Appendix
  B's Testcontainers and `System.IdentityModel.Tokens.Jwt` rows, whose *use*
  this PR makes false. No pin moves in `Directory.Packages.props`, no project
  joins `Platform.slnx`, and the `*.csproj` files sit inside paths the row
  already names.
- **`A+D+E` is the one three-member cell the class row accepts**, and
  `.github/locality-gate/locality_gate.py` admits it, so the row is spelled
  exactly that way and no gate change is owed.
- **Depends on PR-1 having merged.** These are PR-1's names, rendered by the
  scaffold's pure-consumer mode, consumed as spelled here:
  `src/Services/Notifications/Notifications.Application`,
  `Notifications.Infrastructure`, `Notifications.Migrator` and
  `Notifications.Worker`; `tests/Notifications.Application.Tests`,
  `tests/Notifications.Worker.Tests` and `tests/Notifications.TestSupport`;
  `AddNotificationsApplication()` in `Notifications.Application.DependencyInjection`,
  registering `TimeProvider.System` and `RequestMetrics` as Shipping's does;
  `AddNotificationsInfrastructure(IServiceCollection, IConfiguration)` in
  `Notifications.Infrastructure.DependencyInjection`, registering
  `new RetentionPolicy()` and `IDbConnectionFactory` on the `Notifications`
  runtime key; `Notifications.Infrastructure.Persistence.NotificationsDbContext`
  with schema `notifications` and `ApplyConfigurationsFromAssembly`, and its
  migrations under `Persistence/Migrations`;
  `Notifications.Infrastructure.Observability.MetricsInitialiser`;
  `Notifications.Worker/Program.cs` in Shipping's worker shape, ending its
  registrations with `builder.Services.AddNotificationsInfrastructure(builder.Configuration);`;
  `NotificationsWorkerFactory(string connectionString, string rabbitConnectionString, …)`
  over that `Program`, with `UnreachableAuthority` and a virtual
  `ConfigureAuthentication`; `Notifications.TestSupport.ServiceFixture` with
  `ResetAsync`, `ScalarAsync`, `ExecuteAsync` and `AppliedMigrationsAsync`;
  `IntegrationCollection`; `DatabaseSmokeTests`; `MetricsRegistrationTests`
  with `BuildServices()` and `The_metrics_selector_actually_selects_something`;
  Application's `ArchitectureTests`; and `deploy/compose/services/notifications.yml`
  with a `notifications-worker` service running as Development. Where PR-1
  spelled one of these differently, the spelling moves and nothing else in this
  plan does.
- **PR-2 may land first, in either order, and five things are shared.**
  `Notifications.Infrastructure/Observability/OutboundMeter.cs`, the
  `.AddMeter("Notifications.Outbound")` line in `ObservabilityExtensions` with
  its `ObservabilityTests` entry, `tests/Notifications.Worker.Tests/Unreachable.cs`,
  `tests/Notifications.Worker.Tests/RepositoryRoot.cs` and the
  `Microsoft.Extensions.Hosting.Abstractions` reference in
  `Notifications.Infrastructure.csproj` are each written by whichever of the
  two lands first; the second finds the file or line present and adds nothing,
  and its PR body says so. Four edits are additive in both orders and are
  written so: `MetricsInitialiser` gains `ContactMetrics` as its **last**
  parameter, after whatever PR-2 added; `NotificationsWorkerFactory` gains
  one defaulted parameter **after its last one**, which every caller in this
  plan passes **by name**; `Program.cs` holds one order whichever lands
  first — `AddNotificationsInfrastructure`, then PR-2's `AddMailChannel`,
  then `AddContactSource`; and `MetricsRegistrationTests` gains its
  `TestEnvironment`, its `BuildServices` summary, its selector comment and
  its shared usings from whichever lands first, Shipping's forms in both.
- **No new pin and no Appendix B identity.** `Microsoft.Extensions.Http.Resilience`,
  `System.IdentityModel.Tokens.Jwt`, `Testcontainers.Keycloak` and
  `WireMock.Net` are pinned and registered; Appendix B's two rows move only in
  their *use* column (Task 8).
- **ADR-052 is the governing record**, and its closing table is the list of
  places that move. Every row this PR turns red is named in the task that turns
  it green; every row left to a later PR is named in *Self-review*. **The rule
  for each is to name the set or cite ADR-052, never to write "three"**, so the
  next credentialed host moves no prose.
- **The grant, verified against the pinned image rather than assumed.**
  §14.1's realm export is a full Keycloak 26.0 export, and in it
  `realm-management`'s `view-users` is `"composite": true` with
  `"composites": { "client": { "realm-management": ["query-groups", "query-users"] } }`,
  and neither of those two composes further. The service account is assigned
  `view-users` alone; Keycloak expands composites into the token, so the claim
  the worker checks is exactly `["query-groups", "query-users", "view-users"]`.
  Task 2 asserts the composition against the export and Task 6 against a token
  Keycloak issued.
- **The scope the worker requests is `roles`.** `ServiceIdentityOptions.Scope`
  is `[Required]` and `CachingTokenClient` posts it on every grant, and
  Keycloak refuses a scope the client does not hold with `invalid_scope`, so
  `commerce-api` is not available to this client by design. `roles` is the
  built-in default scope whose *client roles* mapper writes `resource_access`
  — the claim the grant check reads — so it is the one scope this host's grant
  depends on, and naming it makes that dependency visible in configuration.
- **No log line or exception message holds a mailbox, a token or a role name**
  — the log takes ids (spec, section 12). `SensitiveKeys` is not widened.
  `ContactLookup.Found` and `ContactRecord` override `ToString` so a record
  printed by mistake holds no mailbox either.
- Comments say why and cite the owner — a section, an ADR or a symbol, never a
  pull request, a test or the superpowers spec. **The comment gate's limit is a
  block of five lines** (`.github/comment-gate/comment_gate.py`'s
  `BLOCK_LIMIT`): a summary is one sentence, a `<remarks>` is cited and at most
  four lines, a Python docstring counts every line it spans, and **a touched
  block is judged whole** — an existing block this plan edits is rewritten to
  five lines or fewer. Explicit local types, file-scoped namespaces with a
  blank line after, braces on two or more statements and on one that wraps,
  one space before `=`, `=>` and `{`, 120 columns for code and 80 for prose,
  British spelling. An exception message that interpolates is **one**
  interpolated literal, never `$"…" + $"…"` with an `int` in it, which is the
  CS1620 trap `CachingTokenClient.Failure` documents.
- `py -3.12`, never `python`. Container tests are in a collection carrying
  `[Trait("Category", "Integration")]` and are never skipped.
- Every step that adds behaviour writes its test first.

---

### Task 1: The contact port, its vocabulary and `ContactOptions`

**Files:**
- Create: `src/Services/Notifications/Notifications.Application/Contacts/IContactSource.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/ContactLookup.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/ContactSourceRefusedException.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/IContactStore.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/ContactRecord.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/ContactLimits.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/LanguageTag.cs`
- Create: `src/Services/Notifications/Notifications.Application/Contacts/ContactOptions.cs`
- Modify: `src/Services/Notifications/Notifications.Application/DependencyInjection.cs`
  — `ContactOptions` registered
- Test: `tests/Notifications.Application.Tests/ContactPortTests.cs`
- Test: `tests/Notifications.Application.Tests/LanguageTagTests.cs`
- Test: `tests/Notifications.Application.Tests/ContactOptionsTests.cs`

**Interfaces:**
- Consumes: nothing of PR-1's beyond the project and `AddNotificationsApplication`.
- Produces:

```csharp
namespace Notifications.Application.Contacts;

public interface IContactSource
{
    Task<ContactLookup> GetAsync(Guid customerId, CancellationToken ct);
}

public abstract record ContactLookup
{
    public sealed record Found(string Email, string? Locale) : ContactLookup;
    public sealed record NoSuchCustomer : ContactLookup;
}

public sealed class ContactSourceRefusedException : Exception;   // the three standard constructors

public interface IContactStore
{
    Task SaveAsync(Guid customerId, ContactLookup.Found contact, DateTimeOffset fetchedAt, CancellationToken ct);
    Task<ContactRecord?> GetAsync(Guid customerId, CancellationToken ct);
    Task DeleteAsync(Guid customerId, CancellationToken ct);
}

public sealed record ContactRecord(string Email, string? Locale, DateTimeOffset FetchedAt);

public static class ContactLimits { public const int MaxEmailLength = 255; }

public static class LanguageTag { public const int MaxLength = 35; public static bool IsOne(string? value); }

public sealed record ContactOptions   // () = 15 min / 24 h; (TimeSpan freshness, TimeSpan staleCeiling)
{
    public TimeSpan Freshness { get; }
    public TimeSpan StaleCeiling { get; }
}
```

**Why `LanguageTag` is not a regular expression.** Application's
`ArchitectureTests` is an exact allow-list of referenced assemblies, and
`[GeneratedRegex]` emits a reference to `System.Text.RegularExpressions`, which
§4.2's second row does not list. Split-and-compare uses `System.Runtime` and
`System.Linq` alone, both already on the list, and says the same thing.

**Why `ContactOptions` has constructors and no `init`.** A cross-field rule
under `init` setters is order-dependent: `{ StaleCeiling = 10 min, Freshness =
5 min }` would be refused at the first setter, against the default freshness,
though the finished value is legal. A constructor sees both numbers at once.
Get-only properties also make `with` a compile error, so no copy can bypass the
check — which `RetentionPolicy`'s `init` validators, each on one member, never
needed.

- [ ] **Step 1: Write the failing tests**

`tests/Notifications.Application.Tests/ContactPortTests.cs`:

```csharp
using System.Reflection;
using Notifications.Application.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

public class ContactPortTests
{
    [Fact]
    public void A_lookup_is_found_or_no_such_customer_and_nothing_else()
    {
        typeof(ContactLookup).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(ContactLookup)))
            .Select(t => t.Name)
            .ShouldBe(["Found", "NoSuchCustomer"], ignoreOrder: true,
                "a fault is an exception, so an owner that is down can never reach a row as an absence");
    }

    [Fact]
    public void A_found_contact_carries_the_mailbox_and_the_locale_and_nothing_else_the_owner_offered()
    {
        // ADR-052: Keycloak's user representation carries a name and attributes nobody asked for.
        typeof(ContactLookup.Found)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ShouldBe(["Email", "Locale"], ignoreOrder: true);
    }

    [Fact]
    public void A_found_contact_prints_no_mailbox()
    {
        ContactLookup.Found found = new("aigerim@example.test", "kk");

        found.ToString().ShouldNotContain("aigerim", Case.Insensitive, "§13.4: a log takes the ids, never the mailbox");
    }

    [Fact]
    public void A_stored_contact_prints_no_mailbox()
    {
        ContactRecord record = new("aigerim@example.test", "kk", DateTimeOffset.UnixEpoch);

        record.ToString().ShouldNotContain("aigerim", Case.Insensitive);
    }
}
```

`tests/Notifications.Application.Tests/LanguageTagTests.cs`:

```csharp
using Notifications.Application.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The BCP 47 shape a locale is held to before it is stored (ADR-052).</summary>
public class LanguageTagTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("kk")]
    [InlineData("ru")]
    [InlineData("en-GB")]
    [InlineData("kk-KZ")]
    [InlineData("kk-Cyrl-KZ")]
    [InlineData("zh-Hant-TW")]
    [InlineData("es-419")]
    [InlineData("de-CH-1996")]
    public void A_tag_of_the_shape_is_one(string value)
    {
        LanguageTag.IsOne(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("k")]
    [InlineData("english")]
    [InlineData("en_GB")]
    [InlineData("en-")]
    [InlineData("-en")]
    [InlineData("en--GB")]
    [InlineData("kk\n")]
    [InlineData("kk\r\nBcc: someone@example.test")]
    [InlineData("<script>")]
    [InlineData("en-GB-subtagtoolong")]
    [InlineData("x-private")]
    [InlineData("қаз")]
    [InlineData("e1")]
    public void Anything_else_is_not(string? value)
    {
        LanguageTag.IsOne(value).ShouldBeFalse();
    }

    [Fact]
    public void A_tag_longer_than_the_bound_is_not_one_whatever_its_shape()
    {
        string longest = "en" + string.Concat(Enumerable.Repeat("-abcdefg", 4));
        string past = longest + "-a";

        longest.Length.ShouldBeLessThanOrEqualTo(LanguageTag.MaxLength);
        LanguageTag.IsOne(longest).ShouldBeTrue("the control, so the refusal below is the length and not the shape");

        past.Length.ShouldBeGreaterThan(LanguageTag.MaxLength);
        LanguageTag.IsOne(past).ShouldBeFalse();
    }
}
```

`tests/Notifications.Application.Tests/ContactOptionsTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>ADR-052's two freshness numbers, refused rather than clamped as <c>RetentionPolicy</c>'s are.</summary>
public class ContactOptionsTests
{
    [Fact]
    public void The_defaults_are_the_records_numbers()
    {
        ContactOptions options = new();

        options.Freshness.ShouldBe(TimeSpan.FromMinutes(15));
        options.StaleCeiling.ShouldBe(TimeSpan.FromHours(24));
    }

    [Fact]
    public void A_ceiling_below_the_freshness_is_refused_rather_than_clamped()
    {
        ArgumentOutOfRangeException thrown = Should.Throw<ArgumentOutOfRangeException>(
            () => new ContactOptions(TimeSpan.FromHours(1), TimeSpan.FromMinutes(59)));

        thrown.ParamName.ShouldBe("staleCeiling");
        thrown.Message.ShouldContain("StaleCeiling must be at least Freshness");
    }

    [Fact]
    public void A_ceiling_equal_to_the_freshness_is_accepted_and_serves_no_stale_row()
    {
        ContactOptions options = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        options.StaleCeiling.ShouldBe(options.Freshness);
    }

    [Fact]
    public void The_order_the_numbers_are_given_in_decides_nothing()
    {
        // A ceiling shorter than the default freshness is legal beside a shorter freshness still.
        ContactOptions options = new(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        options.Freshness.ShouldBe(TimeSpan.FromMinutes(5));
        options.StaleCeiling.ShouldBe(TimeSpan.FromMinutes(10));
    }

    public static TheoryData<TimeSpan> OutOfRange() =>
        [TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromDays(3651)];

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_freshness_out_of_range_is_refused(TimeSpan freshness)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ContactOptions(freshness, TimeSpan.FromDays(3650)))
            .ParamName.ShouldBe("Freshness");
    }

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public void A_ceiling_out_of_range_is_refused(TimeSpan staleCeiling)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ContactOptions(TimeSpan.FromSeconds(1), staleCeiling))
            .ParamName.ShouldBe("StaleCeiling");
    }

    [Fact]
    public void The_layer_registers_the_records_numbers()
    {
        using ServiceProvider services = new ServiceCollection().AddNotificationsApplication().BuildServiceProvider();

        services.GetRequiredService<ContactOptions>().ShouldBe(new ContactOptions());
    }
}
```

- [ ] **Step 2: Run to see them fail**

```bash
dotnet test tests/Notifications.Application.Tests --filter "FullyQualifiedName~ContactPortTests|FullyQualifiedName~LanguageTagTests|FullyQualifiedName~ContactOptionsTests"
```

Expected: compile failure — `Notifications.Application.Contacts` does not
exist.

- [ ] **Step 3: Write the types**

`IContactSource.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>ADR-052's read of a customer's mailbox and locale from Keycloak, the one place either is held.</summary>
/// <remarks>A fault throws, and a refused credential throws <see cref="ContactSourceRefusedException"/>.</remarks>
public interface IContactSource
{
    Task<ContactLookup> GetAsync(Guid customerId, CancellationToken ct);
}
```

`ContactLookup.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>The owner's answer about one customer (ADR-052).</summary>
public abstract record ContactLookup
{
    private ContactLookup()
    {
    }

    /// <summary>A mailbox, and a language tag where the realm holds one; its text names neither (§13.4).</summary>
    public sealed record Found(string Email, string? Locale) : ContactLookup
    {
        public override string ToString() => nameof(Found);
    }

    /// <summary>No such user, a disabled one, or one with no mailbox, collapsed at the adapter (ADR-052).</summary>
    public sealed record NoSuchCustomer : ContactLookup;
}
```

`ContactSourceRefusedException.cs`, the three standard constructors as
`AddressSourceRefusedException` carries them:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>The owner or the identity provider refused this host's credential, a defect ADR-052 counts.</summary>
public sealed class ContactSourceRefusedException : Exception
{
    public ContactSourceRefusedException()
    {
    }

    public ContactSourceRefusedException(string message)
        : base(message)
    {
    }

    public ContactSourceRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

`IContactStore.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>ADR-052's contact row, the one table in this service a mailbox lands in.</summary>
/// <remarks>A port, not <c>IUnitOfWork.ExecuteRawAsync</c>, as <see cref="GetAsync"/> returns what it read.</remarks>
public interface IContactStore
{
    Task SaveAsync(Guid customerId, ContactLookup.Found contact, DateTimeOffset fetchedAt, CancellationToken ct);

    Task<ContactRecord?> GetAsync(Guid customerId, CancellationToken ct);

    /// <summary>ADR-052's delete when the owner says the customer does not exist, and §11.7's erasure.</summary>
    Task DeleteAsync(Guid customerId, CancellationToken ct);
}
```

`ContactRecord.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>A stored contact and the instant it was fetched, which ADR-052's freshness is read against.</summary>
public sealed record ContactRecord(string Email, string? Locale, DateTimeOffset FetchedAt)
{
    public override string ToString() => nameof(ContactRecord);
}
```

`ContactLimits.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>What a contact may carry and still be stored, the widths of <c>notifications.ContactRecords</c>.</summary>
public static class ContactLimits
{
    /// <summary>Keycloak's own column for a user's email, so nothing the owner can hold is refused here.</summary>
    public const int MaxEmailLength = 255;
}
```

`LanguageTag.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>The BCP 47 shape a locale is held to before it is stored; anything else is dropped (ADR-052).</summary>
/// <remarks>
/// A shape and not the registry: a two- or three-letter language, then subtags of one to eight ASCII letters or
/// digits. Narrower than RFC 5646 at the edges, as a tag a deployment ships never meets them (ADR-053).
/// </remarks>
public static class LanguageTag
{
    /// <summary>RFC 5646's recommended buffer for one tag.</summary>
    public const int MaxLength = 35;

    public static bool IsOne(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
            return false;

        string[] subtags = value.Split('-');

        return subtags[0].Length is 2 or 3
            && subtags[0].All(char.IsAsciiLetter)
            && subtags.Skip(1).All(s => s.Length is >= 1 and <= 8 && s.All(char.IsAsciiLetterOrDigit));
    }
}
```

`ContactOptions.cs`:

```csharp
namespace Notifications.Application.Contacts;

/// <summary>ADR-052's two freshness numbers for a contact row, refused at construction rather than clamped.</summary>
/// <remarks>
/// A registered value in <c>RetentionPolicy</c>'s refusing shape, not a bound section: ADR-052 states both numbers,
/// and a value no environment varies earns no options type (§15.4).
/// </remarks>
public sealed record ContactOptions
{
    /// <summary>Ten years, past which a window is a configuration error, as <c>RetentionPolicy</c>'s are.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3650);

    /// <summary>ADR-052's numbers: fifteen minutes fresh, a day served stale while the owner cannot answer.</summary>
    public ContactOptions()
        : this(TimeSpan.FromMinutes(15), TimeSpan.FromHours(24))
    {
    }

    public ContactOptions(TimeSpan freshness, TimeSpan staleCeiling)
    {
        Freshness = InRange(freshness, nameof(Freshness));
        StaleCeiling = InRange(staleCeiling, nameof(StaleCeiling));

        if (StaleCeiling < Freshness)
        {
            throw new ArgumentOutOfRangeException(
                nameof(staleCeiling),
                staleCeiling,
                $"{nameof(StaleCeiling)} must be at least {nameof(Freshness)}, which is {Freshness}. Below it a " +
                "row would be fresh enough to serve without asking the owner and too stale to serve when the " +
                "owner cannot answer, which is a setting that cannot do what it says (ADR-052).");
        }
    }

    /// <summary>A row younger than this is served with no call to the owner.</summary>
    public TimeSpan Freshness { get; }

    /// <summary>The oldest row served while the owner cannot answer, a security number as well (ADR-052).</summary>
    public TimeSpan StaleCeiling { get; }

    private static TimeSpan InRange(TimeSpan value, string member) =>
        value > TimeSpan.Zero && value <= MaxAge
            ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive and at most {MaxAge}. A contact window outside that range does " +
                "not fail where it is set — it asks the owner on every read, or never asks it at all.");
}
```

In `Notifications.Application/DependencyInjection.cs`, after
`services.AddSingleton<RequestMetrics>();`, with
`using Notifications.Application.Contacts;` in sorted position:

```csharp
        // ADR-052's two freshness numbers, registered rather than const so the service can change them.
        services.AddSingleton(new ContactOptions());
```

The layer registers it because the layer holds it (§4.2): `ContactOptions` is
the Application's policy, and PR-5's worker reads it beside the transition
functions section 5 puts here.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Notifications.Application.Tests
dotnet build Platform.slnx
git add src/Services/Notifications/Notifications.Application tests/Notifications.Application.Tests
git commit -m "feat(notifications): IContactSource, ContactLookup and ContactOptions in the domain's words"
```

Expected: green, including `ArchitectureTests`, whose allow-list is unchanged
because nothing here references an assembly beyond `System.Runtime`,
`System.Linq` and `Microsoft.Extensions.DependencyInjection.Abstractions`; 0
warnings.

---

### Task 2: The realm's `notifications-worker` client, the gate's predicate, and the asserted tests

This task lands **before** anything reads the client, and the order is
deliberate: the two suites ADR-052 names go red on the realm edit and green on
the test edit in the same commit, and Task 6's live tests need the client in
the export.

**Files:**
- Modify: `deploy/compose/keycloak/realm-export.json` — the client, its
  service-account user, and `web-bff`'s description
- Modify: `tests/Common.Web.Tests/RealmImportTests.cs` — the credentialed set
  and two new assertions over the service account and the composite
- Modify: `tests/Web.Bff.Tests/RealmClientTests.cs` — the service-account set,
  and the test's name, which this client makes false
- Modify: `deploy/keycloak/realm_check.py` — `CONTACT_CLIENT`,
  `check_contact_client`, and the `WORKER_CLIENT` comment this client makes
  false
- Modify: `deploy/keycloak/test_realm_check.py` — a `contact()` fixture, the
  `realm()` helper, the two documents that list clients by hand, and one
  negative case per limb
- Modify: `deploy/keycloak/README.md` — the new obligation
- Modify: `.github/secret-scan/allowed/deploy.txt` and
  `.github/secret-scan/allowed/tests.txt` — the new local default's findings

**Gates this task turns red, and what turns each green.** ADR-052 marks
`RealmImportTests.No_client_ships_a_secret_but_the_ones_whose_grants_need_one`
and `RealmClientTests.The_service_account_clients_are_exactly_the_hosts_that_call_a_peer`
as asserted; both go red on the realm edit and green on the test edit here.
`RealmImportTests.The_permission_vocabulary_is_a_closed_set_of_client_roles`
**stays green**: this client holds no `commerce-api` role, so the vocabulary
does not move — ADR-052's table names it for Shipping's role, and it is
re-read here rather than re-assigned. The secret scan goes red on the new
literals and green on the allow-list entries. `realm_check.py`'s new predicate
goes red on any realm without the client and green on the export.

- [ ] **Step 1: Write the failing test edits**

In `RealmImportTests`, beside the worker's constants:

```csharp
    /// <summary>ADR-052's contact reader, and its own default.</summary>
    private const string ContactCredentialClient = "notifications-worker";

    private const string DocumentedLocalContactSecret = "local-dev-notifications-secret";

    /// <summary>The client Keycloak's admin roles live on, the contact reader's among them (ADR-052).</summary>
    private const string RealmManagement = "realm-management";
```

`DocumentedLocalSecrets` gains its entry, and **each value keeps a constant of
its own with a credential-shaped name**, for the reason Shipping's PR-4 gave:
the existing `tests.txt` entries match `DocumentedLocalSecret` and
`DocumentedLocalWorkerSecret` as constants, and folding a value into the
dictionary would make an accepted finding vanish, which fails the build:

```csharp
    private static readonly Dictionary<string, string> DocumentedLocalSecrets =
        new(StringComparer.Ordinal)
        {
            [CredentialClient] = DocumentedLocalSecret,
            [WorkerCredentialClient] = DocumentedLocalWorkerSecret,
            [ContactCredentialClient] = DocumentedLocalContactSecret
        };
```

`No_client_ships_a_secret_but_the_ones_whose_grants_need_one` is unchanged: it
iterates the set, and its non-vacuity loop now requires the third member.

Two new tests, after `The_worker_service_account_holds_exactly_the_role_its_grant_names`:

```csharp
    [Fact]
    public void The_contact_service_account_holds_exactly_view_users_on_realm_management()
    {
        JsonElement account = Root.GetProperty("users").EnumerateArray()
            .Single(u => u.TryGetProperty("serviceAccountClientId", out JsonElement client) &&
                         client.GetString() == ContactCredentialClient);

        // One client and one role on it, since ADR-052 sizes this credential by what it reads when stolen.
        string[] clients = [.. account.GetProperty("clientRoles").EnumerateObject().Select(c => c.Name)];
        clients.ShouldBe([RealmManagement]);

        string[] granted =
        [
            .. account.GetProperty("clientRoles").GetProperty(RealmManagement).EnumerateArray()
                .Select(r => r.GetString()).OfType<string>()
        ];

        granted.ShouldBe(["view-users"]);
        account.TryGetProperty("realmRoles", out _).ShouldBeFalse();
        account.TryGetProperty("groups", out _).ShouldBeFalse();
    }

    [Fact]
    public void View_users_composes_exactly_the_two_query_roles_and_neither_composes_further()
    {
        // The pinned Keycloak's own composition, exported with the realm: the worker's check reads the expanded set.
        JsonElement[] management = [.. Root.GetProperty("roles").GetProperty("client").GetProperty(RealmManagement)
            .EnumerateArray()];

        JsonElement viewUsers = management.Single(r => r.GetProperty("name").GetString() == "view-users");

        string[] composed =
        [
            .. viewUsers.GetProperty("composites").GetProperty("client").GetProperty(RealmManagement).EnumerateArray()
                .Select(r => r.GetString()).OfType<string>()
        ];

        composed.ShouldBe(["query-groups", "query-users"], ignoreOrder: true);

        foreach (string role in composed)
        {
            management.Single(r => r.GetProperty("name").GetString() == role)
                .GetProperty("composite").GetBoolean()
                .ShouldBeFalse($"'{role}' composing further would widen the grant past the three roles ADR-052 names");
        }
    }
```

In `RealmClientTests`, the test's name says the set is the hosts that call a
peer, and Notifications' worker calls Keycloak and no peer — so the name moves
with the assertion, which names the set:

```csharp
    /// <summary>The second client ADR-052 mints, and the reader of Ordering's address.</summary>
    private const string WorkerClient = "shipping-worker";

    /// <summary>The third, and the reader of a customer's mailbox from the realm itself (ADR-052).</summary>
    private const string ContactClient = "notifications-worker";

    [Fact]
    public void The_service_account_clients_are_exactly_the_credentialed_hosts()
    {
        string[] serviceAccounts =
        [
            .. Realm.RootElement
                .GetProperty("clients")
                .EnumerateArray()
                .Where(c =>
                    c.TryGetProperty("serviceAccountsEnabled", out JsonElement enabled) &&
                    enabled.GetBoolean())
                .Select(c => c.GetProperty("clientId").GetString()!)
        ];

        // Each secret holder is a synchronous coupling (§11.5), so an undecided client fails.
        serviceAccounts.ShouldBe([ClientId, WorkerClient, ContactClient], ignoreOrder: true);
    }
```

The worker constant's summary — "The second client ADR-052 mints" — is an
ordinal and stays true; it is not touched.

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~RealmImportTests"
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~RealmClientTests"
```

Expected: `No_client_ships_a_secret_but_the_ones_whose_grants_need_one` fails
its non-vacuity loop on an absent `notifications-worker`;
`The_contact_service_account_holds_exactly_view_users_on_realm_management`
throws from `Single`; `View_users_composes_exactly_the_two_query_roles_and_neither_composes_further`
**passes already**, and that is its point — it pins a fact of the pinned image
the export carries, so a Keycloak upgrade that recomposed `view-users` fails
here rather than at the worker; and
`The_service_account_clients_are_exactly_the_credentialed_hosts` reports two
names where three are expected.

- [ ] **Step 2: The realm**

In `clients`, after `shipping-worker` and before `mobile-app`, a client in the
existing shape with a fresh `id`:

```jsonc
    {
      "id": "5328d314-4c27-43f1-937f-5f5637c38259",
      "clientId": "notifications-worker",
      "name": "Notifications worker",
      "description": "ADR-052's contact reader: a Notifications worker reading a customer's mailbox and locale from the admin API. Service accounts only, view-users on realm-management and nothing else, and no commerce-api scope, so no service accepts its token.",
      "surrogateAuthRequired": false,
      "enabled": true,
      "alwaysDisplayInConsole": false,
      "clientAuthenticatorType": "client-secret",
      "secret": "local-dev-notifications-secret",
      "redirectUris": [],
      "webOrigins": [],
      "notBefore": 0,
      "bearerOnly": false,
      "consentRequired": false,
      "standardFlowEnabled": false,
      "implicitFlowEnabled": false,
      "directAccessGrantsEnabled": false,
      "serviceAccountsEnabled": true,
      "publicClient": false,
      "frontchannelLogout": false,
      "protocol": "openid-connect",
      "attributes": {
        "realm_client": "false",
        "backchannel.logout.session.required": "true",
        "backchannel.logout.revoke.offline.tokens": "false"
      },
      "authenticationFlowBindingOverrides": {},
      "fullScopeAllowed": true,
      "nodeReRegistrationTimeout": -1,
      "defaultClientScopes": [
        "web-origins",
        "acr",
        "profile",
        "roles",
        "basic",
        "email"
      ],
      "optionalClientScopes": [
        "address",
        "phone",
        "organization",
        "offline_access",
        "microprofile-jwt"
      ],
      "access": {
        "view": true,
        "configure": true,
        "manage": true
      }
    },
```

The description is 240 characters: Keycloak's client description column is 255,
and the import fails the whole realm rather than truncating — Task 6's fresh
import is the proof, and `py -3.12 -c "import json;print(max(len(c.get('description',''))
for c in json.load(open('deploy/compose/keycloak/realm-export.json',encoding='utf-8'))['clients']))"`
is the quick check before it. `commerce-api` is in **neither** scope list: the
read is Keycloak's own admin API, so the audience every service validates would
make a secret that reads every user also one every service accepts.

In `users`, after `service-account-shipping-worker`:

```jsonc
    {
      "username": "service-account-notifications-worker",
      "enabled": true,
      "serviceAccountClientId": "notifications-worker",
      "clientRoles": {
        "realm-management": [
          "view-users"
        ]
      }
    }
```

`view-users` alone: Keycloak composes `query-groups` and `query-users` into it
(Global Constraints), and listing them as well would make the export say a
second thing the realm already says. No password and no email: a service
account's user is not a login. `realmRoles` is absent on purpose, as on the
Shipping account, so the token's `realm_access` carries nothing the realm did
not choose.

`web-bff`'s description counts the hosts — "One of the two hosts that call a
service synchronously and hold client credentials (§9.7, §11.5, ADR-052)" — and
ADR-052's row for this file names exactly that sentence. It becomes:

> A host holding client credentials (§11.5, ADR-052), and the one that calls a
> service on a request path (§9.7). Service accounts only: no browser flow, and
> nothing can obtain a token as a person through it.

`shipping-worker`'s — "The second host that calls a service synchronously" —
is an ordinal and stays true. **`internationalizationEnabled` stays `false`**,
the other half of ADR-052's row for this file: spec section 1 decides the realm
does not turn it on, and ADR-052 reads the absent locale as an answer.

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~RealmImportTests"
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~RealmClientTests"
```

Expected: green, including `No_role_description_exceeds_what_keycloak_can_store`
(no role moved) and `Every_client_holding_the_scope_holds_it_as_a_default`
(this client holds the scope in neither list).

- [ ] **Step 3: The realm gate's predicate**

ADR-052 is explicit that a service account's roles are in neither document
this gate reads, so the predicate is about everything else a stolen secret's
blast radius depends on. `WORKER_CLIENT`'s comment says "Notifications' client
is decided and minted nowhere yet, so it is deliberately not here", which this
step makes false; the block is eight lines, so it is rewritten whole to five or
fewer. Before:

```python
# The address reader ADR-052 mints, named for the same reason the two above
# are: its obligations are properties of one client and cannot be checked
# without finding it. It is required from the change that mints it in the
# local export; a deployed realm is no committed file (ADR-042), so an
# operator creates the client there with a generated secret, and this gate
# refuses that realm until one has. Notifications' client is decided and
# minted nowhere yet, so it is deliberately not here.
WORKER_CLIENT = "shipping-worker"
```

After:

```python
# The address reader ADR-052 mints, named because its obligations are a
# client's. Required in the local export; a deployed realm is no committed file
# (ADR-042), so an operator creates the client there, and this gate refuses
# that realm until one has.
WORKER_CLIENT = "shipping-worker"

# The contact reader, on the address reader's terms. It reads Keycloak itself
# and calls no service, so its scope obligations invert: commerce-api is in
# neither of its lists, and roles, the scope it requests, is in one.
CONTACT_CLIENT = "notifications-worker"
```

`CLIENT_FIELDS` and `FLAGS` need nothing: Shipping's predicate already put both
scope lists, `serviceAccountsEnabled` and `enabled` into the projection and the
boolean check.

In `check_realm`, after the `worker` lookup:

```python
    contact = [c for c in clients if isinstance(c, dict) and c.get("clientId") == CONTACT_CLIENT]
    if len(contact) != 1:
        problems.append(
            f"the realm declares the contact reader {CONTACT_CLIENT!r} "
            f"{len(contact)} time(s), expected exactly one. ADR-052 sizes that "
            "client by what it reads when its secret is stolen, and every "
            "obligation below is a property of the client object")
```

and after `problems += check_worker_client(worker[0])`'s block:

```python
    if contact:
        problems += check_contact_client(contact[0])
```

The check, after `check_worker_client`:

```python
def check_contact_client(client: dict) -> list[str]:
    """ADR-052's ceiling on the contact reader, as far as a realm document reaches.

    Its grant lives on its service account's user, out of reach for the reason
    `check_worker_client` gives; what is left is a client that mints for itself
    alone, and a token no service of this platform accepts."""
    problems: list[str] = []

    if client.get("enabled") is not True:
        problems.append(
            f"client {CONTACT_CLIENT!r} is disabled. Every obligation below "
            "then holds because the client mints nothing, and the contact read "
            "fails as a refused credential in whichever environment imported "
            "this realm")

    if client.get("publicClient") is not False:
        problems.append(
            f"client {CONTACT_CLIENT!r} has publicClient="
            f"{client.get('publicClient')!r}. A public client presents no "
            "secret, so the grant ADR-052 gives this reader is one Keycloak "
            "refuses outright")

    if client.get("serviceAccountsEnabled") is not True:
        problems.append(
            f"client {CONTACT_CLIENT!r} has service accounts disabled. Keycloak "
            "refuses the client-credentials grant with unauthorized_client, "
            "which reaches the worker as a refused credential — ADR-052's "
            "fourth row, a defect somebody must see rather than an outage")

    for flag, what in (("standardFlowEnabled", "an authorization-code flow"),
                       ("directAccessGrantsEnabled", "a password grant"),
                       ("implicitFlowEnabled", "an implicit flow")):
        if client.get(flag) is not False:
            problems.append(
                f"client {CONTACT_CLIENT!r} has {flag}={client.get(flag)!r}, "
                f"which gives it {what}. Its secret already reads every user's "
                "profile, so a leak of it must never also be a token for a "
                "person in this realm (ADR-052)")

    defaults = client.get("defaultClientScopes")
    defaults = defaults if isinstance(defaults, list) else []
    optional = client.get("optionalClientScopes")
    optional = optional if isinstance(optional, list) else []

    if "commerce-api" in defaults or "commerce-api" in optional:
        problems.append(
            f"client {CONTACT_CLIENT!r} holds commerce-api as a client scope. "
            "Its read is Keycloak's own admin API, so the audience every "
            "service validates would make a secret that reads every user also "
            "one every service accepts (ADR-052)")

    if "roles" not in defaults and "roles" not in optional:
        problems.append(
            f"client {CONTACT_CLIENT!r} holds the roles scope in neither list. "
            "The worker requests it by name and Keycloak refuses a scope the "
            "client does not hold; its mapper is also what writes "
            "resource_access, the claim the worker's grant check reads")

    return problems
```

The docstring is five lines, opening quote to closing quote, which is the
gate's limit exactly; the closing quotes share the last line so that it stays
there.

**What this costs a deployed realm, said here so the PR body can say it.** From
this change on, `deploy.yml`'s rollout and `realm.yml`'s scheduled job refuse a
deployed realm without exactly one `notifications-worker` — the terms
`shipping-worker` has carried since it was minted, and ADR-042's form: a
deployed realm is no committed file, so an operator creates the client with a
generated secret and the grant `docs/secrets.md` names (Task 8), and the gate
refuses that realm until then.

- [ ] **Step 4: The gate's own tests**

In `test_realm_check.py`, a fixture after `worker()`:

```python
def contact(**overrides) -> dict:
    """A compliant `notifications-worker`: confidential, service accounts on,
    no interactive flow, roles a default scope, and commerce-api in neither."""
    client = {
        "clientId": realm_check.CONTACT_CLIENT,
        "enabled": True,
        "standardFlowEnabled": False,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": False,
        "serviceAccountsEnabled": True,
        "publicClient": False,
        "defaultClientScopes": ["web-origins", "acr", "profile", "roles", "basic", "email"],
        "optionalClientScopes": ["address", "phone", "organization"],
        "webOrigins": [],
    }
    client.update(overrides)
    return client
```

`realm()` appends it on the terms it appends the other two, and its docstring —
eight lines that name the two clients it appends — is rewritten whole, because
editing it puts the block under the gate's limit:

```python
def realm(*clients, **overrides) -> dict:
    """A realm document of the shape both an export and the admin API produce.

    Every client a check requires by name is appended unless the caller
    supplied one, so a case about something else need not learn them all."""
    if clients:
        client_list = list(clients)
        for required in (mobile, worker, contact):
            name = required()["clientId"]
            if not any(isinstance(c, dict) and c.get("clientId") == name
                       for c in client_list):
                client_list.append(required())
    else:
        client_list = [browser(), mobile(), worker(), contact()]
```

The rest of `realm()` is unchanged. Two documents list their clients by hand
and each gains the new one, for the reason Shipping's PR-4 gave the worker:
`WhatTheGateIsLookingAt.test_a_realm_missing_the_mobile_client_is_refused`
counts exactly one problem, so its list becomes
`realm(clients=[browser(), other, worker(), contact()])`; and
`WhatTheGateHolds.realm_with_secrets` ends its client list
`}, worker(), contact()],` because `test_the_fields_every_check_reads_do_survive`
asserts the whole verdict is `[]`. `test_rotation_is_not_checked_without_a_mobile_client`
asserts with `any(...)` and stands.

Then the cases:

```python
class TheContactClient(Fixture):
    def test_a_missing_contact_client_is_caught_rather_than_passed(self):
        """The vacuous half: every check below is a property of one client."""
        document = realm(browser(), mobile())
        document["clients"] = [c for c in document["clients"]
                               if c.get("clientId") != realm_check.CONTACT_CLIENT]
        self.assertIn("notifications-worker", self.one(document))

    def test_a_public_contact_client_is_caught(self):
        self.assertIn("publicClient", self.one(realm(browser(), contact(publicClient=True))))

    def test_service_accounts_turned_off_is_caught(self):
        self.assertIn("service accounts disabled",
                      self.one(realm(browser(), contact(serviceAccountsEnabled=False))))

    def test_a_disabled_contact_client_is_caught(self):
        self.assertIn("disabled", self.one(realm(browser(), contact(enabled=False))))

    def test_each_interactive_flow_is_caught_on_its_own(self):
        for flag in ("standardFlowEnabled", "directAccessGrantsEnabled", "implicitFlowEnabled"):
            with self.subTest(flag=flag):
                found = self.problems(realm(browser(), contact(**{flag: True})))
                # The implicit flow is also refused for every client; this limb's wording is what is matched.
                limb = f"client {realm_check.CONTACT_CLIENT!r} has {flag}="
                self.assertTrue(any(limb in problem for problem in found), found)

    def test_commerce_api_as_a_default_scope_is_caught(self):
        found = self.one(realm(browser(), contact(defaultClientScopes=["roles", "commerce-api"])))
        self.assertIn("commerce-api", found)

    def test_commerce_api_as_an_optional_scope_is_caught(self):
        found = self.one(realm(browser(), contact(optionalClientScopes=["address", "commerce-api"])))
        self.assertIn("commerce-api", found)

    def test_roles_in_neither_list_is_caught(self):
        found = self.one(realm(browser(), contact(defaultClientScopes=["basic"], optionalClientScopes=[])))
        self.assertIn("roles", found)

    def test_roles_as_an_optional_scope_is_accepted(self):
        """The control for the case above: the worker names it, so either list serves."""
        document = realm(browser(), contact(defaultClientScopes=["basic"], optionalClientScopes=["roles"]))
        self.assertEqual(self.problems(document), [])

    def test_a_string_service_account_flag_is_refused_rather_than_read_as_on(self):
        """Both the boolean check and this limb see a string, and each is asserted."""
        found = self.problems(realm(browser(), contact(serviceAccountsEnabled="true")))
        self.assertTrue(any("boolean" in problem for problem in found), found)
        self.assertTrue(any("service accounts disabled" in problem for problem in found), found)
```

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py check --kind local --realm deploy/compose/keycloak/realm-export.json
```

Expected: the suite exits 0 — prove the new limbs by mutation, deleting each
`if` in `check_contact_client` in turn and seeing its case fail — and the gate
exits 0 against the export Step 2 left, its closing line counting one client
more.

- [ ] **Step 5: The README that owns the gate's claim**

`deploy/keycloak/README.md`, under *What it asserts*, after the
`shipping-worker` bullet:

```markdown
- **`notifications-worker`'s own shape**, as far as a client object reaches:
  one such client, confidential, service accounts on, no interactive flow,
  `commerce-api` in neither scope list and `roles` in one — cited rather than
  enumerated here, because
  [ADR-052](../../docs/backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
  argues each of them and `check_contact_client` is the list. Its grant,
  `view-users` on `realm-management`, is out of reach for the reason the
  bullet above gives, and the worker's check on its own token is the other
  half.
```

Nothing under *What it does not check* moves: the scope lists are already said
to be in the projection.

- [ ] **Step 6: The secret scan**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: two new `credential-assignment` findings for
`local-dev-notifications-secret` — one on `realm-export.json`, one on
`RealmImportTests.cs`. Take each fingerprint **from the scan's own output** and
add, beside the existing entries for each file:

```
deploy/compose/keycloak/realm-export.json | credential-assignment | <fingerprint> | ADR-052's contact reader, and §14.1's local default for it.
```

```
tests/Common.Web.Tests/RealmImportTests.cs | credential-assignment | <fingerprint> | The suite pinning the realm export's third documented local client default.
```

Re-run; expected: clean, and no unmatched entry.

- [ ] **Step 7: Commit**

```bash
dotnet test tests/Common.Web.Tests --filter "Category!=Integration"
dotnet test tests/Web.Bff.Tests --filter "Category!=Integration"
git add deploy/compose/keycloak/realm-export.json deploy/keycloak \
        .github/secret-scan/allowed/deploy.txt .github/secret-scan/allowed/tests.txt \
        tests/Common.Web.Tests/RealmImportTests.cs tests/Web.Bff.Tests/RealmClientTests.cs
git commit -m "feat(realm): the notifications-worker client, its gate predicate and the asserted realm tests"
```

The body says that `view-users` composes exactly the two query roles in the
pinned 26.0 export, that the client takes no `commerce-api` scope and why, and
that every deployed realm now needs the client before a rollout passes the
gate.

---

### Task 3: `ContactRecords`, its store and `AddContactRecords`

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/ContactRecordRow.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/ContactRecordRowConfiguration.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Persistence/SqlContactStore.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/DependencyInjection.cs`
  — `IContactStore` registered
- Create (generated): `Notifications.Infrastructure/Persistence/Migrations/<ts>_AddContactRecords.cs`,
  its designer, and the rewritten `NotificationsDbContextModelSnapshot.cs`
- Test: `tests/Notifications.Worker.Tests/ContactStoreTests.cs`
- Modify: `tests/Notifications.Worker.Tests/DatabaseSmokeTests.cs` — the
  migration count and name

**Interfaces:**
- Consumes: Task 1's `IContactStore`, `ContactLookup.Found`, `ContactRecord`,
  `ContactLimits`, `LanguageTag`; PR-1's `IDbConnectionFactory` registration and
  `NotificationsDbContext`.
- Produces: `notifications.ContactRecords(CustomerId uniqueidentifier PK,
  Email nvarchar(255), Locale varchar(35) NULL, FetchedAt datetimeoffset(7))`
  and `SqlContactStore`.

**The write differs from Shipping's, and the difference is ADR-052's.**
`SqlDeliveryAddressStore` updates then inserts with no lock hints, because the
one writer of an order's row holds that shipment's lease. A contact row is
keyed by the **customer**, and ADR-052's consequences accept that "a cold table
meets a burst once per replica": two replicas resolving two notifications for
one customer both read, both miss, and both save. Update-then-insert across
two autocommitted statements then puts two inserts of one key in flight, and
one fails on the primary key. So the batch is one transaction whose update
holds `UPDLOCK, HOLDLOCK` — a key-range lock on the absent key — and a
concurrent writer waits, then updates the row the first inserted.

- [ ] **Step 1: Write the failing store tests**

`tests/Notifications.Worker.Tests/ContactStoreTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Persistence;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's contact row over a real engine, as every claim here is the column's (§12.4).</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ContactStoreTests(ServiceFixture fixture) : IAsyncLifetime
{
    /// <summary>An internationalised mailbox, in the letters a Cyrillic code page would lose.</summary>
    private static readonly ContactLookup.Found Kazakh = new("айгерім@мысал.қаз", "kk");

    private static readonly DateTimeOffset Fetched = new(2026, 10, 2, 9, 30, 0, TimeSpan.FromHours(5));

    public ValueTask InitializeAsync() => new(fixture.ResetAsync());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_kazakh_script_mailbox_and_its_locale_round_trip_with_the_instant_they_were_fetched()
    {
        Guid customer = Guid.CreateVersion7();

        await SaveAsync(customer, Kazakh, Fetched);

        (await ReadAsync(customer)).ShouldBe(new ContactRecord(Kazakh.Email, Kazakh.Locale, Fetched));
    }

    [Fact]
    public async Task A_second_save_for_one_customer_leaves_one_row_holding_the_later_answer()
    {
        Guid customer = Guid.CreateVersion7();
        ContactLookup.Found later = new("aigerim@example.test", null);
        await SaveAsync(customer, Kazakh, Fetched);

        await SaveAsync(customer, later, Fetched.AddMinutes(20));

        (await CountAsync(customer)).ShouldBe(1);
        (await ReadAsync(customer)).ShouldBe(new ContactRecord(later.Email, null, Fetched.AddMinutes(20)));
    }

    [Fact]
    public async Task Concurrent_saves_for_one_customer_leave_one_row_and_no_fault()
    {
        // ADR-052 accepts a burst once per replica, so two first saves of one customer can race.
        Guid customer = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            SaveAsync(customer, new ContactLookup.Found($"customer{i}@example.test", null), Fetched.AddSeconds(i))));

        (await CountAsync(customer)).ShouldBe(1);
    }

    [Fact]
    public async Task An_absent_locale_reads_back_as_absent()
    {
        Guid customer = Guid.CreateVersion7();

        await SaveAsync(customer, Kazakh with { Locale = null }, Fetched);

        (await ReadAsync(customer))!.Locale.ShouldBeNull();
    }

    [Fact]
    public async Task A_customer_with_no_row_reads_as_null()
    {
        (await ReadAsync(Guid.CreateVersion7())).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_removes_the_row_and_deleting_an_absent_one_is_no_fault()
    {
        Guid customer = Guid.CreateVersion7();
        await SaveAsync(customer, Kazakh, Fetched);

        await DeleteAsync(customer);
        await DeleteAsync(customer);

        (await ReadAsync(customer)).ShouldBeNull();
    }

    [Fact]
    public async Task A_mailbox_carrying_a_line_break_is_stored_as_it_arrived()
    {
        // The mail channel refuses it as no mailbox (spec, section 8); a store that cleaned it would hide that.
        Guid customer = Guid.CreateVersion7();
        ContactLookup.Found broken = new("aigerim@example.test\r\nbcc: someone@example.test", null);

        await SaveAsync(customer, broken, Fetched);

        (await ReadAsync(customer))!.Email.ShouldBe(broken.Email);
    }

    [Fact]
    public async Task A_mailbox_at_the_owners_own_width_is_stored_whole()
    {
        Guid customer = Guid.CreateVersion7();
        string widest = new string('a', ContactLimits.MaxEmailLength - "@example.test".Length) + "@example.test";

        await SaveAsync(customer, new ContactLookup.Found(widest, null), Fetched);

        (await ReadAsync(customer))!.Email.ShouldBe(widest);
    }

    [Fact]
    public async Task The_columns_are_the_ones_the_record_names_and_nothing_else_the_owner_offered()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

        string[] columns = await db.Database
            .SqlQuery<string>(
                $"""
                SELECT COLUMN_NAME AS Value
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_SCHEMA = 'notifications' AND TABLE_NAME = 'ContactRecords'
                """)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        columns.ShouldBe(
            ["CustomerId", "Email", "Locale", "FetchedAt"],
            ignoreOrder: true,
            "ADR-052: the mailbox, the locale and the instant, and nothing else Keycloak offered");
    }

    private Task<int> CountAsync(Guid customer) =>
        fixture.ScalarAsync<int>(
            "SELECT Value = COUNT(*) FROM notifications.ContactRecords WHERE CustomerId = {0}", customer);

    private async Task SaveAsync(Guid customer, ContactLookup.Found contact, DateTimeOffset fetchedAt)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IContactStore>()
            .SaveAsync(customer, contact, fetchedAt, TestContext.Current.CancellationToken);
    }

    private async Task<ContactRecord?> ReadAsync(Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactStore>()
            .GetAsync(customer, TestContext.Current.CancellationToken);
    }

    private async Task DeleteAsync(Guid customer)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IContactStore>()
            .DeleteAsync(customer, TestContext.Current.CancellationToken);
    }
}
```

- [ ] **Step 2: Run to see them fail**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~ContactStoreTests"
```

Expected: every test fails resolving `IContactStore` — no service registered —
and, once Step 3's registration is in and before Step 4's migration, on SQL
error 208 naming `notifications.ContactRecords` as an invalid object.

- [ ] **Step 3: Write the row, its configuration and the store**

`ContactRecordRow.cs`:

```csharp
namespace Notifications.Infrastructure.Persistence;

/// <summary>Mapped only so <c>migrations add</c> emits <see cref="SqlContactStore"/>'s table.</summary>
internal sealed class ContactRecordRow
{
    public Guid CustomerId { get; set; }
    public string Email { get; set; } = "";
    public string? Locale { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}
```

`ContactRecordRowConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Persistence;

internal sealed class ContactRecordRowConfiguration : IEntityTypeConfiguration<ContactRecordRow>
{
    public void Configure(EntityTypeBuilder<ContactRecordRow> builder)
    {
        builder.ToTable("ContactRecords", "notifications");

        // Keyed by the customer whose mailbox it is; the owner's "does not exist" and erasure delete by it (ADR-052).
        builder.HasKey(r => r.CustomerId);
        builder.Property(r => r.CustomerId).ValueGeneratedNever();

        // nvarchar at the owner's own width, so an internationalised mailbox survives whole.
        builder.Property(r => r.Email).HasMaxLength(ContactLimits.MaxEmailLength).IsRequired();

        // varchar: a language tag is ASCII by the shape the adapter holds it to before a row is written.
        builder.Property(r => r.Locale).HasMaxLength(LanguageTag.MaxLength).IsUnicode(false);
    }
}
```

`SqlContactStore.cs`:

```csharp
using System.Data;
using Common.Application;
using Dapper;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Persistence;

/// <summary>The only reader and writer of <c>notifications.ContactRecords</c> (ADR-052).</summary>
/// <remarks>
/// On its own connection: the worker saves before it renders or sends, outside any unit. The save is one
/// transaction, as ADR-052 accepts two replicas resolving one customer at once.
/// </remarks>
internal sealed class SqlContactStore(IDbConnectionFactory connections) : IContactStore
{
    // HOLDLOCK takes the key range, so a second first-save waits and then updates the row this one inserted.
    private const string SaveSql =
        """
        SET XACT_ABORT ON;
        BEGIN TRANSACTION;

        UPDATE notifications.ContactRecords WITH (UPDLOCK, HOLDLOCK)
        SET Email = @Email, Locale = @Locale, FetchedAt = @FetchedAt
        WHERE CustomerId = @CustomerId;

        IF @@ROWCOUNT = 0
            INSERT INTO notifications.ContactRecords (CustomerId, Email, Locale, FetchedAt)
            VALUES (@CustomerId, @Email, @Locale, @FetchedAt);

        COMMIT TRANSACTION;
        """;

    private const string GetSql =
        """
        SELECT Email, Locale, FetchedAt
        FROM notifications.ContactRecords
        WHERE CustomerId = @CustomerId;
        """;

    private const string DeleteSql =
        "DELETE FROM notifications.ContactRecords WHERE CustomerId = @CustomerId;";

    public async Task SaveAsync(
        Guid customerId,
        ContactLookup.Found contact,
        DateTimeOffset fetchedAt,
        CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            SaveSql,
            new { CustomerId = customerId, contact.Email, contact.Locale, FetchedAt = fetchedAt },
            cancellationToken: ct));
    }

    public async Task<ContactRecord?> GetAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        return await connection.QuerySingleOrDefaultAsync<ContactRecord>(new CommandDefinition(
            GetSql, new { CustomerId = customerId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(Guid customerId, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        await connection.ExecuteAsync(new CommandDefinition(
            DeleteSql, new { CustomerId = customerId }, cancellationToken: ct));
    }
}
```

Dapper maps the three columns onto `ContactRecord`'s positional constructor by
name and type, so no private row record is needed. A `varchar` locale needs no
`DbString`: the parameter is sent as `nvarchar` and converted on the server,
which is a cost on a primary-key update and not a correctness question.

In `AddNotificationsInfrastructure`, directly after PR-1's
`IDbConnectionFactory` registration, with `using Notifications.Application.Contacts;`
in sorted position:

```csharp
        // ADR-052's contact row, the one table here a mailbox lands in.
        services.AddScoped<IContactStore, SqlContactStore>();
```

- [ ] **Step 4: Generate the migration**

```bash
dotnet ef migrations add AddContactRecords \
    --project src/Services/Notifications/Notifications.Infrastructure \
    --startup-project src/Services/Notifications/Notifications.Migrator \
    --output-dir Persistence/Migrations
```

**If PR-4's `AddOrderRecords` merged after this branch was cut, regenerate
after rebasing**: delete this migration and its designer, restore the snapshot
from `main`, and run the command again, so its timestamp is last and its
snapshot is the merged model's. Two migrations generated on two branches apply
in timestamp order, and a snapshot written before the other landed omits its
table.

Open it: exactly one `CreateTable` for `notifications.ContactRecords` with
`CustomerId uniqueidentifier`, `Email nvarchar(255)` not null,
`Locale varchar(35)` null, `FetchedAt datetimeoffset(7)` not null and the
primary key `PK_ContactRecords` — and no index. **No index on `FetchedAt`, and
that is a decision**: the retention pass that deletes a row not refreshed for
`ContactRetention` is PR-5's, and an index for a query no code makes yet is a
guess at its shape. Give the file the house dress — a file-scoped namespace and

```csharp
/// <summary>ADR-052's contact table, generated from <see cref="ContactRecordRowConfiguration"/>.</summary>
```

— and strip the byte-order mark `dotnet ef` writes, from this file only:
`.editorconfig`'s `charset = utf-8` is UTF-8 with no mark, and
`dotnet format --verify-no-changes` reports a marked hand-edited migration as
`CHARSET`. The designer and the snapshot are machine-owned and keep theirs.

`DatabaseSmokeTests.Migrator_exits_zero_and_creates_the_schema` names every
applied migration in order. Its `applied.Length.ShouldBe(n);` becomes `n + 1`,
and one line follows the last name it asserts:

```csharp
        applied[n].ShouldEndWith("_AddContactRecords");
```

where `n` is the count the tree held when this rebased — PR-1's, or PR-1's and
PR-4's.

- [ ] **Step 5: Run; commit**

```bash
dotnet build Platform.slnx
dotnet format Platform.slnx --verify-no-changes
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~ContactStoreTests|FullyQualifiedName~DatabaseSmokeTests"
```

Expected: 0 warnings, format exit 0, green. Prove the transaction by mutation:
cut `SET XACT_ABORT ON; BEGIN TRANSACTION;`, `COMMIT TRANSACTION;` and the
hints, run `Concurrent_saves_for_one_customer_leave_one_row_and_no_fault` five
times, and see it fail on error 2627 at least once; restore. Then, because a
migration rewritten in review is not believed until the engine builds it from
empty:

```bash
docker compose -f deploy/compose/docker-compose.yml down -v
```

```bash
git add src/Services/Notifications/Notifications.Infrastructure tests/Notifications.Worker.Tests
git commit -m "feat(notifications): the ContactRecords table and the port that writes it"
```

The body says why the save is one transaction where Shipping's is two
statements, and names the erasure statement the table is designed against: the
store's own delete, by the key.

---

### Task 4: The packages, `ContactHop`, the meter and the grant check

**Files:**
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Notifications.Infrastructure.csproj`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Contacts/ContactHop.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Observability/OutboundMeter.cs`
  (unless PR-2 landed it)
- Create: `src/Services/Notifications/Notifications.Infrastructure/Contacts/ContactMetrics.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Contacts/GrantCheckedTokenCache.cs`
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` and
  `tests/Common.Web.Tests/ObservabilityTests.cs` (unless PR-2 landed the line)
- Modify: `tests/Notifications.Worker.Tests/Notifications.Worker.Tests.csproj`
- Create: `tests/Notifications.Worker.Tests/ContactCounter.cs`
- Test: `tests/Notifications.Worker.Tests/GrantCheckedTokenCacheTests.cs`
- Test: `tests/Notifications.Worker.Tests/ContactHopTests.cs`

**Interfaces:**
- Consumes: `Common.Infrastructure.Identity.ITokenCache` and `CachingTokenClient`;
  Task 1's `ContactSourceRefusedException`.
- Produces: `ContactHop`'s numbers and `ClientName`, `ResilienceOptionsName`,
  `MaxAnswerBytes`; `OutboundMeter.Name` = `"Notifications.Outbound"`;
  `ContactMetrics.Refused()` over `Counter<long> notifications.contact.refused`;
  `GrantCheckedTokenCache(ITokenCache, ContactMetrics, ILogger<GrantCheckedTokenCache>)`;
  `ContactCounter.Refused(IServiceProvider)` returning a disposable
  `ContactCount` with `Value`.

**Why the claim is decoded by hand.** Shipping's check reads `permission`, a
flat multivalued claim that `JwtSecurityToken.Claims` hands back one value per
role. `resource_access` is a nested object, which the handler flattens into one
claim whose value is JSON text — so reading roles through it would be reading
the library's flattening rule rather than the token. The payload is decoded
with the BCL's `System.Buffers.Text.Base64Url` and read with `JsonDocument`,
which is `read_admin.py`'s `granted_roles` one language over, and needs no
package beyond the one that says the token is a JWT at all.

- [ ] **Step 1: The package references**

In `Notifications.Infrastructure.csproj`, in the `PackageReference` group:

```xml
    <!-- The contact read's typed client and its pipeline (§9.7), as Shipping's address read takes it. -->
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" />
    <!-- JwtSecurityTokenHandler, with which the grant check reads the token this host was issued (ADR-052). -->
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" />
```

and, **unless PR-2 landed it**, the line PR-2's plan writes for the same type:

```xml
    <!-- The hosting abstractions this project names: IHostEnvironment, in each outbound hop's registration. -->
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
```

PR-2's relay pipeline references `Polly.Core` directly because, in its words,
this project "references no HTTP package". From this task on it does, and
`Polly.Core` arrives transitively at the version PR-2 pins — the pin is still
PR-2's, because a project that names a type declares its package.

In `Notifications.Worker.Tests.csproj`, beside PR-1's references:

```xml
    <!-- The contact read's owner, a real Keycloak importing the shipped realm (§11.5, ADR-052). -->
    <PackageReference Include="Testcontainers.Keycloak" />
    <!-- A stub Keycloak on loopback, where a fault has to be staged (§12.7). -->
    <PackageReference Include="WireMock.Net" />
    <!-- Named although transitive: the grant check's suite and the live suite read tokens with these types. -->
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" />
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" />
```

```bash
dotnet restore Platform.slnx
dotnet build Platform.slnx
```

Expected: restore and build clean; no new pin, so the licence gate sees no new
identity.

- [ ] **Step 2: Write the failing tests**

`tests/Notifications.Worker.Tests/ContactCounter.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Infrastructure.Contacts;
using Notifications.Infrastructure.Observability;
using Shouldly;

namespace Notifications.Worker.Tests;

/// <summary>One host's refusal counter, matched by meter instance as a <c>MeterListener</c> is process-wide.</summary>
internal static class ContactCounter
{
    public static ContactCount Refused(IServiceProvider services)
    {
        services.GetRequiredService<ContactMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(OutboundMeter.Name);
        ContactCount count = new(mine, "notifications.contact.refused");
        count.Enabled.ShouldBeTrue("no refusal counter on this host's meter was enabled, so a zero proves nothing");

        return count;
    }
}

internal sealed class ContactCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _counted;

    public ContactCount(Meter mine, string instrument)
    {
        _listener.InstrumentPublished = (published, l) =>
        {
            if (ReferenceEquals(published.Meter, mine) && published.Name == instrument)
            {
                l.EnableMeasurementEvents(published);
                Enabled = true;
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _counted, value));
        _listener.Start();
    }

    public bool Enabled { get; private set; }

    public long Value => Interlocked.Read(ref _counted);

    public void Dispose() => _listener.Dispose();
}
```

`tests/Notifications.Worker.Tests/GrantCheckedTokenCacheTests.cs`:

```csharp
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The grant ADR-052 names, checked on the token this host was issued.</summary>
public class GrantCheckedTokenCacheTests
{
    private static readonly string[] TheGrant = ["view-users", "query-users", "query-groups"];

    public static TheoryData<string[]> GrantsThatAreNotTheOne()
    {
        TheoryData<string[]> data = [];
        data.Add([]);
        data.Add(["view-users"]);
        data.Add(["query-users", "query-groups"]);
        data.Add(["view-users", "query-users", "query-groups", "manage-users"]);
        data.Add(["view-users", "query-users", "query-groups", "view-clients"]);
        data.Add(["realm-admin"]);
        data.Add(["view-users", "view-users", "query-users", "query-groups"]);
        return data;
    }

    [Theory]
    [MemberData(nameof(GrantsThatAreNotTheOne))]
    public async Task A_token_whose_grant_is_not_exactly_the_one_the_record_names_is_refused(string[] roles)
    {
        using ServiceProvider services = Metrics();
        using ContactCount counted = ContactCounter.Refused(services);

        ContactSourceRefusedException thrown = await Should.ThrowAsync<ContactSourceRefusedException>(
            () => Cache(services, new FixedTokenCache(Jwt(Granted(roles)))).GetAsync("roles", Ct));

        // The count, never the names: a role set in a message is a configuration detail a log carries.
        thrown.Message.ShouldNotContain("manage-users");
        thrown.Message.ShouldNotContain("view-clients");
        counted.Value.ShouldBe(1);
    }

    public static TheoryData<string> PayloadsThatHoldNoGrant() =>
    [
        """{"iss":"x"}""",
        """{"resource_access":"realm-management"}""",
        """{"resource_access":{"account":{"roles":["view-profile"]}}}""",
        """{"resource_access":{"realm-management":{"roles":"view-users"}}}""",
        """{"resource_access":{"realm-management":{"roles":["view-users","query-users",7]}}}"""
    ];

    [Theory]
    [MemberData(nameof(PayloadsThatHoldNoGrant))]
    public async Task A_token_whose_claim_is_absent_or_misshapen_is_refused_rather_than_read_as_empty(string payload)
    {
        using ServiceProvider services = Metrics();
        using ContactCount counted = ContactCounter.Refused(services);

        await Should.ThrowAsync<ContactSourceRefusedException>(
            () => Cache(services, new FixedTokenCache(Jwt(payload))).GetAsync("roles", Ct));

        counted.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_token_carrying_exactly_the_grant_in_any_order_is_handed_on_unchanged()
    {
        using ServiceProvider services = Metrics();
        using ContactCount counted = ContactCounter.Refused(services);
        string issued = Jwt(Granted(["query-groups", "view-users", "query-users"]));

        (await Cache(services, new FixedTokenCache(issued)).GetAsync("roles", Ct)).ShouldBe(issued);

        counted.Value.ShouldBe(0);
    }

    [Fact]
    public async Task The_realms_default_roles_beside_the_grant_are_outside_the_check()
    {
        // ADR-052 names them so nobody widens the check to them: realm_access and the account client.
        using ServiceProvider services = Metrics();
        string issued = Jwt(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["realm_access"] = Roles(["default-roles-commerce", "offline_access", "uma_authorization"]),
            ["resource_access"] = new Dictionary<string, object>
            {
                ["account"] = Roles(["manage-account", "view-profile"]),
                ["realm-management"] = Roles(TheGrant)
            }
        }));

        (await Cache(services, new FixedTokenCache(issued)).GetAsync("roles", Ct)).ShouldBe(issued);
    }

    [Fact]
    public async Task A_refused_client_credential_is_a_refusal_and_a_transport_fault_is_not()
    {
        using ServiceProvider services = Metrics();
        using ContactCount counted = ContactCounter.Refused(services);

        // CachingTokenClient's split, relied on here: InvalidOperationException for a refused client (§11.5).
        await Should.ThrowAsync<ContactSourceRefusedException>(() =>
            Cache(services, new FixedTokenCache(new InvalidOperationException("refused"))).GetAsync("roles", Ct));
        await Should.ThrowAsync<HttpRequestException>(() =>
            Cache(services, new FixedTokenCache(new HttpRequestException("down"))).GetAsync("roles", Ct));

        counted.Value.ShouldBe(1, "the transport fault backs off uncounted");
    }

    [Fact]
    public async Task A_token_that_is_not_a_jwt_is_a_refusal_that_quotes_nothing_of_it()
    {
        using ServiceProvider services = Metrics();
        using ContactCount counted = ContactCounter.Refused(services);

        ContactSourceRefusedException thrown = await Should.ThrowAsync<ContactSourceRefusedException>(() =>
            Cache(services, new FixedTokenCache("an-opaque-token-value")).GetAsync("roles", Ct));

        thrown.Message.ShouldNotContain("an-opaque-token-value");
        thrown.InnerException.ShouldBeNull("the parser's message can quote the token it could not read");
        counted.Value.ShouldBe(1);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The meter factory and the one metrics type, from a container that outlives the assertions.</summary>
    private static ServiceProvider Metrics() =>
        new ServiceCollection().AddMetrics().AddSingleton<ContactMetrics>().BuildServiceProvider();

    private static GrantCheckedTokenCache Cache(IServiceProvider services, ITokenCache inner) =>
        new(inner, services.GetRequiredService<ContactMetrics>(), NullLogger<GrantCheckedTokenCache>.Instance);

    private static Dictionary<string, object> Roles(string[] roles) => new() { ["roles"] = roles };

    /// <summary>A payload carrying <paramref name="roles"/> on <c>realm-management</c> and nothing else.</summary>
    private static string Granted(string[] roles) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["resource_access"] = new Dictionary<string, object> { ["realm-management"] = Roles(roles) }
        });

    /// <summary>An unsigned token over a given payload, since the subject is a nested claim's shape.</summary>
    private static string Jwt(string payload) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}""")) + "." +
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload)) + ".";

    /// <summary>A token source answering with one token or throwing one exception.</summary>
    private sealed class FixedTokenCache : ITokenCache
    {
        private readonly string? _token;
        private readonly Exception? _fault;

        public FixedTokenCache(string token) => _token = token;

        public FixedTokenCache(Exception fault) => _fault = fault;

        public Task<string> GetAsync(string scope, CancellationToken ct) =>
            _fault is null ? Task.FromResult(_token!) : Task.FromException<string>(_fault);
    }
}
```

`tests/Notifications.Worker.Tests/ContactHopTests.cs` — no container, no
collection:

```csharp
using Notifications.Infrastructure.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The contact hop's arithmetic, which <see cref="ContactHop"/>'s numbers satisfy together (§9.7).</summary>
public sealed class ContactHopTests
{
    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = ContactHop.AttemptTimeout * (ContactHop.MaxRetryAttempts + 1)
                         + ContactHop.MaxRetryDelay * ContactHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(ContactHop.TotalRequestTimeout,
            "a total that cancels the last retry makes the retry count a fiction");

        ContactHop.TotalRequestTimeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout);
    }

    [Fact]
    public void The_hop_sits_inside_the_bands_because_keycloak_is_this_deployments_own()
    {
        ContactHop.AttemptTimeout.ShouldBeInRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        ContactHop.TotalRequestTimeout.ShouldBeInRange(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_breaker_breaks_for_less_than_it_samples_and_samples_at_least_twice_an_attempt()
    {
        ContactHop.CircuitBreakerBreakDuration.ShouldBeLessThan(ContactHop.CircuitBreakerSamplingDuration);
        ContactHop.CircuitBreakerSamplingDuration.ShouldBeGreaterThanOrEqualTo(ContactHop.AttemptTimeout * 2,
            "the standard handler's options validation refuses a shorter window at start");
    }
}
```

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~GrantCheckedTokenCacheTests|FullyQualifiedName~ContactHopTests"
```

Expected: compile failure — `Notifications.Infrastructure.Contacts` does not
exist.

- [ ] **Step 3: Write the hop, the meter and the check**

`Contacts/ContactHop.cs`:

```csharp
namespace Notifications.Infrastructure.Contacts;

/// <summary>The contact read's budget, inside §9.7's bands, since Keycloak is this deployment's own.</summary>
/// <remarks>Public for the reason <c>Program</c> is (§4.2); the send worker's lease is sized above its total.</remarks>
public static class ContactHop
{
    /// <summary>Explicit rather than the typed client's type name, so the pipeline can be read back by it.</summary>
    public const string ClientName = "keycloak-contacts";

    /// <summary>The name the resilience options are filed under (§9.7).</summary>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>The per-attempt bound, inside §9.7's one-to-two-second band.</summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(1.2);

    /// <summary>Retries after the first attempt, so one more request than this; a read is safe to repeat.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>The bound on one jittered delay, of which <see cref="RetryDelay"/> is only the nominal.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Inside §9.7's three-to-five-second band and below <c>ServiceOptions.OperationTimeout</c>.</summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(4.5);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>Sized to a worker's call rate: a loop never reaches the endpoint default's hundred.</summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>Shorter than the window, so the breaker keeps its failures while open.</summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>A user representation is a few fields and attributes, so a larger answer is refused unread.</summary>
    public const int MaxAnswerBytes = 64 * 1024;
}
```

The five numbers are `AddressHop`'s, and that is the argument rather than a
copy: both are a worker's read of a party this deployment runs, sized to §9.7's
bands for that reason, where `MailHop` and `CarrierHop` sit outside them.

`Observability/OutboundMeter.cs`, **unless PR-2 landed it**, the same file
PR-2's plan writes:

```csharp
namespace Notifications.Infrastructure.Observability;

/// <summary>The meter for the work that leaves this service (§13.2).</summary>
public static class OutboundMeter
{
    public const string Name = "Notifications.Outbound";
}
```

`Contacts/ContactMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;
using Notifications.Infrastructure.Observability;

namespace Notifications.Infrastructure.Contacts;

/// <summary>Contact reads refused over this host's credential or its grant, the defect ADR-052 counts.</summary>
/// <remarks>On <see cref="OutboundMeter.Name"/>, so §13.2's one <c>AddMeter</c> line covers it.</remarks>
public sealed class ContactMetrics
{
    private readonly Counter<long> _refused;

    public ContactMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(OutboundMeter.Name);
        _refused = meter.CreateCounter<long>(
            "notifications.contact.refused",
            unit: "{refusal}",
            description: "Contact reads refused over this host's credential or its grant rather than failing.");
    }

    public void Refused() => _refused.Add(1);
}
```

`Contacts/GrantCheckedTokenCache.cs`:

```csharp
using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Common.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Contacts;

/// <summary>Refuses a token whose <c>realm-management</c> roles are anything but the grant ADR-052 names.</summary>
/// <remarks>A decorator: the grant is this service's, and <see cref="CachingTokenClient"/> every host's.</remarks>
public sealed partial class GrantCheckedTokenCache(
    ITokenCache inner,
    ContactMetrics metrics,
    ILogger<GrantCheckedTokenCache> log) : ITokenCache
{
    /// <summary>The client Keycloak's admin roles live on, under <c>resource_access</c>.</summary>
    private const string RealmManagement = "realm-management";

    /// <summary><c>view-users</c> and the two query roles it composes on the pinned Keycloak, ordinal-sorted.</summary>
    private static readonly string[] Grant = ["query-groups", "query-users", "view-users"];

    // CA1848 (ADR-019); the messages name neither the token nor the roles (§13.4).
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "The token this host was issued does not carry exactly its grant on realm-management (ADR-052).")]
    private static partial void GrantIsWrong(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "The token this host was issued is not a JWT, so its grant cannot be read (ADR-052).")]
    private static partial void TokenIsUnreadable(ILogger logger);

    public async Task<string> GetAsync(string scope, CancellationToken ct)
    {
        string token;

        try
        {
            token = await inner.GetAsync(scope, ct);
        }
        catch (InvalidOperationException e)
        {
            // Every cause is the deployment's to fix, so each counts; HttpRequestException passes uncounted.
            metrics.Refused();
            throw new ContactSourceRefusedException(
                "The identity provider did not issue this host a usable token (§11.5).", e);
        }

        string[] granted = [.. Roles(token).Order(StringComparer.Ordinal)];

        if (!granted.SequenceEqual(Grant, StringComparer.Ordinal))
        {
            metrics.Refused();
            GrantIsWrong(log);

            throw new ContactSourceRefusedException(
                $"The realm issued this host {granted.Length} realm-management role(s) where ADR-052 names three.");
        }

        return token;
    }

    /// <summary>The roles on <c>realm-management</c>, read and never validated: a signature sizes no grant.</summary>
    /// <remarks>Decoded by hand, as the handler flattens the nested claim to text (ADR-052).</remarks>
    private string[] Roles(string token)
    {
        JwtSecurityToken jwt;

        try
        {
            jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        }
        catch (ArgumentException)
        {
            throw Unreadable();
        }

        try
        {
            using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt.RawPayload));

            return payload.RootElement.TryGetProperty("resource_access", out JsonElement access)
                   && access.ValueKind == JsonValueKind.Object
                   && access.TryGetProperty(RealmManagement, out JsonElement client)
                   && client.ValueKind == JsonValueKind.Object
                   && client.TryGetProperty("roles", out JsonElement roles)
                   && roles.ValueKind == JsonValueKind.Array
                ? [.. roles.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : "")]
                : [];
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            throw Unreadable();
        }
    }

    // The parser's exception is dropped, as its message can quote the token (§13.4).
    private ContactSourceRefusedException Unreadable()
    {
        metrics.Refused();
        TokenIsUnreadable(log);

        return new ContactSourceRefusedException("The realm issued this host a token that is not a JWT (ADR-052).");
    }
}
```

The refusal message says "three" and that is not the prose rule broken: it is
the size of this one grant, which ADR-052 fixes, and not a count of hosts. A
non-string role becomes `""`, which matches no member of the grant, so a
misshapen array is a refusal rather than a shorter set.

- [ ] **Step 4: The meter's export, unless PR-2 landed it**

If `ObservabilityExtensions` already carries `.AddMeter("Notifications.Outbound")`,
skip this step and say so in the PR body. Otherwise, add
`"Notifications.Outbound",` to `ObservabilityTests`' `Required` list after
`"Shipping.Outbox"`, run

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~ObservabilityTests"
```

and see `Every_meter_an_alert_reads_from_is_collected` fail; then, in
`ObservabilityExtensions`, after `.AddMeter("Shipping.Outbox")`, the comment
aligned with its neighbours':

```csharp
                .AddMeter("Notifications.Outbound")                // Notifications' outbound calls (§3.2)
```

and see it pass. The comment names the calls rather than the relay and the
contact read, so it is true whichever of PR-2 and PR-3 lands first.

- [ ] **Step 5: Run; commit**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~GrantCheckedTokenCacheTests|FullyQualifiedName~ContactHopTests"
dotnet build Platform.slnx
git add src/Services/Notifications/Notifications.Infrastructure src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs \
        tests/Common.Web.Tests/ObservabilityTests.cs tests/Notifications.Worker.Tests
git commit -m "feat(notifications): ContactHop, the contact meter and the grant check over realm-management's roles"
```

Expected: green, 0 warnings. Prove the order-insensitivity and the exactness by
mutation: replace `SequenceEqual` with `Grant.All(granted.Contains)` and see the
three widened cases fail; restore.

---

### Task 5: The adapter, its registration, and the host's client credentials

**Files:**
- Create: `src/Services/Notifications/Notifications.Infrastructure/Contacts/ContactAnswerBuffer.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Contacts/KeycloakContactSource.cs`
- Create: `src/Services/Notifications/Notifications.Infrastructure/Contacts/DependencyInjection.cs`
- Modify: `src/Services/Notifications/Notifications.Infrastructure/Observability/MetricsInitialiser.cs`
  — `ContactMetrics` joins the constructor
- Modify: `src/Services/Notifications/Notifications.Worker/Program.cs`
- Modify: `src/BuildingBlocks/Common.Infrastructure/Identity/ServiceIdentityOptions.cs`
  — the summary this host makes false
- Create: `tests/Notifications.TestSupport/RecordingTokenCache.cs`
- Modify: `tests/Notifications.TestSupport/NotificationsWorkerFactory.cs`
- Create: `tests/Notifications.Worker.Tests/Unreachable.cs` (unless PR-2 landed it)
- Modify: `tests/Notifications.Worker.Tests/MetricsRegistrationTests.cs`
- Test: `tests/Notifications.Worker.Tests/ContactSourceTests.cs` — the answers,
  over one shared host and a stub Keycloak
- Test: `tests/Notifications.Worker.Tests/ContactFaultTests.cs` — the faults, a
  host each, since the breaker they fill opens
- Test: `tests/Notifications.Worker.Tests/ContactSourceRegistrationTests.cs` —
  the keys refused at start

**Interfaces:**
- Consumes: Tasks 1 and 4.
- Produces: `AddContactSource(IServiceCollection, IConfiguration, IHostEnvironment)`
  in `Notifications.Infrastructure.Contacts.DependencyInjection`, with
  `BaseUrlKey` = `"ContactSource:BaseUrl"` and `RealmKey` = `"ContactSource:Realm"`;
  `IContactSource` resolvable from the host; `MetricsInitialiser(…, ContactMetrics contact)`;
  `NotificationsWorkerFactory`'s `contactSourceBaseUrl` parameter,
  `UnreachableContactSource`, `LocalRealm`, `ContactScope`, `Tokens` and the
  virtual `ConfigureTokens`; `RecordingTokenCache`; `Unreachable.Sql` and
  `Unreachable.Rabbit`.

**Why a method of its own, and why `Program.cs` holds the credentials.** ADR-055:
an outbound hop registers in a method named for it, beside its layer's, and the
host calls it — here because the scheme rule needs `IHostEnvironment`, which
`AddNotificationsInfrastructure` is not given. The same record keeps the host's
client-credential bindings in `Program.cs`: the handler, the token client, and
`GrantCheckedTokenCache`'s decoration, exactly as Shipping's worker holds them.

**Why HTTPS outside Development.** The bearer token this client presents reads
every user's profile in the realm, and the answer carries a mailbox. §11.3
refuses a plain-HTTP authority outside Development for the weaker reason that a
token passes over it, and `AddCarrierGateway` refuses a plain carrier for its
key; the contact read carries both a credential and personal data.

**Why two keys rather than one derived from `Identity:Authority`.** Keycloak 26
can serve its admin API on a hostname of its own (`hostname-admin`), so the
admin base is a value the deployment is given; the realm is a key beside it so
that the admin path is built once at start and checked as one path segment.

- [ ] **Step 1: The test support**

`tests/Notifications.TestSupport/RecordingTokenCache.cs`, Shipping's:

```csharp
using Common.Infrastructure.Identity;

namespace Notifications.TestSupport;

/// <summary>An <see cref="ITokenCache"/> that answers without a network call, a different token each time.</summary>
/// <remarks>Distinct, so the attempts §11.5's retried handler makes can be told apart.</remarks>
public sealed class RecordingTokenCache : ITokenCache
{
    private int _issued;

    /// <summary>Every scope this has been asked for, in order.</summary>
    public List<string> Scopes { get; } = [];

    /// <summary>How many tokens have been handed out.</summary>
    public int Issued => _issued;

    public Task<string> GetAsync(string scope, CancellationToken ct)
    {
        lock (Scopes)
        {
            Scopes.Add(scope);
        }

        return Task.FromResult($"token-{Interlocked.Increment(ref _issued)}");
    }
}
```

`NotificationsWorkerFactory` gains one parameter after its last, defaulted, so
every existing caller compiles unchanged:

```csharp
    string contactSourceBaseUrl = NotificationsWorkerFactory.UnreachableContactSource
```

the members, beside `UnreachableAuthority`:

```csharp
    /// <summary>The test contact source: HTTPS, which no environment refuses; <c>.invalid</c> never resolves.</summary>
    public const string UnreachableContactSource = "https://keycloak.invalid/";

    /// <summary>The realm every contact is read from: §14.1's, and the realm export's.</summary>
    public const string LocalRealm = "commerce";

    /// <summary>The scope this host requests, whose mapper writes the claim its grant lives in (ADR-052).</summary>
    public const string ContactScope = "roles";

    /// <summary>The token source the credential handler draws on, so no test needs an identity provider.</summary>
    public RecordingTokenCache Tokens { get; } = new();
```

five settings after the authority's, with
`using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;`
and `using Common.Infrastructure.Identity;` in sorted position:

```csharp
            .UseSetting(ContactRegistration.BaseUrlKey, contactSourceBaseUrl)
            .UseSetting(ContactRegistration.RealmKey, LocalRealm)
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientId", "notifications-worker-test")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", "not-a-real-secret")
            .UseSetting($"{ServiceIdentityOptions.SectionName}:Scope", ContactScope)
```

`ConfigureTokens(services);` as the line after `ConfigureAuthentication(services);`
inside `ConfigureServices`, and the hook beside `ConfigureAuthentication`, with
`using Microsoft.Extensions.DependencyInjection.Extensions;`:

```csharp
    /// <summary>Puts <see cref="Tokens"/> in place of the host's own token source; a host may override it.</summary>
    protected virtual void ConfigureTokens(IServiceCollection services)
    {
        services.RemoveAll<ITokenCache>();
        services.AddSingleton<ITokenCache>(Tokens);
    }
```

The factory is the fifth of `docs/secrets.md`'s places for the three
`Identity__Client__*` keys and the two `ContactSource__*` keys, and the one
where the right value is a fake: `notifications-worker-test` names no realm
client, and `not-a-real-secret` says what it is.

`tests/Notifications.Worker.Tests/Unreachable.cs`, **unless PR-2 landed it**,
with PR-2's content exactly:

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

- [ ] **Step 2: Write the failing tests**

`tests/Notifications.Worker.Tests/ContactSourceTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Common.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Contacts;
using Notifications.TestSupport;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's answers read from the client's side, over a stub Keycloak's admin API on loopback.</summary>
/// <remarks>Nothing here fills the breaker: each status is one the standard handler hands back (§9.7).</remarks>
public sealed class ContactSourceTests : IClassFixture<ContactSourceTests.KeycloakStub>
{
    /// <summary>One stub and one host for the class, as a host over an unreachable broker is slow to stop.</summary>
    public sealed class KeycloakStub : IDisposable
    {
        public KeycloakStub()
        {
            Server = WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });
            Factory = new NotificationsWorkerFactory(
                Unreachable.Sql,
                Unreachable.Rabbit,
                contactSourceBaseUrl: Server.Urls[0] + "/");
        }

        public WireMockServer Server { get; }

        public NotificationsWorkerFactory Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            Server.Stop();
        }
    }

    private const string Mailbox = "aigerim@example.test";

    private readonly WireMockServer _keycloak;
    private readonly NotificationsWorkerFactory _factory;

    public ContactSourceTests(KeycloakStub stub)
    {
        _keycloak = stub.Server;
        _factory = stub.Factory;
        _keycloak.ResetMappings();
        _keycloak.ResetLogEntries();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string PathOf(Guid customer) =>
        $"/admin/realms/{NotificationsWorkerFactory.LocalRealm}/users/{customer:D}";

    private static async Task<ContactLookup> ReadAsync(WebApplicationFactory<Program> host, Guid customer)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>().GetAsync(customer, Ct);
    }

    private Task<ContactLookup> ReadAsync(Guid customer) => ReadAsync(_factory, customer);

    /// <summary>A user as Keycloak represents one, with a name and an attribute the adapter must never bind.</summary>
    private static JsonObject User(string? email = Mailbox, bool enabled = true) =>
        new()
        {
            ["id"] = Guid.CreateVersion7().ToString(),
            ["username"] = "aigerim",
            ["firstName"] = "Айгерім",
            ["lastName"] = "Сейітқызы",
            ["enabled"] = enabled,
            ["email"] = email,
            ["attributes"] = new JsonObject { ["phone"] = new JsonArray("+77010000000") }
        };

    private Guid Answer(int status, string? body = null)
    {
        Guid customer = Guid.CreateVersion7();
        IResponseBuilder response = Response.Create().WithStatusCode(status);

        if (body is not null)
            response = response.WithHeader("Content-Type", "application/json").WithBody(body);

        _keycloak.Given(Request.Create().WithPath(PathOf(customer)).UsingGet()).RespondWith(response);

        return customer;
    }

    private Guid Answer(JsonObject user) => Answer(200, user.ToJsonString());

    private int Calls(string path) => _keycloak.LogEntries.Count(e => e.RequestMessage!.Path == path);

    [Fact]
    public async Task A_customer_answers_with_the_mailbox_and_the_request_is_the_admin_apis_and_carries_the_token()
    {
        Guid customer = Answer(User());

        ContactLookup lookup = await ReadAsync(customer);

        lookup.ShouldBe(new ContactLookup.Found(Mailbox, null));

        ILogEntry call = _keycloak.LogEntries.ShouldHaveSingleItem();
        call.RequestMessage!.Method.ShouldBe("GET");
        call.RequestMessage.Path.ShouldBe(PathOf(customer));
        call.RequestMessage.Headers!["Authorization"][0].ShouldStartWith("Bearer token-");
        call.RequestMessage.Headers["Accept"][0].ShouldContain("application/json");
    }

    [Fact]
    public async Task The_token_is_asked_for_under_the_scope_whose_mapper_writes_the_grant()
    {
        int before = _factory.Tokens.Scopes.Count;

        await ReadAsync(Answer(User()));

        string[] asked = [.. _factory.Tokens.Scopes.Skip(before)];
        asked.ShouldNotBeEmpty("the read drew no token");
        asked.ShouldAllBe(s => s == NotificationsWorkerFactory.ContactScope);
    }

    [Theory]
    [InlineData("kk", "kk")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("not a locale", null)]
    [InlineData("kk\n", null)]
    [InlineData("<script>", null)]
    [InlineData("", null)]
    public async Task A_locale_is_kept_when_it_has_the_shape_and_dropped_rather_than_refused_when_not(
        string stored,
        string? expected)
    {
        JsonObject user = User();
        user["attributes"]!["locale"] = new JsonArray(stored);

        (await ReadAsync(Answer(user))).ShouldBe(new ContactLookup.Found(Mailbox, expected));
    }

    [Fact]
    public async Task A_locale_with_two_values_is_dropped_rather_than_one_chosen()
    {
        JsonObject user = User();
        user["attributes"]!["locale"] = new JsonArray("kk", "ru");

        (await ReadAsync(Answer(user))).ShouldBe(new ContactLookup.Found(Mailbox, null));
    }

    [Fact]
    public async Task A_user_with_no_attributes_at_all_has_no_locale()
    {
        JsonObject user = User();
        user.Remove("attributes");

        (await ReadAsync(Answer(user))).ShouldBe(new ContactLookup.Found(Mailbox, null));
    }

    [Fact]
    public async Task No_such_user_is_no_such_customer_after_one_call()
    {
        Guid customer = Answer(404);

        (await ReadAsync(customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
        Calls(PathOf(customer)).ShouldBe(1);
    }

    [Fact]
    public async Task A_disabled_user_is_no_such_customer_though_keycloak_still_answers_with_a_mailbox()
    {
        (await ReadAsync(Answer(User(enabled: false)))).ShouldBeOfType<ContactLookup.NoSuchCustomer>(
            "an account taken back from somebody is most often a disabled one (ADR-052)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_user_with_no_mailbox_is_no_such_customer(string? email)
    {
        (await ReadAsync(Answer(User(email)))).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_user_whose_email_member_is_absent_is_no_such_customer()
    {
        JsonObject user = User();
        user.Remove("email");

        (await ReadAsync(Answer(user))).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_mailbox_carrying_a_line_break_arrives_as_it_is_stored_for_the_mail_channel_to_refuse()
    {
        const string broken = "aigerim@example.test\r\nbcc: someone@example.test";

        (await ReadAsync(Answer(User(broken)))).ShouldBe(new ContactLookup.Found(broken, null));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_token_is_counted_once_and_thrown_rather_than_read_as_an_absence(int status)
    {
        using ContactCount counted = ContactCounter.Refused(_factory.Services);
        Guid customer = Answer(status);

        await Should.ThrowAsync<ContactSourceRefusedException>(() => ReadAsync(customer));

        counted.Value.ShouldBe(1, "a revoked grant is a defect somebody must see, not an outage to wait out");
        Calls(PathOf(customer)).ShouldBe(1, "a refusal is no transient fault, so nothing retries it");
    }

    [Fact]
    public async Task A_redirect_is_not_followed_so_the_token_goes_nowhere_else()
    {
        Guid customer = Guid.CreateVersion7();
        _keycloak.Given(Request.Create().WithPath(PathOf(customer)).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(302)
                .WithHeader("Location", _keycloak.Urls[0] + "/elsewhere"));
        _keycloak.Given(Request.Create().WithPath("/elsewhere").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(User().ToJsonString()));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync(customer));

        Calls("/elsewhere").ShouldBe(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"email":"aigerim@example.test"}""")]
    [InlineData("""{"enabled":"true","email":"aigerim@example.test"}""")]
    [InlineData("""{"enabled":true,"email":42}""")]
    public async Task A_body_that_is_not_a_user_is_refused_by_shape_and_quotes_none_of_it(string body)
    {
        using ContactCount counted = ContactCounter.Refused(_factory.Services);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => ReadAsync(Answer(200, body)));

        thrown.Message.ShouldNotContain("aigerim");
        thrown.InnerException.ShouldBeNull("the parser's message can quote the body");
        counted.Value.ShouldBe(0, "a malformed answer is not a refused credential");
    }

    [Fact]
    public async Task A_mailbox_wider_than_the_owners_own_column_is_refused_by_field_and_never_by_value()
    {
        string wide = new string('ж', ContactLimits.MaxEmailLength) + "@example.test";

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => ReadAsync(Answer(User(wide))));

        thrown.Message.ShouldContain("email");
        thrown.Message.ShouldNotContain("жжж");
    }

    [Fact]
    public async Task A_mailbox_at_the_owners_own_width_is_accepted()
    {
        string widest = new string('a', ContactLimits.MaxEmailLength - "@example.test".Length) + "@example.test";

        (await ReadAsync(Answer(User(widest)))).ShouldBe(new ContactLookup.Found(widest, null));
    }

    [Fact]
    public async Task A_base_address_with_a_path_keeps_it_and_the_realm_follows_it()
    {
        using NotificationsWorkerFactory prefixed = new(
            Unreachable.Sql, Unreachable.Rabbit, contactSourceBaseUrl: _keycloak.Urls[0] + "/auth");
        Guid customer = Guid.CreateVersion7();
        _keycloak.Given(Request.Create().WithPath("/auth" + PathOf(customer)).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(User().ToJsonString()));

        (await ReadAsync(prefixed, customer)).ShouldBeOfType<ContactLookup.Found>();
    }

    [Fact]
    public void The_built_pipeline_fits_every_attempt_and_every_bounded_delay_inside_the_total()
    {
        // Off the built host, by the name the handler registered under, so this checks the registration (§9.7).
        HttpStandardResilienceOptions options = _factory.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(ContactHop.ResilienceOptionsName);

        options.AttemptTimeout.Timeout.ShouldBe(ContactHop.AttemptTimeout);
        options.Retry.MaxRetryAttempts.ShouldBe(ContactHop.MaxRetryAttempts);
        options.Retry.MaxDelay.ShouldBe(ContactHop.MaxRetryDelay, "jitter makes the nominal delay no bound");
        options.Retry.ShouldRetryAfterHeader.ShouldBeFalse("a Retry-After past MaxDelay would spend the budget");
        options.TotalRequestTimeout.Timeout.ShouldBe(ContactHop.TotalRequestTimeout);
        options.CircuitBreaker.MinimumThroughput.ShouldBe(ContactHop.CircuitBreakerMinimumThroughput);
    }

    [Fact]
    public void The_host_draws_its_token_through_the_grant_check()
    {
        using ProgramTokensFactory host = new();

        host.Services.GetRequiredService<ITokenCache>().ShouldBeOfType<GrantCheckedTokenCache>();
    }

    [Fact]
    public void A_host_given_no_client_secret_does_not_start()
    {
        using WebApplicationFactory<Program> host = _factory.WithWebHostBuilder(b =>
            b.UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", ""));

        // The factory builds the host on first use, and a host refusing to start races its disposal.
        Should.Throw<Exception>(() => host.Services);
    }

    /// <summary>The host with <c>Program</c>'s own token source left in place.</summary>
    private sealed class ProgramTokensFactory() : NotificationsWorkerFactory(Unreachable.Sql, Unreachable.Rabbit)
    {
        protected override void ConfigureTokens(IServiceCollection services)
        {
        }
    }
}
```

`tests/Notifications.Worker.Tests/ContactFaultTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.Infrastructure.Contacts;
using Notifications.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The contact read's transient answers, a host each, since the breaker they fill opens.</summary>
public sealed class ContactFaultTests : IDisposable
{
    private readonly WireMockServer _keycloak =
        WireMockServer.Start(new WireMockServerSettings { Urls = ["http://127.0.0.1:0"] });

    private readonly NotificationsWorkerFactory _factory;

    private readonly Guid _customer = Guid.CreateVersion7();

    public ContactFaultTests() =>
        _factory = new NotificationsWorkerFactory(
            Unreachable.Sql, Unreachable.Rabbit, contactSourceBaseUrl: _keycloak.Urls[0] + "/");

    public void Dispose()
    {
        _factory.Dispose();
        _keycloak.Stop();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Path => $"/admin/realms/{NotificationsWorkerFactory.LocalRealm}/users/{_customer:D}";

    private const string User = """{"enabled":true,"email":"aigerim@example.test"}""";

    private int Calls => _keycloak.LogEntries.Count(e => e.RequestMessage!.Path == Path);

    private async Task<ContactLookup> ReadAsync(NotificationsWorkerFactory? host = null)
    {
        await using AsyncServiceScope scope = (host ?? _factory).Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>().GetAsync(_customer, Ct);
    }

    [Fact]
    public async Task A_server_fault_is_retried_inside_the_budget_then_thrown_uncounted()
    {
        using ContactCount counted = ContactCounter.Refused(_factory.Services);
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(503));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync());

        Calls.ShouldBe(ContactHop.MaxRetryAttempts + 1);
        counted.Value.ShouldBe(0, "an outage is not a decision anybody took");
    }

    [Fact]
    public async Task A_fault_that_clears_is_retried_and_each_attempt_asks_for_a_token_again()
    {
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet())
            .InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet())
            .InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody(User));

        (await ReadAsync()).ShouldBeOfType<ContactLookup.Found>();

        Calls.ShouldBe(2);

        // The credential handler inside the pipeline runs once per attempt (§11.5).
        _keycloak.LogEntries.Select(e => e.RequestMessage!.Headers!["Authorization"][0])
            .Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task An_answer_larger_than_the_bound_is_an_attempt_the_pipeline_retries_and_then_a_fault()
    {
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"enabled\":true,\"email\":\"" + new string('a', ContactHop.MaxAnswerBytes) + "\"}"));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync());

        Calls.ShouldBe(ContactHop.MaxRetryAttempts + 1);
    }

    [Fact]
    public async Task A_stalled_owner_is_given_up_on_within_the_total_budget()
    {
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(User).WithDelay(TimeSpan.FromSeconds(10)));
        DateTimeOffset started = DateTimeOffset.UtcNow;

        Exception thrown = await Should.ThrowAsync<Exception>(() => ReadAsync());

        thrown.ShouldNotBeOfType<ContactSourceRefusedException>();
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(ContactHop.TotalRequestTimeout + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        _keycloak.Given(Request.Create().WithPath(Path).UsingGet()).RespondWith(Response.Create().WithStatusCode(503));

        // The breaker sits inside the retry, so one read is MaxRetryAttempts + 1 attempts toward the throughput.
        while (Calls < ContactHop.CircuitBreakerMinimumThroughput)
            await Should.ThrowAsync<Exception>(() => ReadAsync());

        int before = Calls;

        await Should.ThrowAsync<Exception>(() => ReadAsync());

        Calls.ShouldBe(before, "once open, it refuses without a request leaving this process");
    }

    [Fact]
    public async Task An_unreachable_owner_throws_rather_than_answering()
    {
        using NotificationsWorkerFactory dead = new(Unreachable.Sql, Unreachable.Rabbit);

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync(dead));
    }
}
```

`tests/Notifications.Worker.Tests/ContactSourceRegistrationTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Notifications.Infrastructure.Contacts;
using Notifications.TestSupport;
using Shouldly;
using Xunit;
using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;

namespace Notifications.Worker.Tests;

/// <summary>The contact source's two keys, refused at registration so a host that cannot read never starts.</summary>
public sealed class ContactSourceRegistrationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("keycloak/")]
    [InlineData("ftp://keycloak.example/")]
    public void A_base_address_that_is_not_an_absolute_http_one_is_refused_by_key(string? configured)
    {
        Should.Throw<InvalidOperationException>(() => Register(configured, Environments.Development))
            .Message.ShouldContain(ContactRegistration.BaseUrlKey);
    }

    [Theory]
    [InlineData("https://keycloak.example/?realm=master")]
    [InlineData("https://keycloak.example/#admin")]
    public void A_base_address_with_a_query_or_fragment_is_refused(string configured)
    {
        Should.Throw<InvalidOperationException>(() => Register(configured, Environments.Production))
            .Message.ShouldContain("query or fragment");
    }

    [Fact]
    public void A_base_address_carrying_user_information_is_refused_without_echoing_it()
    {
        string message = Should.Throw<InvalidOperationException>(
            () => Register("https://notifications:hunter2@keycloak.example/", Environments.Production)).Message;

        message.ShouldContain("user information");
        message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Plain_http_outside_development_is_refused()
    {
        Should.Throw<InvalidOperationException>(() => Register("http://keycloak.example/", Environments.Production))
            .Message.ShouldContain("plain HTTP outside Development");
    }

    [Fact]
    public void Https_outside_development_and_plain_http_in_it_are_each_accepted()
    {
        // The controls, so the two refusals above cannot pass against a rule nothing satisfies.
        Should.NotThrow(() => Register("https://keycloak.example/", Environments.Production));
        Should.NotThrow(() => Register("http://keycloak:8080/", Environments.Development));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("commerce/../master")]
    [InlineData("..")]
    [InlineData("comm erce")]
    [InlineData("%2e%2e")]
    [InlineData("commerce?x=1")]
    public void A_realm_that_is_not_one_plain_path_segment_is_refused_by_key(string? realm)
    {
        Should.Throw<InvalidOperationException>(
                () => Register("https://keycloak.example/", Environments.Production, realm))
            .Message.ShouldContain(ContactRegistration.RealmKey);
    }

    [Fact]
    public void A_host_that_names_no_contact_source_does_not_start()
    {
        using NotificationsWorkerFactory factory = new(Unreachable.Sql, Unreachable.Rabbit, contactSourceBaseUrl: "");

        Should.Throw<Exception>(() => factory.Services);
    }

    private static IServiceCollection Register(
        string? baseUrl,
        string environment,
        string? realm = NotificationsWorkerFactory.LocalRealm)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ContactRegistration.BaseUrlKey] = baseUrl,
                [ContactRegistration.RealmKey] = realm
            })
            .Build();

        TestEnvironment host = new() { EnvironmentName = environment };

        return new ServiceCollection().AddContactSource(configuration, host);
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

`MetricsRegistrationTests`: `BuildServices()`'s configuration gains the two
keys, its body calls the hop's registration as `Program.cs` does, and the
selector test names the new type. With
`using Notifications.Infrastructure.Contacts;`,
`using Microsoft.Extensions.FileProviders;` and
`using ContactRegistration = Notifications.Infrastructure.Contacts.DependencyInjection;`
in sorted position, each where absent, in the dictionary:

```csharp
                    // Read eagerly by AddContactSource; HTTPS because the environment below is not Development.
                    [ContactRegistration.BaseUrlKey] = "https://notifications-keycloak.invalid/",
                    [ContactRegistration.RealmKey] = "commerce"
```

after `services.AddNotificationsInfrastructure(configuration);`:

```csharp
        services.AddContactSource(configuration, new TestEnvironment());
```

the summary over `BuildServices`, a touched block rewritten whole:

```csharp
    /// <summary>The registration helpers the worker's <c>Program</c> calls, over unreachable configuration.</summary>
```

the two-line comment opening
`Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be`, whose "Both
helpers run" stops being true once a third registers a metrics type, cut to
Shipping's one line unless PR-2 already cut it:

```csharp
        // The collection, not a built provider, which cannot enumerate its registrations.
```

a `TestEnvironment` class at the foot of the file — Production, Shipping's
`MetricsRegistrationTests.TestEnvironment` verbatim — unless PR-1's render or
PR-2 already put one there; and in
`The_metrics_selector_actually_selects_something`:

```csharp
        registered.ShouldContain(typeof(ContactMetrics));
```

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~ContactSourceTests|FullyQualifiedName~ContactFaultTests|FullyQualifiedName~ContactSourceRegistrationTests|FullyQualifiedName~MetricsRegistrationTests"
```

Expected: compile failure on `AddContactSource`, `ContactRegistration` and the
factory's new parameter. Once Step 3 gives the suite the registration and
before Step 4's initialiser edit,
`Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be` fails on "add
it to MetricsInitialiser, or to NotForced with a reason" — the red that proves
the selector sees the new type.

- [ ] **Step 3: The adapter, the buffer and the registration**

`Contacts/ContactAnswerBuffer.cs`:

```csharp
namespace Notifications.Infrastructure.Contacts;

/// <summary>Reads each answer whole inside the attempt, so a stalled or oversize body meets the budget.</summary>
internal sealed class ContactAnswerBuffer : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response = await base.SendAsync(request, ct);

        try
        {
            await response.Content.LoadIntoBufferAsync(ContactHop.MaxAnswerBytes, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
```

`Contacts/KeycloakContactSource.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Notifications.Application.Contacts;

namespace Notifications.Infrastructure.Contacts;

/// <summary>The client half of ADR-052's contact read, over Keycloak's admin API.</summary>
/// <remarks>
/// Binds <c>enabled</c>, <c>email</c> and <c>attributes.locale</c> and nothing else (ADR-052), so a name or
/// another attribute never reaches a type. A transient outcome escapes for the worker's backoff.
/// </remarks>
internal sealed class KeycloakContactSource(HttpClient http, ContactMetrics metrics) : IContactSource
{
    public async Task<ContactLookup> GetAsync(Guid customerId, CancellationToken ct)
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException("The empty id is no customer.", nameof(customerId));

        using HttpRequestMessage message = new(HttpMethod.Get, $"users/{customerId:D}");
        using HttpResponseMessage response = await http.SendAsync(message, ct);

        // No such user; the disabled and the mailbox-less below collapse into the same answer (ADR-052).
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new ContactLookup.NoSuchCustomer();

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            metrics.Refused();

            throw new ContactSourceRefusedException(
                $"Keycloak refused this host's token with {(int)response.StatusCode} (ADR-052).");
        }

        // A redirect lands here too: none is followed, so a token that reads every user goes nowhere else.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Keycloak answered the contact read with {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }

        using JsonDocument user = await ParseAsync(response, ct);

        return Contact(user.RootElement);
    }

    private static ContactLookup Contact(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Keycloak answered the contact read with no user.");

        if (!user.TryGetProperty("enabled", out JsonElement enabled) ||
            enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidOperationException("Keycloak answered a user with no boolean enabled.");
        }

        // An account taken back from somebody is most often a disabled one, and Keycloak still answers its mailbox.
        if (!enabled.GetBoolean())
            return new ContactLookup.NoSuchCustomer();

        string? email = Email(user);

        return string.IsNullOrWhiteSpace(email)
            ? new ContactLookup.NoSuchCustomer()
            : new ContactLookup.Found(email, Locale(user));
    }

    // The field, never the value: a mailbox in a message is what a log carries (§13.4).
    private static string? Email(JsonElement user)
    {
        if (!user.TryGetProperty("email", out JsonElement email) || email.ValueKind == JsonValueKind.Null)
            return null;

        if (email.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("Keycloak answered a user whose email is not a string.");

        string value = email.GetString()!;

        return value.Length <= ContactLimits.MaxEmailLength
            ? value
            : throw new InvalidOperationException(
                $"Keycloak answered an email longer than {ContactLimits.MaxEmailLength}.");
    }

    // Dropped, never refused, when it is not one tag of the shape: an absent locale is an answer (ADR-052).
    private static string? Locale(JsonElement user) =>
        user.TryGetProperty("attributes", out JsonElement attributes)
        && attributes.ValueKind == JsonValueKind.Object
        && attributes.TryGetProperty("locale", out JsonElement locale)
        && locale.ValueKind == JsonValueKind.Array
        && locale.GetArrayLength() == 1
        && locale[0].ValueKind == JsonValueKind.String
        && LanguageTag.IsOne(locale[0].GetString())
            ? locale[0].GetString()
            : null;

    // The parser's exception is dropped, as its message can quote the body (§13.4).
    private static async Task<JsonDocument> ParseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream body = await response.Content.ReadAsStreamAsync(ct);

            return await JsonDocument.ParseAsync(body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Keycloak answered the contact read with no JSON body.");
        }
    }
}
```

**A mailbox carrying CR or LF is passed through unchanged, and that is
deliberate.** Spec section 8 makes the mail channel the one judge of a mailbox
— MimeKit's parser, refusing it as `Refused(NotAMailbox)` and recording
`Undeliverable: not_a_mailbox`. An adapter that refused it here would have to
choose an outcome ADR-052's table does not have — it is not "does not exist" —
and would put a second, different mailbox rule in the service. The bound here
is the owner's column, and nothing narrower.

`Contacts/DependencyInjection.cs`:

```csharp
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notifications.Application.Contacts;
using Polly;

namespace Notifications.Infrastructure.Contacts;

/// <summary>ADR-052's contact read, in a method of its own as ADR-055 places a hop; the host calls it.</summary>
public static class DependencyInjection
{
    private const string Section = "ContactSource";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string RealmKey = $"{Section}:Realm";

    public static IServiceCollection AddContactSource(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        Uri baseUrl = BaseUrl(configuration, environment);

        // Every request is relative to one realm's admin path, built and checked once here.
        Uri admin = new(baseUrl, $"admin/realms/{Realm(configuration)}/");

        services.AddSingleton<ContactMetrics>();
        services.AddTransient<ContactAnswerBuffer>();

        IHttpClientBuilder client = services.AddHttpClient<IContactSource, KeycloakContactSource>(
            ContactHop.ClientName,
            http =>
            {
                http.BaseAddress = admin;
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            });

        // A followed redirect would carry a token that reads every user wherever it pointed (ADR-052).
        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Resilience first, so it is outermost and a retried attempt asks for a token again (§9.7, §11.5).
        client.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = ContactHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = ContactHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = ContactHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = ContactHop.RetryDelay;
            options.Retry.MaxDelay = ContactHop.MaxRetryDelay;

            // MaxDelay does not cap a Retry-After, so one long header would spend the budget ContactHop counts on.
            options.Retry.ShouldRetryAfterHeader = false;

            options.CircuitBreaker.FailureRatio = ContactHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = ContactHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = ContactHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = ContactHop.CircuitBreakerBreakDuration;
        });

        client.AddHttpMessageHandler<Common.Infrastructure.Identity.ClientCredentialsHandler>();

        // Innermost, so a body that stalls or runs over is an attempt the pipeline bounds and retries.
        client.AddHttpMessageHandler<ContactAnswerBuffer>();

        return services;
    }

    // No message echoes the configured value, since an address can carry user information.
    private static Uri BaseUrl(IConfiguration configuration, IHostEnvironment environment)
    {
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} is not configured. Notifications cannot read a contact.");
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute HTTP(S) address.");
        }

        if (parsed.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries user information; this host authenticates with §11.5's grant alone.");
        }

        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries a query or fragment, which no request to Keycloak would keep.");
        }

        // HTTPS outside Development, as for the authority (§11.3): the token reads every user, the answer is personal.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "a token that reads every user would travel in the clear.");
        }

        // A trailing slash, or a relative request would replace the base address's last segment.
        return parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");
    }

    // One path segment of plain characters, since the name is written into every request's path.
    private static string Realm(IConfiguration configuration)
    {
        string? realm = configuration[RealmKey];
        if (string.IsNullOrWhiteSpace(realm))
        {
            throw new InvalidOperationException(
                $"{RealmKey} is not configured. Notifications cannot name the realm whose users it reads.");
        }

        bool segment = realm is not ("." or "..")
            && realm.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

        return segment
            ? realm
            : throw new InvalidOperationException($"{RealmKey} is not a realm name this host can put in a path.");
    }
}
```

`ClientCredentialsHandler` is named by its namespace rather than a `using`,
because this file's own class is `DependencyInjection` and so is
`Common.Infrastructure.Identity`'s neighbour in the editor's completion list;
either form compiles, and the qualified one is what says the handler is the
host's, registered in `Program.cs` (ADR-055). If the reviewer prefers the
`using`, it is one line and changes nothing.

- [ ] **Step 4: The initialiser, the host, and the sentence this makes false**

`MetricsInitialiser` gains `ContactMetrics contact` as its **last**
parameter, with its guard as the last statement and
`using Notifications.Infrastructure.Contacts;` in sorted position:

```csharp
        ArgumentNullException.ThrowIfNull(contact);
```

The file's membership rule — can the service run for an hour without
constructing it — is met: a Notifications that has sent nothing never reads a
contact.

`Notifications.Worker/Program.cs`, after
`builder.Services.AddNotificationsInfrastructure(builder.Configuration);   // §4.2, §7.1`
and after PR-2's `AddMailChannel` line if it is there, so the order is the
same whichever lands first, and before the worker's no-permission-policy
comment, with
`using Common.Infrastructure.Identity;` and
`using Notifications.Infrastructure.Contacts;` in sorted position:

```csharp
// ADR-052's contact read; its address is read, and its scheme checked, eagerly (ADR-055).
builder.Services.AddContactSource(builder.Configuration, builder.Environment);

// §11.5's client-credentials registrations, this host's own (§15.4, ADR-055).
builder.Services.AddTransient<ClientCredentialsHandler>();
builder.Services.AddSingleton<CachingTokenClient>();

// ADR-052: this host holds itself to its grant, because the realm gate cannot read a service account's roles.
builder.Services.AddSingleton<ITokenCache>(sp => new GrantCheckedTokenCache(
    sp.GetRequiredService<CachingTokenClient>(),
    sp.GetRequiredService<ContactMetrics>(),
    sp.GetRequiredService<ILogger<GrantCheckedTokenCache>>()));

// Validated at start: IOptions<T> always resolves, so ValidateOnBuild cannot see a forgotten binding (§15.4).
builder.Services
    .AddOptions<ServiceIdentityOptions>()
    .BindConfiguration(ServiceIdentityOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The token client's transport carries no ClientCredentialsHandler, which would recurse.
string authority = builder.Configuration[AuthenticationExtensions.AuthorityKey]!;

builder.Services
    .AddHttpClient(CachingTokenClient.HttpClientName, client =>
        client.BaseAddress = new Uri(authority.TrimEnd('/') + "/"));

// The key's name, so a refused discovery document says which key to fix (§11.3, §11.5).
builder.Services.AddSingleton(new AuthorityKeyName(AuthenticationExtensions.AuthorityKey));
```

These are Shipping's worker's lines with the address read's names replaced; the
worker's `ArchitectureTests` exempts `Program` and its generated helpers, which
is where the lambda above compiles.

`ServiceIdentityOptions`' summary says the options are "bound by each host that
calls a peer", and Notifications' worker binds them and calls Keycloak and no
peer. One line, a summary of one sentence:

```csharp
/// <summary>§15.4's options for §11.5's client-credentials grant, bound by each host that holds one.</summary>
```

- [ ] **Step 5: Run; commit**

```bash
dotnet build Platform.slnx
dotnet test tests/Notifications.Worker.Tests --filter "Category!=Integration"
dotnet test tests/Notifications.Worker.Tests
```

Expected: 0 warnings, and green — the contact suites, the registration suite in
both directions, and every PR-1 host still starting, because the factory
supplies the five keys. Prove two orderings by mutation: move
`AddHttpMessageHandler<ClientCredentialsHandler>()` above
`AddStandardResilienceHandler` and see
`A_fault_that_clears_is_retried_and_each_attempt_asks_for_a_token_again` fail
on one distinct token; move `ContactAnswerBuffer` above the resilience handler
and see `An_answer_larger_than_the_bound_is_an_attempt_the_pipeline_retries_and_then_a_fault`
fail on one call; restore both.

```bash
git add src/Services/Notifications src/BuildingBlocks/Common.Infrastructure/Identity/ServiceIdentityOptions.cs \
        tests/Notifications.TestSupport tests/Notifications.Worker.Tests
git commit -m "feat(notifications): the Keycloak contact adapter, its registration and the host's client credentials"
```

---

### Task 6: Against a real Keycloak

ADR-052: "each client is proved both ways against a token Keycloak issued:
accepted with the grant, refused without." The owner here *is* the realm, so
the adapter's answers and the client's grant are proved against one container
importing the shipped export — the second suite in the solution to start one,
for a reason Task 8 writes into §11.5.

**Files:**
- Create: `tests/Notifications.Worker.Tests/RepositoryRoot.cs` (unless PR-2
  landed it)
- Create: `tests/Notifications.Worker.Tests/KeycloakFixture.cs` — the fixture,
  its hosts and its collection
- Test: `tests/Notifications.Worker.Tests/KeycloakContactSourceTests.cs`
- Modify: `.github/secret-scan/allowed/tests.txt` — the fixture's two
  credential-shaped constants

**Staging what the shipped realm refuses, on this container alone.** Keycloak
26's declarative user profile refuses an email with CR or LF at its `email`
validator, and with internationalisation off it treats `locale` as an
unmanaged attribute, which it neither stores nor returns by default. Both are
right for the shipped realm and both would make two of section 13's cases
unstageable. So the fixture, after import, fetches
`/admin/realms/commerce/users/profile`, sets `unmanagedAttributePolicy` to
`ADMIN_EDIT` and removes the `email` validator from the `email` attribute, and
puts the profile back — through the admin API, on the test container, as
`Web.Bff.Tests`' fixture creates its unrelated client. The shipped export is
not touched, and a test of the fixture's own (below) proves the staging took.

- [ ] **Step 1: The fixture**

`RepositoryRoot.cs`, **unless PR-2 landed it**, with PR-2's content exactly:

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

`KeycloakFixture.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Infrastructure.Identity;
using Common.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notifications.TestSupport;
using Testcontainers.Keycloak;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>A real Keycloak importing the shipped realm, the owner ADR-052's contact read asks.</summary>
/// <remarks>
/// The user profile is loosened on this container alone, so a value the shipped realm refuses at sign-up can
/// still reach the adapter (ADR-052); the export itself is what every other fixture imports.
/// </remarks>
public sealed class KeycloakFixture : IAsyncLifetime
{
    /// <summary>§14.1's image, which a file test holds equal to the Compose baseline's.</summary>
    public const string Image = "quay.io/keycloak/keycloak:26.0";

    public const string Realm = NotificationsWorkerFactory.LocalRealm;

    /// <summary>ADR-052's contact reader and its local default (§14.1), matching the realm.</summary>
    public const string ContactClient = "notifications-worker";
    public const string ContactSecret = "local-dev-notifications-secret";

    /// <summary>A credentialed client of the same realm holding no role on <c>realm-management</c> (§11.5).</summary>
    public const string UngrantedClient = "web-bff";
    public const string UngrantedSecret = "local-dev-secret";

    /// <summary>The bootstrap admin, literals the builder also sets, as the module exposes no accessor.</summary>
    private const string AdminUser = "admin";
    private const string AdminPassword = "admin";

    private readonly KeycloakContainer _keycloak = new KeycloakBuilder()
        .WithImage(Image)
        .WithUsername(AdminUser)
        .WithPassword(AdminPassword)
        .WithResourceMapping(
            new FileInfo(Path.Combine(RepositoryRoot.Locate(), "deploy", "compose", "keycloak", "realm-export.json")),
            "/opt/keycloak/data/import/")
        .WithCommand("--import-realm")
        .Build();

    private readonly List<ContactHost> _hosts = [];

    public HttpClient Http { get; private set; } = null!;

    /// <summary>The server's root, where both the realm and its admin API live.</summary>
    public string BaseAddress => _keycloak.GetBaseAddress().TrimEnd('/') + "/";

    /// <summary>The realm's authority, as a host would configure it.</summary>
    public string Authority => $"{BaseAddress}realms/{Realm}";

    /// <summary>The real host holding the contact reader's credential, through its own grant check.</summary>
    public ContactHost Granted { get; private set; } = null!;

    /// <summary>The same host holding a credential with no grant, refused by its own check before any read.</summary>
    public ContactHost Ungranted { get; private set; } = null!;

    /// <summary>The ungranted credential with the check bypassed, so Keycloak's own refusal is what is met.</summary>
    public ContactHost UncheckedUngranted { get; private set; } = null!;

    /// <summary>The contact reader's id with a secret the realm does not hold.</summary>
    public ContactHost WrongSecret { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _keycloak.StartAsync();
        Http = new HttpClient();

        await WaitForRealmAsync();
        await LoosenUserProfileAsync();

        Granted = Host(ContactClient, ContactSecret, grantChecked: true);
        Ungranted = Host(UngrantedClient, UngrantedSecret, grantChecked: true);
        UncheckedUngranted = Host(UngrantedClient, UngrantedSecret, grantChecked: false);
        WrongSecret = Host(ContactClient, "not-the-realms-secret", grantChecked: true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ContactHost host in _hosts)
            host.Dispose();

        Http?.Dispose();
        await _keycloak.DisposeAsync();
    }

    /// <summary>A client-credentials token, failing loudly if the realm refuses the client.</summary>
    public async Task<string> TokenAsync(string clientId, string secret, string scope)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = scope
        });

        using HttpResponseMessage response = await Http.PostAsync(
            $"{Authority}/protocol/openid-connect/token", form, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>A user created through the admin API, with a name the adapter must never bind; its id.</summary>
    public async Task<Guid> CreateUserAsync(string? email, bool enabled = true, string? locale = null)
    {
        Dictionary<string, object?> user = new()
        {
            ["username"] = $"customer-{Guid.CreateVersion7():N}",
            ["enabled"] = enabled,
            ["firstName"] = "Айгерім",
            ["lastName"] = "Сейітқызы"
        };

        if (email is not null)
            user["email"] = email;

        if (locale is not null)
            user["attributes"] = new Dictionary<string, string[]> { ["locale"] = [locale] };

        using HttpRequestMessage request = new(HttpMethod.Post, $"{BaseAddress}admin/realms/{Realm}/users")
        {
            Content = JsonContent.Create(user)
        };

        using HttpResponseMessage response = await AsAdminAsync(request);
        response.EnsureSuccessStatusCode();

        return Guid.Parse(response.Headers.Location!.Segments[^1]);
    }

    /// <summary>A user as the master admin reads it, so a test can tell staging from the adapter.</summary>
    public async Task<JsonElement> UserAsAdminAsync(Guid id)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{BaseAddress}admin/realms/{Realm}/users/{id:D}");
        using HttpResponseMessage response = await AsAdminAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private ContactHost Host(string clientId, string secret, bool grantChecked)
    {
        ContactHost host = new(BaseAddress, Authority, clientId, secret, grantChecked);
        _hosts.Add(host);

        return host;
    }

    private async Task LoosenUserProfileAsync()
    {
        string url = $"{BaseAddress}admin/realms/{Realm}/users/profile";

        using HttpRequestMessage read = new(HttpMethod.Get, url);
        using HttpResponseMessage current = await AsAdminAsync(read);
        current.EnsureSuccessStatusCode();

        JsonNode profile = (await current.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
        profile["unmanagedAttributePolicy"] = "ADMIN_EDIT";

        JsonObject email = profile["attributes"]!.AsArray()
            .Single(a => (string?)a!["name"] == "email")!
            .AsObject();
        email["validations"]?.AsObject().Remove("email");

        using HttpRequestMessage write = new(HttpMethod.Put, url) { Content = JsonContent.Create(profile) };
        using HttpResponseMessage written = await AsAdminAsync(write);
        written.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> AsAdminAsync(HttpRequestMessage request)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private async Task<string> AdminTokenAsync()
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = AdminUser,
            ["password"] = AdminPassword
        });

        using HttpResponseMessage response = await Http.PostAsync(
            $"{BaseAddress}realms/master/protocol/openid-connect/token", form, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        return body.GetProperty("access_token").GetString()!;
    }

    /// <summary>Polls until the imported realm answers, a later event than the process listening (§14.1).</summary>
    private async Task WaitForRealmAsync()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(
                    $"{Authority}/.well-known/openid-configuration", TestContext.Current.CancellationToken);

                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException(
            $"Keycloak never served realm '{Realm}' at {Authority}; the import may have failed with the process up.");
    }

    /// <summary>The real host pointed at this container and holding one client's credential.</summary>
    /// <remarks>
    /// The address is passed and the authority captured, never both of one parameter (CS9107, an error under ADR-019).
    /// </remarks>
    public sealed class ContactHost(
        string baseAddress,
        string authority,
        string clientId,
        string secret,
        bool grantChecked)
        : NotificationsWorkerFactory(Unreachable.Sql, Unreachable.Rabbit, contactSourceBaseUrl: baseAddress)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // A later setting replaces an earlier one, so these supersede the unreachable authority and the fake.
            builder
                .UseSetting(AuthenticationExtensions.AuthorityKey, authority)
                .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientId", clientId)
                .UseSetting($"{ServiceIdentityOptions.SectionName}:ClientSecret", secret);
        }

        protected override void ConfigureTokens(IServiceCollection services)
        {
            // Program's own source is the grant check over CachingTokenClient; bypassed only when that is the subject.
            if (grantChecked)
                return;

            services.RemoveAll<ITokenCache>();
            services.AddSingleton<ITokenCache>(sp => sp.GetRequiredService<CachingTokenClient>());
        }
    }
}

/// <summary>§12.4's per-assembly collection: one Keycloak, and a category every member class inherits.</summary>
[CollectionDefinition(nameof(KeycloakCollection))]
[Trait("Category", "Integration")]
public sealed class KeycloakCollection : ICollectionFixture<KeycloakFixture>;
```

`ContactHost` is public because the fixture exposes it; it passes the internal
`Unreachable` constants as arguments, which accessibility allows. It takes the
two addresses as strings rather than the fixture, because a primary
constructor parameter both passed to the base and captured is CS9107, an error
under ADR-019.

- [ ] **Step 2: Write the tests**

`tests/Notifications.Worker.Tests/KeycloakContactSourceTests.cs`:

```csharp
using System.Buffers.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application.Contacts;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>ADR-052's contact read and its client's grant, against the pinned Keycloak and the shipped realm.</summary>
[Collection(nameof(KeycloakCollection))]
public sealed class KeycloakContactSourceTests(KeycloakFixture keycloak)
{
    private static string Mailbox() => $"c{Guid.CreateVersion7():N}@example.test";

    private static async Task<ContactLookup> ReadAsync(KeycloakFixture.ContactHost host, Guid customer)
    {
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IContactSource>()
            .GetAsync(customer, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_customer_answers_with_the_mailbox_and_no_locale_while_the_realm_holds_none()
    {
        string mailbox = Mailbox();
        Guid customer = await keycloak.CreateUserAsync(mailbox);

        ContactLookup lookup = await ReadAsync(keycloak.Granted, customer);

        // Internationalisation is off in the shipped realm, so an absent locale is the answer (ADR-052).
        lookup.ShouldBe(new ContactLookup.Found(mailbox, null));
    }

    [Fact]
    public async Task No_such_user_is_no_such_customer()
    {
        (await ReadAsync(keycloak.Granted, Guid.CreateVersion7())).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_disabled_user_is_no_such_customer()
    {
        Guid customer = await keycloak.CreateUserAsync(Mailbox(), enabled: false);

        (await ReadAsync(keycloak.Granted, customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task A_user_with_no_email_is_no_such_customer()
    {
        Guid customer = await keycloak.CreateUserAsync(email: null);

        (await ReadAsync(keycloak.Granted, customer)).ShouldBeOfType<ContactLookup.NoSuchCustomer>();
    }

    [Fact]
    public async Task An_email_carrying_a_line_break_arrives_as_keycloak_holds_it()
    {
        // The mail channel refuses it as no mailbox (spec, section 8); this half proves it reaches that channel whole.
        string broken = $"{Mailbox()}\r\nbcc: someone@example.test";
        Guid customer = await keycloak.CreateUserAsync(broken);

        (await ReadAsync(keycloak.Granted, customer)).ShouldBe(new ContactLookup.Found(broken, null));
    }

    [Fact]
    public async Task A_locale_of_the_shape_is_kept()
    {
        string mailbox = Mailbox();
        Guid customer = await keycloak.CreateUserAsync(mailbox, locale: "kk");

        (await ReadAsync(keycloak.Granted, customer)).ShouldBe(new ContactLookup.Found(mailbox, "kk"));
    }

    [Fact]
    public async Task A_locale_that_is_not_one_is_dropped_and_the_customer_still_found()
    {
        string mailbox = Mailbox();
        Guid customer = await keycloak.CreateUserAsync(mailbox, locale: "not a locale!");

        (await ReadAsync(keycloak.Granted, customer)).ShouldBe(new ContactLookup.Found(mailbox, null));
    }

    [Fact]
    public async Task The_container_holds_the_values_the_adapter_is_asked_to_keep_or_drop()
    {
        // The dropped-locale case above passes vacuously if Keycloak never stored the value; this says it did.
        string broken = $"{Mailbox()}\r\nbcc: someone@example.test";
        Guid customer = await keycloak.CreateUserAsync(broken, locale: "not a locale!");

        JsonElement user = await keycloak.UserAsAdminAsync(customer);

        user.GetProperty("email").GetString().ShouldBe(broken);
        user.GetProperty("attributes").GetProperty("locale")[0].GetString().ShouldBe("not a locale!");
    }

    [Fact]
    public async Task The_token_issued_to_the_contact_reader_carries_exactly_the_grant_and_no_service_audience()
    {
        string token = await keycloak.TokenAsync(
            KeycloakFixture.ContactClient, KeycloakFixture.ContactSecret, NotificationsWorkerFactory.ContactScope);

        JwtSecurityToken jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        using JsonDocument payload = JsonDocument.Parse(Base64Url.DecodeFromChars(jwt.RawPayload));

        string[] roles =
        [
            .. payload.RootElement.GetProperty("resource_access").GetProperty("realm-management")
                .GetProperty("roles").EnumerateArray().Select(r => r.GetString()).OfType<string>()
        ];

        // Exactly the three, which is the pinned Keycloak expanding view-users's composite into the token.
        roles.ShouldBe(["query-groups", "query-users", "view-users"], ignoreOrder: true);

        // No audience any service validates and no permission: a stolen secret reads users and calls nothing.
        jwt.Audiences.ShouldNotContain(AuthenticationExtensions.Audience);
        jwt.Claims.ShouldNotContain(c => c.Type == PermissionClaim.Type);
    }

    [Fact]
    public async Task A_token_issued_to_a_client_without_the_grant_is_refused_by_the_hosts_own_check()
    {
        using ContactCount counted = ContactCounter.Refused(keycloak.Ungranted.Services);
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        await Should.ThrowAsync<ContactSourceRefusedException>(() => ReadAsync(keycloak.Ungranted, customer));

        counted.Value.ShouldBe(1, "counted where the refusal was decided");
    }

    [Fact]
    public async Task Keycloak_itself_refuses_a_token_without_the_grant_and_the_refusal_is_counted()
    {
        // The check bypassed, so this is the owner's enforcement and the adapter's mapping of it (ADR-052).
        using ContactCount counted = ContactCounter.Refused(keycloak.UncheckedUngranted.Services);
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        await Should.ThrowAsync<ContactSourceRefusedException>(
            () => ReadAsync(keycloak.UncheckedUngranted, customer));

        counted.Value.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_client_secret_is_a_refusal_rather_than_an_outage()
    {
        using ContactCount counted = ContactCounter.Refused(keycloak.WrongSecret.Services);
        Guid customer = await keycloak.CreateUserAsync(Mailbox());

        await Should.ThrowAsync<ContactSourceRefusedException>(() => ReadAsync(keycloak.WrongSecret, customer));

        counted.Value.ShouldBe(1, "the token endpoint refusing the client is ADR-052's fourth row");
    }
}
```

`PermissionClaim` is `Common.Web`'s, which the suite reaches through the
worker's reference; a `using Common.Web;` covers it and
`AuthenticationExtensions` together.

- [ ] **Step 3: Run**

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~KeycloakContactSourceTests"
```

This class is in `KeycloakCollection`, which carries
`[Trait("Category", "Integration")]`: it needs a running Docker daemon and is
never skipped without one. Expected: green — and, **with Task 2's realm edit
reverted** as a mutation check,
`The_token_issued_to_the_contact_reader_carries_exactly_the_grant_and_no_service_audience`
fails on `EnsureSuccessStatusCode` (`unauthorized_client`) and the granted
reads throw `ContactSourceRefusedException`. Restore the realm.

**If `The_container_holds_the_values_the_adapter_is_asked_to_keep_or_drop`
fails, the staging did not take, and two cases are proving nothing.** Two
outcomes are possible and each is recorded in the PR body rather than worked
around: Keycloak 26.0 answering `400` to the CR/LF create even with the `email`
validator removed — then no realm user can carry one, the CR/LF case moves to
the stub suite alone (it is already there), and spec section 8's "a test that
tries it through a realm user" is reported against the spec; or the locale
dropped under `ADMIN_EDIT` — then the fixture also sets
`internationalizationEnabled: true` with `supportedLocales: ["en"]` on the
container's realm, and the case is re-run.

- [ ] **Step 4: Prove the negative half is not vacuous**

Temporarily replace `view-users` in `service-account-notifications-worker`'s
`clientRoles` with `view-clients`, re-run, and expect the exact-grant test to
fail on the role set and every `Granted` read to throw
`ContactSourceRefusedException`. Then restore `view-users` and remove the
whole `service-account-notifications-worker` user: the token is issued, its
`resource_access` has no `realm-management` member, and the same reads are
refused at the check. Restore it. These are the two mutations that tell a realm
which grants the role from one that merely holds the client.

- [ ] **Step 5: The secret scan, and commit**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: two `credential-assignment` findings on
`tests/Notifications.Worker.Tests/KeycloakFixture.cs`, for `ContactSecret` and
`UngrantedSecret` (the last shares `local-dev-secret`'s fingerprint with the
existing entries for other files, and an entry is per path). Add, beside the
other fixture entries in `tests.txt`, with the fingerprints the scan printed:

```
tests/Notifications.Worker.Tests/KeycloakFixture.cs | credential-assignment | <fingerprint> | ADR-052's contact reader's local client default, held where the fixture and the realm meet.
tests/Notifications.Worker.Tests/KeycloakFixture.cs | credential-assignment | <fingerprint> | The BFF's local client default, the realm's ungranted credential the refusal is proved with.
```

`not-the-realms-secret` is an argument and not an assignment, so the scan does
not report it. Re-run; expected clean.

```bash
dotnet test tests/Notifications.Worker.Tests
git add tests/Notifications.Worker.Tests .github/secret-scan/allowed/tests.txt
git commit -m "test(notifications): the contact read and the client's grant against a real Keycloak"
```

---

### Task 7: The keys in Compose, matched to the realm

**Files:**
- Modify: `deploy/compose/services/notifications.yml` — the worker's five keys
  and its `keycloak` dependency
- Modify: `deploy/compose/.env.example` — the override, commented out
- Modify: `deploy/compose/README.md` — only if a Notifications host-run block
  exists (below)
- Test: `tests/Notifications.Worker.Tests/ContactDeploymentTests.cs`
- Modify: `.github/secret-scan/allowed/deploy.txt`

- [ ] **Step 1: Write the failing file tests**

`tests/Notifications.Worker.Tests/ContactDeploymentTests.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Notifications.TestSupport;
using Shouldly;
using Xunit;

namespace Notifications.Worker.Tests;

/// <summary>The realm and the Compose unit hold one client, one secret, one scope and one realm (§11.5).</summary>
/// <remarks>An unknown client id passes <c>ValidateOnStart</c> and fails every contact read (§15.4).</remarks>
public sealed partial class ContactDeploymentTests
{
    private static string File(params string[] path) =>
        System.IO.File.ReadAllText(Path.Combine([RepositoryRoot.Locate(), .. path]));

    private static string Unit() => File("deploy", "compose", "services", "notifications.yml");

    private static JsonElement Realm() =>
        JsonDocument.Parse(File("deploy", "compose", "keycloak", "realm-export.json")).RootElement;

    private static JsonElement Client() =>
        Realm().GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == KeycloakFixture.ContactClient);

    [Fact]
    public void The_realm_and_the_deployment_hold_the_same_secret()
    {
        string secret = Client().GetProperty("secret").GetString()!;

        Unit().ShouldContain($"Identity__Client__ClientSecret: \"${{NOTIFICATIONS_CLIENT_SECRET:-{secret}}}\"");
    }

    [Fact]
    public void The_deployment_names_the_client_the_scope_and_the_realm_the_export_holds()
    {
        string unit = Unit();

        unit.ShouldContain($"Identity__Client__ClientId: \"{KeycloakFixture.ContactClient}\"");
        unit.ShouldContain($"Identity__Client__Scope: \"{NotificationsWorkerFactory.ContactScope}\"");
        unit.ShouldContain($"ContactSource__Realm: \"{Realm().GetProperty("realm").GetString()}\"");
    }

    [Fact]
    public void The_scope_the_deployment_requests_is_one_the_client_holds()
    {
        // Keycloak refuses a scope the client does not hold with invalid_scope, at the first read.
        string[] held =
        [
            .. Client().GetProperty("defaultClientScopes").EnumerateArray()
                .Concat(Client().GetProperty("optionalClientScopes").EnumerateArray())
                .Select(s => s.GetString()!)
        ];

        held.ShouldContain(NotificationsWorkerFactory.ContactScope);
    }

    [Fact]
    public void The_suites_keycloak_is_the_image_compose_runs()
    {
        Match image = KeycloakImage().Match(File("deploy", "compose", "infrastructure.yml"));

        image.Success.ShouldBeTrue("infrastructure.yml names no Keycloak image");
        image.Groups["image"].Value.ShouldBe(KeycloakFixture.Image);
    }

    [GeneratedRegex(@"^\s*image:\s*(?<image>quay\.io/keycloak/keycloak:\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex KeycloakImage();
}
```

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~ContactDeploymentTests"
```

Expected: the first two fail on `ShouldContain` — PR-1's unit sets none of
these keys; the last two pass, and the image test is there so that a Keycloak
bump in `infrastructure.yml` that the suite does not follow fails a test
rather than leaving the suite proving an older realm.

- [ ] **Step 2: The Compose unit**

On `notifications-worker`'s `environment`, after PR-1's (and PR-2's, if landed)
last key:

```yaml
      # ADR-052's contact read: the realm's admin API, under the realm that issues this host's token. Read and
      # checked at start; plain HTTP is Development's alone (§15.4).
      ContactSource__BaseUrl: "http://keycloak:8080/"
      ContactSource__Realm: "commerce"
      # Required by ValidateOnStart (§15.4); the default is the realm export's. roles is the scope whose mapper
      # writes the realm-management roles this host checks; it takes no commerce-api, so no service accepts it.
      Identity__Client__ClientId: "notifications-worker"
      Identity__Client__ClientSecret: "${NOTIFICATIONS_CLIENT_SECRET:-local-dev-notifications-secret}"
      Identity__Client__Scope: "roles"
```

and under its `depends_on`, unless PR-1 put it there:

```yaml
      # The first contact read asks the realm for a token, which races the import under service_started.
      keycloak: { condition: service_healthy }
```

`KC_HOSTNAME` fixes the issuer at `http://localhost:8080` while
`KC_HOSTNAME_BACKCHANNEL_DYNAMIC` lets a container reach Keycloak by its
Compose name — `infrastructure.yml` argues both — so a token minted at
`keycloak:8080` carries the issuer the admin API checks, and Step 4 proves it.

- [ ] **Step 3: `.env.example`, and the README only where it has a block**

`.env.example`, after Shipping's commented override, five lines on the BFF's
terms:

```
# Notifications' worker client secret (ADR-052), commented out for the BFF's
# reason above: services/notifications.yml's inline default must match the
# realm export, and a value changed here alone has every contact read refused
# at the token endpoint.
# NOTIFICATIONS_CLIENT_SECRET=local-dev-notifications-secret
```

`deploy/compose/README.md` carries a host-run block per service the override
excludes. **If PR-1 or PR-2 wrote one for `Notifications.Worker`**, it gains
these five exports after its authority, and its lead sentence gains "and,
since it reads contacts from Keycloak, the two `ContactSource__*` keys and the
three `Identity__Client__*` ones
([ADR-052](../../docs/backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md))":

```bash
export ContactSource__BaseUrl='http://localhost:8080/'
export ContactSource__Realm='commerce'
export Identity__Client__ClientId='notifications-worker'
export Identity__Client__ClientSecret='local-dev-notifications-secret'
export Identity__Client__Scope='roles'
```

**If neither wrote one, this PR writes none**: a block is a whole host's recipe,
and a Notifications worker run on the host needs PR-2's relay keys and PR-4's
jurisdiction too, so the five lines alone would be a recipe that does not
start. The block is owed by the PR that completes the host's keys, and
*Self-review* names it.

- [ ] **Step 4: The scan, the image test, and Compose**

```bash
py -3.12 .github/secret-scan/secret_scan.py
```

Expected: a `credential-assignment` finding on `services/notifications.yml`
for the secret default — and on `README.md` if Step 3 added exports. Add each
to `deploy.txt` beside the file's other entries, with the scan's fingerprint:

```
deploy/compose/services/notifications.yml | credential-assignment | <fingerprint> | Section 14.1's local default for ADR-052's contact reader's client secret, behind its own variable; the realm export holds the same value.
```

The commented `.env.example` line is not reported, as Shipping's is not.

```bash
dotnet test tests/Notifications.Worker.Tests --filter "FullyQualifiedName~ContactDeploymentTests"
docker compose -f deploy/compose/docker-compose.yml config -q
docker compose -f deploy/compose/docker-compose.yml down -v
docker compose -f deploy/compose/docker-compose.yml up -d --wait
docker compose -f deploy/compose/docker-compose.yml ps notifications-worker
```

Expected: green; `config -q` silent; the stack healthy with
`notifications-worker` running — its five keys passing `ValidateOnStart` and
`AddContactSource`'s checks under Development. `down -v` first, because
Keycloak imports once and a volume from before this PR holds a realm without
the client. Where a native RabbitMQ on this machine holds 5672, bring the stack
up with the usual scratchpad-only port override for that service and nothing
else.

Then the read the worker will make, from inside the Compose network, by the
names the unit uses — which is what proves the issuer and the admin path agree
there, and is the PR body's evidence:

```bash
NET=$(docker compose -f deploy/compose/docker-compose.yml config --format json | jq -r '.networks | keys[0]')
NET_NAME=$(docker network ls --format '{{.Name}}' | grep "${NET}$" | head -1)
TOKEN=$(docker run --rm --network "$NET_NAME" curlimages/curl -s \
  -d grant_type=client_credentials -d client_id=notifications-worker \
  -d client_secret=local-dev-notifications-secret -d scope=roles \
  http://keycloak:8080/realms/commerce/protocol/openid-connect/token | jq -r .access_token)
DEMO=$(docker run --rm --network "$NET_NAME" curlimages/curl -s -H "Authorization: Bearer $TOKEN" \
  "http://keycloak:8080/admin/realms/commerce/users?username=demo&exact=true" | jq -r '.[0].id')
docker run --rm --network "$NET_NAME" curlimages/curl -s -o /dev/null -w '%{http_code}\n' \
  -H "Authorization: Bearer $TOKEN" "http://keycloak:8080/admin/realms/commerce/users/$DEMO"
docker run --rm --network "$NET_NAME" curlimages/curl -s -o /dev/null -w '%{http_code}\n' \
  -H "Authorization: Bearer $TOKEN" "http://keycloak:8080/admin/realms/commerce/clients"
docker compose -f deploy/compose/docker-compose.yml down -v
```

Expected: `200` for the user and `403` for the client list — the grant reads
users and nothing beside them, the narrowness ADR-052 sizes, measured. Record
both lines in the PR body.

- [ ] **Step 5: Commit**

```bash
git add deploy/compose tests/Notifications.Worker.Tests/ContactDeploymentTests.cs .github/secret-scan/allowed/deploy.txt
git commit -m "feat(compose): Notifications' contact source and client credentials, matched to the realm"
```

---

### Task 8: The chapters, Appendix B and `docs/secrets.md`

**Files:**
- Modify: `docs/backend-architecture/11-identity-authorization.md` — §11.5's
  opening count, its callout, the audience paragraph, the table's row, and the
  two paragraphs on the one suite that runs Keycloak
- Modify: `docs/backend-architecture/02-architecture-at-a-glance.md` — §2.2's
  two edges
- Modify: `docs/backend-architecture/03-bounded-contexts.md` — §3.1's
  paragraph on the collaboration the map leaves undrawn
- Modify: `docs/backend-architecture/09-messaging.md` — §9.7's hop-budget
  paragraph and its registration paragraph
- Modify: `docs/backend-architecture/12-test-strategy.md` — §12.1's
  outbound-hop row and §12.4's two sentences
- Modify: `docs/backend-architecture/14-local-development.md` — §14.1's
  Ordering comment and §14.2's credentials comment
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's
  required-for-some-hosts paragraph, its three rows, and two new rows
- Modify: `docs/backend-architecture/appendix-b-licences.md` — two rows' use
- Modify: `docs/secrets.md` — the rotation clause, the procedure's two steps,
  the provisioning paragraph, and the exception row
- Modify: `Directory.Packages.props` — the comment over
  `Testcontainers.Keycloak`'s pin
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj` — the comment over its
  `Testcontainers.Keycloak` reference
- Modify: `tests/Web.Bff.Tests/KeycloakFixture.cs` — the class summary

Every edit below names the set or cites ADR-052, and none writes a count of
credentialed hosts.

- [ ] **Step 1: §11.5**

The opening count. Before:

> **In this blueprint that is two hosts: the BFF** (§9.7) **and Shipping's
> worker** ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).

After:

> **In this blueprint those are the hosts the table below gives a client of
> their own**, each by the decision its row cites — the BFF by §9.7, and the
> workers by
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md).

The paragraph's next sentence — the gateway forwards, every other service
exchanges events — is unchanged.

The callout, whose opener counts one host decided and not built, is rewritten
whole. Before, its first sentence through "event.": "**One more host is decided
and not built.** Shipping's client is minted here, … Notifications' is still
owed, and reads a mailbox from Keycloak, because ADR-035 left neither value a
way to arrive by event." and its last sentence "Shipping's joined it with
Ordering's method rather than with Shipping, because the grant is the address
owner's to serve." After, the whole callout:

> **Each host ADR-052 decides holds a client of its own, sized by what it
> reads.** Shipping's holds one role on `commerce-api`, because the address
> owner serves its read as a method; Notifications' holds `view-users` on
> `realm-management`, because the mailbox's owner is the realm itself.
> [ADR-035](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
> left neither value a way to arrive by event. The argument above is why each
> took a record rather than a registration: the count of hosts holding a
> client secret is the count of synchronous couplings, and it moves only by a
> decision that says what each new secret reads when it is stolen.

In *The scope has to become an audience*, the sentence ending "…and so does
Shipping's, for the same reason and by the same mapper (ADR-052):" becomes "…and
so does Shipping's, for the same reason and by the same mapper (ADR-052).
Notifications' takes neither the scope nor the audience, because the one thing
it calls is the realm's own admin API:" — the colon still introducing the
table.

The table's row, after `shipping-worker`'s:

```
| Client `notifications-worker` | Service accounts enabled, `commerce-api` in neither scope list, `view-users` on `realm-management` for its service account — with the two query roles that role composes | The contact reader ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)). It reads the realm's admin API, so it holds no audience any service validates: a stolen secret reads every user's profile and calls no service. The worker refuses a token whose `realm-management` roles are not exactly those three |
```

The paragraph beginning "It is also the **one** suite that runs a real
Keycloak, and it arrives with the BFF (PR-19)…" loses its count. Before, its
first sentence: "It is also the **one** suite that runs a real Keycloak, and it
arrives with the BFF (PR-19) because client credentials were the BFF's
mechanism alone until ADR-052 minted `shipping-worker`, whose grant the same
suite proves both ways." After: "It arrives with the BFF (PR-19), because
client credentials were the BFF's mechanism alone until ADR-052 minted
`shipping-worker`, whose grant the same suite proves both ways." The rest of
the paragraph is unchanged.

And the section's last paragraph, which ends "…asserts the BFF's and
`shipping-worker`'s grants there rather than buying `Shipping.Worker.Tests` a
second Keycloak container.", gains:

> `notifications-worker`'s grant is the exception that reason does not reach,
> and it is proved in `Notifications.Worker.Tests` against a Keycloak of that
> suite's own: the contact read *is* the realm's admin API, so the owner its
> adapter is tested against and the realm its grant is proved in are one
> container, and the container is what the read costs rather than what the
> grant does.

- [ ] **Step 2: §2.2 and §3.1**

§2.2's diagram, after the two Shipping edges:

```
    NOT -->|HTTPS, the contact read| IDP
    NOT -.->|client credentials| IDP
```

Both, as Shipping's pair is drawn: the dashed edge is the token, and the solid
one the read, which here goes to the same node.

§3.1's paragraph is the one ADR-052's §3.2 row reaches — §3.2's table has no
column for a synchronous read, and Shipping's equivalent sentence was written
here for that reason. Before:

> One collaboration is of another kind, and the map leaves it undrawn because it
> draws messages: a Shipping worker reads a delivery address from Ordering over
> gRPC, off every request path
> ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).
> It is the departure [§2.3](02-architecture-at-a-glance.md)'s callout records,
> not a round trip whose return leg is an event.

After:

> ADR-052's reads are of another kind, and the map leaves them undrawn because
> it draws messages: a Shipping worker reads a delivery address from Ordering
> over gRPC, and a Notifications worker reads a mailbox and a locale from
> Keycloak's admin API, each off every request path
> ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)).
> Each is the departure [§2.3](02-architecture-at-a-glance.md)'s callout
> records, not a round trip whose return leg is an event.

§3.2's own text — "Shipping and Notifications expose no public API at all —
they are reached only through the broker" — is about what reaches them, and
stays true.

- [ ] **Step 3: §9.7 and §4.1**

*The hop budget*'s paragraph, after "…so it spends no hop of this budget and
§2.3's callout records the departure.", gains one sentence:

> The same record has a Notifications worker read a mailbox from Keycloak's
> admin API on the same terms, which reaches no service of the platform at
> all.

The paragraph after the rules says "so a host that holds client credentials is
a host that calls a peer (§11.5)", which Notifications' worker makes false. Its
first two sentences become:

> For a peer call, the caller's own `Program.cs` (§4.1) registers it and §4.2's
> helper deliberately registers none of it, so a host that holds client
> credentials is a host that makes a synchronous call under a grant of its own
> (§11.5). `Web.Bff` registers the pricing hop; Shipping's worker registers the
> address read
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> gives it, and Notifications' worker the contact read the same record gives
> it, both off every request path, and `ContactHop` sits inside the band
> because Keycloak is the deployment's own and no third party.

The rest of the paragraph — the third-party clients, and PR-2's `MailHop` where
it has landed — is unchanged.

§4.1's Ordering sample makes the same claim in its comment on outbound
identity, "Outbound identity belongs to the hosts that call a peer (§9.7,
§11.5), and Ordering is not one of them." Its last two lines become two, so
the block stays at five:

```csharp
    // over the broker. Outbound identity belongs to a host calling out under a
    // grant of its own (§9.7, §11.5), and Ordering is not one.
```

- [ ] **Step 4: §12**

§12.1's outbound-hop row. After:

```
| Outbound hop | One of §9.7's synchronous calls, from its caller's host: the timeout hierarchy, the credential handler's position inside the resilience pipeline, and §11.5's realm for a caller whose grant is proved there | `WebApplicationFactory` + the peer on loopback — a real gRPC server or a stub HTTP one; a class in a suite proving a grant also runs a real Keycloak | < 1 s, and seconds for a Keycloak class | One suite per caller | `Web.Bff.Tests`, `Shipping.Worker.Tests`, `Notifications.Worker.Tests` |
```

§12.4's *The outbound hop*, whose first paragraph ends "`Shipping.Worker.Tests`
holds the first two for the address read ADR-052 adds, against a stub Ordering
on loopback.", gains:

> `Notifications.Worker.Tests` holds all three for the contact read the same
> record adds: the first two against a stub Keycloak on loopback, and the realm
> against a real one, which is also the owner the read asks.

and the sentence "The realm is the third, and it is the one suite in the
solution that starts a real Keycloak (§11.5)." becomes "The realm is the third,
and only a suite that holds it starts a real Keycloak (§11.5)."

§12.6's verdict on the address read is not extended: Keycloak is no service of
this platform, so there is no provider suite to link a consumer's file into,
and the contact suite runs the provider itself at the pinned version, which is
the verification ADR-023's form exists to approximate.

- [ ] **Step 5: §14.1 and §14.2**

§14.1's Ordering comment, whose last sentence reads "A host holds client
credentials when it calls a peer (§9.7, §11.5, ADR-052)." — Notifications'
worker holds them and calls no peer. After:

```
      # The authority, to validate inbound tokens (§11.2). No Identity__Client__*:
      # Ordering calls no peer synchronously — prices come from a local
      # projection (§6.4) and the rest goes over the broker. §15.4 says which
      # hosts hold client credentials (§11.5, ADR-052).
```

§14.2's sample comment "only a host that calls another service (§11.5)
presents them" becomes "only a host that makes a synchronous call under a grant
of its own (§11.5) presents them". The comment over `web-bff`'s
`WithPlatformIdentity` call already cites §15.4 for which other hosts hold
one, and stays.

- [ ] **Step 6: §15.4**

The paragraph that opens "**Required-for-some-hosts is a third category**",
from "`Identity__Client__*` is mandatory" to "…what each new one reads when it
is stolen.", becomes:

> `Identity__Client__*` is mandatory for a host that makes a synchronous call
> under a grant of its own and meaningless for one that does not — which in
> this blueprint is **every host but the BFF and the workers
> [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
> gives a read**. The gateway forwards the caller's token rather than minting
> its own; every other service exchanges events over the broker and reads local
> projections ([§6.4](06-cqrs.md), ADR-002). One set of credentials per such
> host, and the number is the point: it is the number of synchronous
> couplings, and it moves only by a decision that says what each new one reads
> when it is stolen.

"Supplying the rest "for consistency" is not harmless padding…" follows
unchanged.

The three rows:

```
| `Identity__Client__ClientId` | Config | Helm `identity.clientId` | ✓ **for a host that calls out under a grant of its own** — the BFF ([§9.7](09-messaging.md), [§11.5](11-identity-authorization.md)), and each worker [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md) gives a read |
| `Identity__Client__Scope` | Config | Helm `identity.scope` | ✓ **for a host that calls out under a grant of its own**, as above |
| `Identity__Client__ClientSecret` | Secret | `web-bff-identity`, `shipping-identity` and `notifications-identity`; one per host, never shared — two hosts on one grant is one host able to act as the other (§11.5) | ✓ **for a host that calls out under a grant of its own**, as above |
```

and two rows at the table's end, after whichever service rows earlier pull
requests left last:

```
| `ContactSource__BaseUrl` | Config | Helm `contactSource.baseUrl` → ConfigMap | ✓ — **Notifications only**; Keycloak's address for [ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)'s contact read, HTTPS outside Development, and the host refuses to start without it |
| `ContactSource__Realm` | Config | Helm `contactSource.realm` → ConfigMap | ✓ — **Notifications only**; the realm whose users are read, the one that issues this host's token, and the host refuses to start without it |
```

`contactSource` and `notifications-identity` are the names spec section 11
gives PR-6's chart, written here because §15.4's rule is that a key joins the
inventory when a host's code reads it, and this host's does from this PR. The
paragraph naming the options types that earned one does not move:
`ContactSource` is read as keys, not bound, and `ContactOptions` is a
registered value.

- [ ] **Step 7: Appendix B**

The Testcontainers row's use: "Integration test infrastructure. The Keycloak
module is [§11.5](11-identity-authorization.md)'s suite, and the only place the
solution runs an identity provider" becomes "Integration test infrastructure.
The Keycloak module is [§11.5](11-identity-authorization.md)'s suite in
`Web.Bff.Tests` and the contact read's in `Notifications.Worker.Tests`, the
only places the solution runs an identity provider".

`System.IdentityModel.Tokens.Jwt`'s use gains, after "…in production
([ADR-052](…))": "and Notifications' worker reads its own token's
`resource_access` claim with,"; and after "`Shipping.Worker.Tests` writes the
tokens it checks with": ", `Notifications.Worker.Tests` reads a token Keycloak
issued with". The licence gate matches identities, which do not move:

```bash
cd .github/licence-gate && py -3.12 -m unittest && py -3.12 licence_gate.py && cd ../..
```

Expected: exit 0.

- [ ] **Step 8: `docs/secrets.md`**

*Rotation*'s opening sentence, by its `Identity__Client__ClientSecret` clause
and not as a whole sentence — PR-2 appends `Mail__Password` to the same
sentence, and a whole-sentence replacement would drop it in one order. Before:

```markdown
`Identity__Client__ClientSecret`, for the
BFF and, since
[ADR-052](backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md),
for Shipping's worker — the two hosts that call a peer synchronously
([§9.7](backend-architecture/09-messaging.md),
[§11.5](backend-architecture/11-identity-authorization.md), ADR-017),
```

After:

```markdown
`Identity__Client__ClientSecret`, for each host §15.4's rows name — the BFF,
which calls a peer on a request path
([§9.7](backend-architecture/09-messaging.md), ADR-017), and every worker
[ADR-052](backend-architecture/adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
gives a read of its own
([§11.5](backend-architecture/11-identity-authorization.md)),
```

and rewrap the paragraph at 80 columns; every other clause stays as found.

*A client secret*'s step 2 names the vault entries per host: "…`web-bff-identity`
for the BFF, `shipping-identity` for Shipping's worker, `notifications-identity`
for Notifications' worker. Each chart names its own under
`identity.clientSecretRef`, and they are never one Secret." Step 4 gains, after
Shipping's clause: "; for Notifications' worker it is
`notifications.contact.refused` staying flat, the same separate count for the
same moment."

After "**Step 3 is the one that gets skipped**…", a new paragraph — the
provisioning note ADR-052 says each secret's grant rests on:

> **What each worker's client holds is a provisioning obligation, because no
> gate can read it.** A service account's roles are in neither document the
> realm gate reads (ADR-052), so a deployed realm is held to them here.
> `shipping-worker`'s service account holds `orders:delivery-address` on
> `commerce-api` and nothing else. `notifications-worker`'s holds `view-users`
> on `realm-management` — Keycloak composes `query-groups` and `query-users`
> into it — and nothing else, with `commerce-api` in neither of its scope
> lists and `roles` in one. Each worker refuses a token wider than its grant at
> its first read; a grant on some other client is outside that check, and this
> paragraph is what says it must not exist.

*Local development is a deliberate exception*'s table gains:

```
| Notifications worker client secret | `${NOTIFICATIONS_CLIENT_SECRET:-local-dev-notifications-secret}` |
```

The five places for the new keys, accounted for: Compose is Task 7's unit and
`.env.example`; §14.2's Aspire host is not adopted; Helm is PR-6's chart; the
inventory is Step 6; the fixture is Task 5's `NotificationsWorkerFactory`.

- [ ] **Step 9: The three build-side statements of one Keycloak**

Each says §11.5's suite is the only place a real Keycloak runs, which Task 6's
fixture makes false; each loses the claim rather than gaining a count.

`Directory.Packages.props`, the comment over `Testcontainers.Keycloak`'s pin,
seven lines rewritten whole to the comment gate's five. Before, its opening:
"§11.5's suite, and the ONE place in the solution that runs a real Keycloak."
After, the whole block:

```xml
    <!-- §11.5's suite and ADR-052's contact read, the places the solution runs
         a real Keycloak. The audience mapper the first proves is realm
         configuration, which no other test can see: §12.4's fixture points at
         an unreachable authority on purpose. Same version line as the three
         above: the Testcontainers modules ship as one release. -->
```

`tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, the comment over the reference.
Before: `<!-- §11.5's suite, the one that runs a real Keycloak. -->`. After:

```xml
    <!-- §11.5's suite, which proves the realm's grants against a real Keycloak. -->
```

`tests/Web.Bff.Tests/KeycloakFixture.cs`, the class summary. Before: "A real
Keycloak importing the shipped realm file, the only fixture that runs one
(§11.5)." After:

```csharp
/// <summary>A real Keycloak importing the shipped realm file, for §11.5's grants.</summary>
```

- [ ] **Step 10: Check and commit**

Run `/check-links`, then `/validate-blueprint` — last in the session's run,
after every `src/` commit, because the command's `Edit` deny on `src/` lasts
the turn it runs in.

```bash
git add docs Directory.Packages.props tests/Web.Bff.Tests/Web.Bff.Tests.csproj \
    tests/Web.Bff.Tests/KeycloakFixture.cs
git commit -m "docs: the chapters, Appendix B and docs/secrets.md name the contact reader beside the other credentialed hosts"
```

---

### Task 9: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet format Platform.slnx --verify-no-changes` — exit 0.
- [ ] `dotnet test Platform.slnx` — green, with a running Docker daemon. The
  container suites this PR touches are `Notifications.Worker.Tests`' SQL and
  Keycloak collections, `Web.Bff.Tests`' Keycloak collection (for the realm it
  imports) and `Common.Web.Tests`.
- [ ] The gates none of the above runs, each on its own:

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py check --kind local --realm deploy/compose/keycloak/realm-export.json
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
cd .github/licence-gate && py -3.12 -m unittest && py -3.12 licence_gate.py && cd ../..
git fetch origin main
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

`--base` is required and the gate judges `HEAD`, so it runs after the last
commit. Expected: every one exits 0 — the comment gate finding no block past
five lines among the ones this PR adds or touches, including `realm_check.py`'s
rewritten `WORKER_CLIENT` comment, `realm()`'s rewritten docstring and the
Compose unit's two blocks.

- [ ] PR body: `| Class | A+D+E |`, the touch set from the Global Constraints
  as paths alone with the reasons under the table; Task 7's two `curl` codes;
  whether Task 4 found PR-2's meter file and `AddMeter` line, and Task 5 and
  Task 6 its `Unreachable.cs` and `RepositoryRoot.cs`; whether Task 6's staging
  self-check passed first time; and one sentence that every deployed realm now
  needs `notifications-worker`, provisioned as `docs/secrets.md` says, before
  `deploy.yml` passes its realm check. Then `/ship`.

## Self-review

**Spec coverage.**

- Section 2's realm bullet — `notifications-worker`, confidential, a service
  account, `commerce-api` not among its scopes, `view-users` with its two
  composed query roles and nothing else → Task 2, verified against the pinned
  export (Task 2 Step 1) and a token Keycloak issued (Task 6).
- Section 3's PR-3 row — the realm's client, `realm_check.py`'s predicate,
  every asserted test ADR-052 names, `docs/secrets.md`'s rows (Tasks 2 and 8);
  `IContactSource` (Task 1), the Keycloak adapter with `ContactHop` (Tasks 4
  and 5), the grant-checked token cache over `realm-management`'s roles (Task
  4), `ContactRecords` and its migration (Task 3), `ContactOptions` (Task 1).
  Nothing calls the source.
- Section 6 — `ContactRecords` with `CustomerId` the key and `Email`,
  `Locale NULL`, `FetchedAt`, holding nothing else Keycloak offered (Task 3's
  column test); `AddContactRecords` by `dotnet ef migrations add` (Task 3); the
  erasure path named beside the table — the store's delete by the key (Task 1's
  port, Task 3's test).
- Section 9's contact port — `GET /admin/realms/{realm}/users/{id}`, a typed
  client with `ClientCredentialsHandler` inside its pipeline and `ContactHop`'s
  five numbers inside the bands (Tasks 4 and 5); `404`, `enabled: false` and
  no email to `NoSuchCustomer`; anything transient thrown; `401`/`403` or a
  token whose `realm-management` roles are not exactly the grant thrown as
  `ContactSourceRefusedException` and counted on `notifications.contact.refused`
  (Tasks 4–6); `email`, `enabled` and `attributes.locale` bound and nothing
  else (Task 5's adapter, Task 1's structural test); the locale bounded to a
  BCP 47 shape and dropped (Tasks 1 and 5). The dependency table's two Keycloak
  rows — unreachable and refused, for the contact and for the token — are the
  adapter's half; serving a row younger than the ceiling is PR-5's.
- Section 11 — `ContactSource__BaseUrl`, `ContactSource__Realm` and the three
  `Identity__Client__*` keys: read (Task 5), defaulted in Compose and the
  fixture (Tasks 5 and 7), inventoried (Task 8). The client secret's places:
  the realm, the rotation row, the exception row, §15.4's rows, Compose and the
  fixture (Tasks 2, 5, 7, 8); the chart's is PR-6's.
- Section 12 — `notifications.contact.refused` on `Notifications.Outbound`
  (Task 4); no mailbox in a log or an exception (Tasks 1, 4 and 5's
  assertions on `ToString` and on messages).
- Section 13 — the contact adapter against ADR-052's outcomes, a disabled user,
  a user with no email, an email carrying CR or LF, a locale that is not one,
  and a token Keycloak issued to `notifications-worker` accepted and one issued
  to a client without the grant refused (Task 6, with the stub suites of Task 5
  for what only a stub can stage); PR-3's realm half (Task 2).
- Section 14's table, the rows it gives PR-3: §2.2's edge (Task 8 Step 2, and
  both edges rather than the dashed one alone); §3.2's Notifications row, which
  lands in §3.1's paragraph (Step 2 says why); §11.5's table and counting
  sentences (Step 1); §14.1's Compose default and §14.2 (Step 5); §15.4's rows
  (Step 6); `RealmClientTests` and both `RealmImportTests` (Task 2, with the
  vocabulary test re-read and found green); `realm-export.json` (Task 2);
  `docs/secrets.md` (Step 8); `docs/repo-map.md` and `CLAUDE.md` — re-read, and
  **neither counts credentialed hosts today**, so neither moves.

**Places outside section 14's table this PR makes false, and takes.** §9.7's "a
host that holds client credentials is a host that calls a peer" and §4.1's
"Outbound identity belongs to the hosts that call a peer" (Step 3); §12.1's
outbound-hop row and §12.4's "the one suite in the solution that starts a real
Keycloak" (Step 4); §11.5's "the **one** suite that runs a real Keycloak" (Step
1); Appendix B's Testcontainers row, "the only place the solution runs an
identity provider", and the JWT row's use (Step 7); `ServiceIdentityOptions`'
summary, "bound by each host that calls a peer" (Task 5 Step 4);
`realm_check.py`'s `WORKER_CLIENT` comment, "Notifications' client is decided
and minted nowhere yet" (Task 2 Step 3); and `realm-export.json`'s `web-bff`
description, which counts two (Task 2 Step 2).

**Gates this PR turns red, and the task that turns each green.**

| Gate | Red because | Green in |
|---|---|---|
| `RealmImportTests.No_client_ships_a_secret_but_the_ones_whose_grants_need_one` | a third client ships a secret | Task 2, Step 1 |
| `RealmClientTests.The_service_account_clients_are_exactly_the_hosts_that_call_a_peer` | a third service-account client; renamed to `…_the_credentialed_hosts`, as this one calls no peer | Task 2, Step 1 |
| `deploy/keycloak/realm_check.py` | its new predicate requires exactly one `notifications-worker` | Task 2, Steps 2–3 |
| `.github/secret-scan` | the new local default in the realm, `RealmImportTests`, the Compose unit and the Keycloak fixture | Task 2 Step 6, Task 6 Step 5, Task 7 Step 4 |
| `MetricsRegistrationTests.Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be` | `ContactMetrics` registered and not forced | Task 5, Step 4 |
| `DatabaseSmokeTests.Migrator_exits_zero_and_creates_the_schema` | one more applied migration | Task 3, Step 4 |
| `ObservabilityTests.Every_meter_an_alert_reads_from_is_collected` | `Notifications.Outbound` required, unless PR-2 landed it | Task 4, Step 4 |
| Every host built over `NotificationsWorkerFactory` | `AddContactSource` and `ValidateOnStart` now require five keys | Task 5, Step 1 |

**Asserted rows that stay green, and why.**
`RealmImportTests.The_permission_vocabulary_is_a_closed_set_of_client_roles`:
the client holds no `commerce-api` role. `_helpers.tpl`'s `fail` naming
`web-bff` and `shipping`, and `smoke.sh`'s credentialed-chart assertions: they
read charts' `clientCredentials`, Notifications has no chart until PR-6, and
nothing here declares one — confirmed by reading
`deploy/helm/common/templates/_helpers.tpl`'s `has .Chart.Name (list "web-bff" "shipping")`
and `smoke.sh`'s `CREDENTIALED_CHARTS`, both PR-6's by spec section 14.

**Type consistency.** `IContactSource`, `ContactLookup.Found`/`NoSuchCustomer`,
`ContactSourceRefusedException`, `IContactStore`, `ContactRecord`,
`ContactLimits.MaxEmailLength`, `LanguageTag.IsOne`/`MaxLength` and
`ContactOptions` are Task 1's and consumed under those spellings by Tasks 3–6.
`ContactHop`, `OutboundMeter.Name`, `ContactMetrics.Refused` and
`GrantCheckedTokenCache` are Task 4's; `KeycloakContactSource`,
`ContactAnswerBuffer` and `AddContactSource` with `BaseUrlKey` and `RealmKey`
are Task 5's, as are the factory's `contactSourceBaseUrl`,
`UnreachableContactSource`, `LocalRealm`, `ContactScope`, `Tokens` and
`ConfigureTokens`; `ContactCounter`/`ContactCount` are Task 4's and read by
Tasks 5 and 6; `KeycloakFixture` with `ContactHost`, `Image`, `ContactClient`,
`ContactSecret`, `UngrantedClient` and `UngrantedSecret` is Task 6's and read by
Task 7's file tests. `MetricsInitialiser` leaves this PR with `ContactMetrics`
last, which PR-5 extends after it.

**Left to a later PR.**

- **Every caller.** The send worker's use of ADR-052's five outcomes over this
  source and `ContactOptions` — fresh row served with no call, stale row served
  up to the ceiling while the owner cannot answer, "does not exist" recorded
  terminal with the contact row deleted, a refusal backed off and logged — is
  PR-5's, as are the lease above `MailHop`'s total plus `ContactHop`'s, and the
  retention pass that deletes a contact row not refreshed for `ContactRetention`.
  **A start-up check that `ContactRetention` is at least `StaleCeiling` is owed
  by PR-4**, which binds `ContactRetention`: a row deleted before its ceiling
  makes the ceiling a number nothing reaches.
- **The CR/LF mailbox end to end** — a realm user's broken mailbox read here,
  stored, and refused by the mail channel as `not_a_mailbox` — is PR-5's, which
  is the first PR holding both ports' callers.
- **The chart's places**: `deploy/helm/notifications`, the `contactSource`
  capability, the client-credentials capability with `notifications-identity`,
  `_helpers.tpl`'s set, `smoke.sh`'s lists, §15.1's inventory rows and §15.3's
  callout naming the hosts a further `clientCredentials: true` must be argued
  against — PR-6's.
- **A host-run block for Notifications in `deploy/compose/README.md`**, if no
  earlier PR wrote one (Task 7 Step 3): owed by the PR that completes the
  host's required keys.
- **The latency runbook's slow-peer branch** gains the contact read's symptom —
  a notification waiting on its contact, not latency — with the waiting gauge
  PR-5 adds and the runbook half PR-6 owes.
- **§11.7's erasure consumer**, which runs the store's delete; owed with that
  extension, and this PR designs the table against it.

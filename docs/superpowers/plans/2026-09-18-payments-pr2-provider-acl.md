# Payments PR-2 — the PSP anti-corruption layer and its simulator — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Payments the port §3.2's anti-corruption layer stands behind,
an HTTP adapter that is the only code knowing the provider's wire format, and
a WireMock.Net simulator that Compose runs and the tests load in process from
the same mapping files. Nothing calls the port yet.

**Architecture:** `IPaymentProvider` in `Payments.Application` speaks the
domain's vocabulary: an authorisation is `Authorised(reference)` or
`Declined(reason)`, and a transient fault is an exception, never a result.
`HttpPaymentProvider` in `Payments.Infrastructure.Provider` is a typed
`HttpClient` behind `AddStandardResilienceHandler`, with a per-attempt counter
inside the pipeline. Every request carries an idempotency key, which is what
makes the in-client retry safe.

**Tech Stack:** `Microsoft.Extensions.Http.Resilience` (pinned, used by the
BFF), WireMock.Net (pinned, named by §12's table and Appendix B, referenced by
no project until this one), `System.Diagnostics.Metrics`.

**Spec:** `docs/superpowers/specs/2026-09-18-payments-service-design.md`,
sections 4, 9, 11 (the two provider keys) and 12 (the provider counter).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Payments/**`, `tests/Payments.*`,
  the two `*.csproj` package references (E: `Microsoft.Extensions.Http.Resilience`
  in `Payments.Infrastructure`, `WireMock.Net` in `Payments.Api.Tests`, no
  `Version=`), `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` and
  `tests/Common.Web.Tests/ObservabilityTests.cs` (B: one `AddMeter` line),
  `deploy/compose/**` (D: the simulator), `.github/secret-scan/allowed/**`
  (D: the entry for the simulator key the Compose unit sets, which the scan
  flags — this plan's own text drew the same finding), and
  `docs/backend-architecture/15-cicd-deployment.md` (§15.4's two rows,
  inside D's `docs/**`); `Common.Web` and its test sit in A's
  `src/BuildingBlocks/**` and `tests/**`.
- **Three classes, which the locality gate does not yet admit.** A service's
  arrival spans its code (A), its projects (E) and its deployment or harness
  tree (D); `docs/change-locality.md` names at most two and
  `.github/locality-gate` refuses a third letter. This PR cannot merge until
  the contract and the gate admit that case — a Class D change of its own,
  owed before Payments' PR-1, and met first by Inventory's plans, which
  declare the same shape.
- Depends on PR-1 having merged.
- No `Directory.Packages.props` change and no Appendix B row: both packages
  are pinned and listed already.
- The simulator's image tag equals `Directory.Packages.props`'s `WireMock.Net`
  pin, so the container and the in-process server are one engine at one
  version.
- The wire format appears in `HttpPaymentProvider` and the mapping files and
  nowhere else. No `Payments.Application` type names HTTP.
- Comments say why and cite the owner. Explicit local types, file-scoped
  namespaces, 120 columns. `py -3.12`.
- Every step that adds behaviour writes its test first.

---

### Task 1: The port and its vocabulary

**Files:**
- Create: `src/Services/Payments/Payments.Application/Provider/IPaymentProvider.cs`
- Create: `src/Services/Payments/Payments.Application/Provider/AuthorisationRequest.cs`
- Create: `src/Services/Payments/Payments.Application/Provider/AuthorisationResult.cs`
- Create: `src/Services/Payments/Payments.Application/Provider/VoidRequest.cs`
- Create: `src/Services/Payments/Payments.Application/Provider/PaymentProviderUnavailableException.cs`
- Create: `src/Services/Payments/Payments.Application/PaymentMismatchException.cs`
- Create: `src/Services/Payments/Payments.Application/Provider/ProviderLimits.cs`
- Test: `tests/Payments.Application.Tests/AuthorisationResultTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Payments.Application.Provider;

public sealed record AuthorisationRequest(OrderId OrderId, Guid PayerId, decimal Amount, string Currency)
{
    public string IdempotencyKey => $"authorise:{OrderId.Value}";
}

public sealed record VoidRequest(OrderId OrderId, string Reference)
{
    public string IdempotencyKey => $"void:{OrderId.Value}";
}

public abstract record AuthorisationResult
{
    public sealed record Authorised(string Reference) : AuthorisationResult;
    public sealed record Declined(string Reason) : AuthorisationResult;
}

public interface IPaymentProvider
{
    Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct);
    Task VoidAsync(VoidRequest request, CancellationToken ct);
}

public sealed class PaymentProviderUnavailableException : Exception { /* the three standard constructors */ }

/// What a verdict may carry and still be recorded. The reference is confirmed
/// onto the order, so Ordering's PaymentReference.MaxLength is the bound it
/// must fit; the adapter refuses a longer answer and the columns are this wide.
public static class ProviderLimits
{
    public const int MaxReferenceLength = 100;
    public const int MaxReasonLength = 100;
}

namespace Payments.Application;

public sealed class PaymentMismatchException : Exception { /* the three standard constructors */ }
```

- [ ] **Step 1: Write the failing test**

```csharp
using Payments.Application.Provider;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class AuthorisationResultTests
{
    [Fact]
    public void The_keys_are_the_order_and_differ_between_the_two_acts()
    {
        OrderId order = OrderId.New();

        string authorise = new AuthorisationRequest(order, Guid.CreateVersion7(), 1m, "EUR").IdempotencyKey;
        string @void = new VoidRequest(order, "psp_x").IdempotencyKey;

        authorise.ShouldBe($"authorise:{order.Value}");
        @void.ShouldBe($"void:{order.Value}");
        authorise.ShouldNotBe(@void, "a void replayed under the authorisation's key would return the authorisation");
    }

    [Fact]
    public void The_two_results_are_the_only_two()
    {
        typeof(AuthorisationResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(AuthorisationResult)))
            .Select(t => t.Name)
            .ShouldBe(["Authorised", "Declined"], ignoreOrder: true,
                "a transient fault is an exception, so it can never reach the saga as a decline");
    }
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Payments.Application.Tests --filter AuthorisationResultTests`
Expected: compile failure.

- [ ] **Step 3: Write the types**

`IPaymentProvider.cs`:

```csharp
namespace Payments.Application.Provider;

/// <summary>
/// §3.2's anti-corruption layer: the provider in this service's vocabulary.
/// </summary>
/// <remarks>
/// A transient fault throws <see cref="PaymentProviderUnavailableException"/>
/// and is never an <see cref="AuthorisationResult"/>, so "the provider is down"
/// cannot reach the saga as a decline. Every call carries its request's
/// idempotency key, which is what lets the caller's unit of work be retried
/// whole after the provider has answered.
/// </remarks>
public interface IPaymentProvider
{
    Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct);

    Task VoidAsync(VoidRequest request, CancellationToken ct);
}
```

`AuthorisationResult.cs`, `AuthorisationRequest.cs`, `VoidRequest.cs` as in
Interfaces. `AuthorisationRequest`'s summary: "The payer is the order record's
`CustomerId`, never a field the command carried (ADR-028)." Both exceptions
take the three standard constructors, as `ContractMappingException` does.
`PaymentMismatchException`'s summary: "The command's figures, or a replayed
key's, disagree with what Payments holds. A fault, not a verdict (spec,
section 1): excluded from retry, so it reaches the error queue §13.6 pages on."

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Payments.Application.Tests
git add src/Services/Payments/Payments.Application tests/Payments.Application.Tests
git commit -m "feat(payments): the provider port, in the domain's vocabulary"
```

---

### Task 2: The simulator's mappings

**Files:**
- Create: `deploy/compose/psp-simulator/mappings/authorise-card-declined.json`
- Create: `deploy/compose/psp-simulator/mappings/authorise-insufficient-funds.json`
- Create: `deploy/compose/psp-simulator/mappings/authorise-unavailable.json`
- Create: `deploy/compose/psp-simulator/mappings/authorise-stalled.json`
- Create: `deploy/compose/psp-simulator/mappings/authorise-approved.json`
- Create: `deploy/compose/psp-simulator/mappings/void.json`
- Create: `deploy/compose/psp-simulator/README.md`

The wire format these define, and the adapter in Task 3 speaks:

| | |
|---|---|
| Authorise | `POST /v1/authorisations`, header `Idempotency-Key`, body `{"amountMinor":4210,"currency":"EUR","payerId":"…"}` |
| Approved | `201 {"status":"approved","reference":"psp_<key>"}` |
| Declined | `402 {"status":"declined","code":"card_declined"}` |
| Void | `POST /v1/authorisations/{reference}/void`, header `Idempotency-Key` → `200 {"status":"voided"}` |

- [ ] **Step 1: Write the six mappings**

The scripted four match on the body's `amountMinor` ending, at `Priority` 1;
the approval matches every authorisation at `Priority` 10, so it answers only
what the four did not. The body regex relies on `System.Text.Json`'s compact
output, which is what the adapter sends.

`authorise-card-declined.json`:

```json
{
  "Guid": "5b7e3f0a-0001-4000-8000-000000000001",
  "Title": "Minor units ending 01 decline as card_declined",
  "Priority": 1,
  "Request": {
    "Path": { "Matchers": [{ "Name": "ExactMatcher", "Pattern": "/v1/authorisations" }] },
    "Methods": ["POST"],
    "Body": { "Matcher": { "Name": "RegexMatcher", "Pattern": "\"amountMinor\":(\\d*0)?1[,}]" } }
  },
  "Response": {
    "StatusCode": 402,
    "Headers": { "Content-Type": "application/json" },
    "BodyAsJson": { "status": "declined", "code": "card_declined" }
  }
}
```

The optional `(\d*0)?` is what makes a total of 0.01 — `amountMinor` 1, a
single digit with no leading zero — decline as the spec's `.01` row says,
while 11 and 21 still do not match.

`authorise-insufficient-funds.json`: the same with Guid `…0002`, the pattern
ending `(\d*0)?2[,}]` and `code` `insufficient_funds`.

`authorise-unavailable.json`: Guid `…0005`, pattern ending `(\d*0)?5[,}]`,
`StatusCode` 503, `BodyAsJson` `{ "status": "unavailable" }`.

`authorise-stalled.json`: Guid `…0009`, pattern ending `(\d*0)?9[,}]`,
`StatusCode` 201, `"Delay": 30000`, and the approval's templated body below —
the answer would be an approval, and the adapter gives up before it arrives.

`authorise-approved.json`:

```json
{
  "Guid": "5b7e3f0a-0010-4000-8000-000000000010",
  "Title": "Everything else is approved; the reference is the key, so a replay answers the same",
  "Priority": 10,
  "Request": {
    "Path": { "Matchers": [{ "Name": "ExactMatcher", "Pattern": "/v1/authorisations" }] },
    "Methods": ["POST"]
  },
  "Response": {
    "StatusCode": 201,
    "Headers": { "Content-Type": "application/json" },
    "Body": "{\"status\":\"approved\",\"reference\":\"psp_{{request.headers.Idempotency-Key}}\"}",
    "UseTransformer": true
  }
}
```

`void.json`: Guid `…0020`, `Priority` 10, `WildcardMatcher` on
`/v1/authorisations/*/void`, `POST`, `200 {"status":"voided"}`.

- [ ] **Step 2: Write the README**

Three short sections: what this is (§3.2's provider, simulated; spec section
9), the table of scripted amounts from spec section 9 copied as the one place
a person at the keyboard reads them, and how to watch each: "order something
whose total ends in .05 and watch Payments retry, then the error queue".
Nothing about history.

- [ ] **Step 3: Commit**

```bash
git add deploy/compose/psp-simulator
git commit -m "feat(payments): the provider simulator's mappings"
```

The mappings are proved by Task 3's tests, which load this directory.

---

### Task 3: The adapter, its resilience and its counter

**Files:**
- Modify: `src/Services/Payments/Payments.Infrastructure/Payments.Infrastructure.csproj`
  — `<PackageReference Include="Microsoft.Extensions.Http.Resilience" />`, and
  `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />`
  for the `IHostEnvironment` the registration now takes, as
  `Common.Infrastructure` references it
- Create: `src/Services/Payments/Payments.Infrastructure/Provider/ProviderHop.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Provider/ProviderOptions.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Provider/HttpPaymentProvider.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Provider/ProviderAttemptCounter.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Provider/ProviderMetrics.cs`
- Create: `src/Services/Payments/Payments.Infrastructure/Provider/DependencyInjection.cs`
- Modify: `src/Services/Payments/Payments.Api/Program.cs`
  (`builder.Services.AddPaymentProvider(builder.Configuration, builder.Environment);`
  — in the composition root, because the scheme rule needs the environment,
  which `AddPaymentsInfrastructure` is not given)
- Modify: `tests/Payments.Api.Tests/Payments.Api.Tests.csproj` —
  `<PackageReference Include="WireMock.Net" />`
- Modify: `tests/Payments.TestSupport/PaymentsApiFactory.cs` — a third
  parameter, `string providerBaseUrl = UnreachableProvider`, set as
  `PaymentProvider:BaseUrl`, and a fourth, `string? providerApiKey = null`,
  set as `PaymentProvider:ApiKey` — the Compose unit's local default when
  null, so every existing caller is unchanged — and `public const string UnreachableProvider =
  "http://psp.invalid/"`
- Create: `tests/Payments.TestSupport/SimulatorMappings.cs`
- Test: `tests/Payments.Api.Tests/HttpPaymentProviderTests.cs`

**Interfaces:**
- Consumes: Task 1's port.
- Produces: `ProviderHop.AttemptTimeout` (5 s), `ProviderHop.MaxRetryAttempts`
  (2), `ProviderHop.RetryDelay` (500 ms), `ProviderHop.TotalRequestTimeout`
  (20 s); `ProviderMetrics` with meter `Payments.Provider` and
  `Counter<long> payments.provider.unavailable`;
  `IServiceCollection AddPaymentProvider(IConfiguration)`;
  `SimulatorMappings.Directory()`; the factory's third parameter.

- [ ] **Step 1: Write the failing adapter tests**

`SimulatorMappings.cs` walks up from `AppContext.BaseDirectory` to the
directory holding `Platform.slnx` and returns
`deploy/compose/psp-simulator/mappings` beneath it, throwing a message naming
the path when it is absent — the fixture's `BrokerContextPath()` is the same
walk for the broker's files; share its shape.

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Provider;
using Payments.Domain.Orders;
using Payments.TestSupport;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>
/// The adapter over a real HTTP server loading the simulator's own mappings,
/// so the file Compose runs is the file these assert (§12: WireMock.Net for a
/// third-party API).
/// </summary>
public sealed class HttpPaymentProviderTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly PaymentsApiFactory _factory;

    public HttpPaymentProviderTests()
    {
        _server = WireMockServer.Start();
        _server.ReadStaticMappings(SimulatorMappings.Directory());
        _factory = new PaymentsApiFactory(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672",
            _server.Urls[0] + "/");
    }

    public void Dispose()
    {
        _factory.Dispose();
        _server.Stop();
    }

    private IPaymentProvider Provider() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IPaymentProvider>();

    private static AuthorisationRequest Authorisation(decimal amount, OrderId? order = null) =>
        new(order ?? OrderId.New(), Guid.CreateVersion7(), amount, "EUR");

    private int Calls(string path) =>
        _server.LogEntries.Count(e => e.RequestMessage.Path == path);

    [Fact]
    public async Task An_ordinary_amount_is_authorised_and_a_replay_of_the_key_answers_the_same_reference()
    {
        OrderId order = OrderId.New();

        AuthorisationResult first = await Provider().AuthoriseAsync(Authorisation(42.10m, order), TestContext.Current.CancellationToken);
        AuthorisationResult second = await Provider().AuthoriseAsync(Authorisation(42.10m, order), TestContext.Current.CancellationToken);

        AuthorisationResult.Authorised authorised = first.ShouldBeOfType<AuthorisationResult.Authorised>();
        authorised.Reference.ShouldBe($"psp_authorise:{order.Value}");
        second.ShouldBe(first, "section 4: a unit retried after the provider answered receives the same answer");
        _server.LogEntries.ShouldAllBe(e => e.RequestMessage.Headers!["Idempotency-Key"].Single() == $"authorise:{order.Value}");

        // The simulator ignores the credential, so only this line fails if the
        // adapter stops sending it: compared with what the host configured, so
        // the test prints no key of its own.
        string configured = _factory.Services.GetRequiredService<IConfiguration>()[DependencyInjection.ApiKeyKey]!;
        _server.LogEntries.ShouldAllBe(e => e.RequestMessage.Headers!["Authorization"].Single() == $"Bearer {configured}");
    }

    [Theory]
    [InlineData(10.01, "card_declined")]
    [InlineData(0.01, "card_declined")]
    [InlineData(10.02, "insufficient_funds")]
    [InlineData(0.02, "insufficient_funds")]
    public async Task A_scripted_decline_is_a_decline_with_the_providers_code(decimal amount, string code)
    {
        AuthorisationResult result = await Provider().AuthoriseAsync(Authorisation(amount), TestContext.Current.CancellationToken);

        result.ShouldBe(new AuthorisationResult.Declined(code));
        Calls("/v1/authorisations").ShouldBe(1, "a decline is an answer, and the pipeline does not retry a 402");
    }

    [Fact]
    public async Task A_503_is_retried_in_the_client_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        long counted = 0;
        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Payments.Provider" && instrument.Name == "payments.provider.unavailable")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(10.05m), TestContext.Current.CancellationToken));

        Calls("/v1/authorisations").ShouldBe(ProviderHop.MaxRetryAttempts + 1);
        Interlocked.Read(ref counted).ShouldBe(ProviderHop.MaxRetryAttempts + 1, "one per failing attempt, not one per call");
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    public async Task A_timeout_or_throttle_status_is_retried_then_thrown_as_unavailable(int status)
    {
        // Stubbed rather than scripted: the translation table names both, and
        // the simulator scripts neither, so without this a branch that dropped
        // either would leave the suite green.
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status));

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));

        Calls("/v1/authorisations").ShouldBe(ProviderHop.MaxRetryAttempts + 1, "the pipeline retries both, as it does a 503");
    }

    [Theory]
    [InlineData(0.11)]
    [InlineData(0.21)]
    public async Task An_amount_ending_in_one_that_is_not_one_cent_is_approved(decimal amount)
    {
        (await Provider().AuthoriseAsync(Authorisation(amount), TestContext.Current.CancellationToken))
            .ShouldBeOfType<AuthorisationResult.Authorised>("only a minor amount ending 01, or exactly 1, is scripted");
    }

    [Fact]
    public async Task A_stalled_provider_is_unavailable_within_the_total_budget_and_its_timeouts_count()
    {
        long counted = 0;
        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Payments.Provider" && instrument.Name == "payments.provider.unavailable")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();
        DateTimeOffset started = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(10.09m), TestContext.Current.CancellationToken));

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(ProviderHop.TotalRequestTimeout + TimeSpan.FromSeconds(2));
        Interlocked.Read(ref counted).ShouldBeGreaterThanOrEqualTo(1, "an attempt timeout is the provider's, counted by OnTimeout");
    }

    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = ProviderHop.AttemptTimeout * (ProviderHop.MaxRetryAttempts + 1)
                         + ProviderHop.MaxRetryDelay * ProviderHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(ProviderHop.TotalRequestTimeout,
            "the Web.Bff's PricingHop argument: a total that cancels the last retry makes the retry count a fiction");
    }

    [Fact]
    public async Task A_409_is_a_mismatch_and_is_not_retried()
    {
        // Stubbed here, not in the mappings: a stateless simulator cannot know a
        // key was used before (spec, section 9).
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(409));

        await Should.ThrowAsync<PaymentMismatchException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));
        Calls("/v1/authorisations").ShouldBe(1);
    }

    [Theory]
    [InlineData(202, "{\"status\":\"voided\"}")]
    [InlineData(204, "")]
    [InlineData(200, "{\"status\":\"pending\"}")]
    [InlineData(200, "not json")]
    public async Task A_void_answered_with_anything_but_200_voided_is_not_a_void(int status, string body)
    {
        _server.Given(Request.Create().WithPath("/v1/authorisations/*/void").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().VoidAsync(new VoidRequest(OrderId.New(), "psp_ref"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_void_goes_to_the_references_path_under_the_void_key()
    {
        OrderId order = OrderId.New();

        await Provider().VoidAsync(new VoidRequest(order, "psp_ref"), TestContext.Current.CancellationToken);

        ILogEntry call = _server.LogEntries.ShouldHaveSingleItem();
        call.RequestMessage.Path.ShouldBe("/v1/authorisations/psp_ref/void");
        call.RequestMessage.Headers!["Idempotency-Key"].Single().ShouldBe($"void:{order.Value}");
    }

    [Fact]
    public async Task A_base_url_with_a_path_and_no_trailing_slash_keeps_its_path()
    {
        using PaymentsApiFactory factory = new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672",
            _server.Urls[0] + "/psp");
        _server.Given(Request.Create().WithPath("/psp/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201).WithBody("{\"status\":\"approved\",\"reference\":\"psp_p\"}"));

        AuthorisationResult result = await factory.Services.CreateScope().ServiceProvider
            .GetRequiredService<IPaymentProvider>()
            .AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken);

        result.ShouldBe(new AuthorisationResult.Authorised("psp_p"));
    }

    [Theory]
    [InlineData("http://psp.example/", false)]
    [InlineData("https://psp.example/", true)]
    public void Outside_development_only_an_https_provider_is_accepted(string address, bool starts)
    {
        using PaymentsApiFactory factory = new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672",
            address);
        using WebApplicationFactory<Program> production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        if (starts)
            production.Services.GetRequiredService<IPaymentProvider>().ShouldNotBeNull();
        else
            Should.Throw<InvalidOperationException>(() => production.Services)
                .Message.ShouldContain("plain HTTP outside Development");
    }

    [Fact]
    public void A_missing_base_url_stops_the_host()
    {
        using PaymentsApiFactory factory = new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672",
            providerBaseUrl: "");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain("PaymentProvider:BaseUrl");
    }

    [Fact]
    public void A_missing_provider_key_stops_the_host()
    {
        using PaymentsApiFactory factory = new(
            "Server=sql.invalid;Database=Payments;User Id=x;Password=x;TrustServerCertificate=true",
            "amqp://payments-svc:x@rabbit.invalid:5672",
            _server.Urls[0] + "/",
            providerApiKey: " ");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain("PaymentProvider:ApiKey", Case.Sensitive,
                "§15.4 marks the key required; a host must not call a provider unauthenticated");
    }

    [Theory]
    [InlineData(201, "{\"status\":\"declined\",\"reference\":\"psp_x\"}")]
    [InlineData(201, "{\"status\":\"approved\"}")]
    [InlineData(402, "{\"status\":\"declined\"}")]
    [InlineData(402, "{\"status\":\"approved\",\"code\":\"card_declined\"}")]
    [InlineData(201, "not json")]
    [InlineData(201, "{\"status\":\"approved\",\"reference\":\"   \"}")]
    [InlineData(402, "{\"status\":\"declined\",\"code\":\" \"}")]
    public async Task A_body_that_contradicts_its_status_is_unavailable_never_a_verdict(int status, string body)
    {
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        await Should.ThrowAsync<PaymentProviderUnavailableException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(ProviderLimits.MaxReferenceLength, true)]
    [InlineData(ProviderLimits.MaxReferenceLength + 1, false)]
    public async Task A_reference_longer_than_the_column_is_refused_before_it_is_recorded(int length, bool accepted)
    {
        string reference = new('r', length);
        _server.Given(Request.Create().WithPath("/v1/authorisations").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201)
                .WithBody($"{{\"status\":\"approved\",\"reference\":\"{reference}\"}}"));

        Func<Task<AuthorisationResult>> call = () =>
            Provider().AuthoriseAsync(Authorisation(42.10m), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new AuthorisationResult.Authorised(reference));
        else
            await Should.ThrowAsync<PaymentProviderUnavailableException>(call);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_counted_against_the_provider()
    {
        long counted = 0;
        using MeterListener listener = new()
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "Payments.Provider" && instrument.Name == "payments.provider.unavailable")
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref counted, value));
        listener.Start();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Provider().AuthoriseAsync(Authorisation(42.10m), cancelled.Token));

        Interlocked.Read(ref counted).ShouldBe(0, "a consume cancelled at shutdown is not a provider incident");
    }
}
```

The helper is `Authorisation(...)` rather than `Request(...)`: a method of
that name would shadow WireMock's `Request.Create()` builder in the 409 test.
`DependencyInjection` is `Payments.Infrastructure.Provider`'s, and
`IConfiguration` needs `Microsoft.Extensions.Configuration`.
The environment test needs `Microsoft.AspNetCore.Mvc.Testing` for
`WebApplicationFactory<Program>` and `Microsoft.AspNetCore.Hosting` for
`UseEnvironment`; the scaffolded test project already references the first.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Payments.Api.Tests --filter HttpPaymentProviderTests`
Expected: compile failure on `ProviderHop` and the factory's third parameter.

- [ ] **Step 3: Write the adapter**

`ProviderHop.cs`:

```csharp
namespace Payments.Infrastructure.Provider;

/// <summary>
/// The provider call's budget. Every retry inside it is safe only because each
/// request carries its idempotency key (spec, section 4); the endpoint's policy
/// (§9.8) owns every retry after it, and the worst case of both stays inside
/// the saga's payment wait (§9.6).
/// </summary>
internal static class ProviderHop
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 2;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The cap on one jittered delay. With jitter on, <see cref="RetryDelay"/> is
    /// a nominal and not a bound; this is what makes the budget arithmetic.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(20);
}
```

`ProviderHop` is `internal`; the test project reaches it because the scaffold
declares `InternalsVisibleTo` for `Payments.Api.Tests` in
`Payments.Infrastructure.csproj`. If it does not, add that one item.

`ProviderOptions.cs`: `internal sealed record ProviderOptions(Uri BaseUrl,
string ApiKey)`.

`ProviderMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace Payments.Infrastructure.Provider;

/// <summary>
/// One attempt that met a failing provider. A fact about the provider rather
/// than an order, so §13.3's claim rule does not reach it: a unit that rolls
/// back still met a failing provider.
/// </summary>
public sealed class ProviderMetrics
{
    public const string MeterName = "Payments.Provider";

    private readonly Counter<long> _unavailable;

    public ProviderMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(MeterName);
        _unavailable = meter.CreateCounter<long>(
            "payments.provider.unavailable",
            unit: "{attempt}",
            description: "Provider attempts that ended in a transient fault; the error queue sees only exhausted units.");
    }

    public void Unavailable() => _unavailable.Add(1);
}
```

`ProviderAttemptCounter.cs` — a `DelegatingHandler` inside the resilience
pipeline, so it sees each attempt:

```csharp
using System.Net;

namespace Payments.Infrastructure.Provider;

internal sealed class ProviderAttemptCounter(ProviderMetrics metrics) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            HttpResponseMessage response = await base.SendAsync(request, ct);

            if (IsTransient(response.StatusCode))
                metrics.Unavailable();

            return response;
        }
        catch (HttpRequestException)
        {
            // A refused or broken connection is the provider's. A cancelled
            // attempt is not counted here: an attempt timeout and the caller's
            // own cancellation arrive as the same exception, so timeouts are
            // counted where only they arrive, the pipeline's OnTimeout.
            metrics.Unavailable();
            throw;
        }
    }

    internal static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
```

`HttpPaymentProvider.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Payments.Application;
using Payments.Application.Provider;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Payments.Infrastructure.Provider;

/// <summary>
/// The one place that knows the provider's wire format (§3.2's anti-corruption
/// layer). Everything it returns is the port's vocabulary.
/// </summary>
internal sealed class HttpPaymentProvider(HttpClient http) : IPaymentProvider
{
    private const string KeyHeader = "Idempotency-Key";

    private sealed record AuthoriseBody(long AmountMinor, string Currency, Guid PayerId);

    private sealed record AuthoriseAnswer(string Status, string? Reference, string? Code);

    private sealed record VoidAnswer(string Status);

    public async Task<AuthorisationResult> AuthoriseAsync(AuthorisationRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(HttpMethod.Post, "v1/authorisations")
        {
            Content = JsonContent.Create(new AuthoriseBody(ToMinor(request.Amount), request.Currency, request.PayerId))
        };
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // Only the two answers the wire format defines carry a body worth
        // reading; anything else is a provider this adapter does not
        // understand, which is a fault rather than a verdict.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.PaymentRequired))
        {
            throw new PaymentProviderUnavailableException(
                $"The provider answered an authorisation with {(int)response.StatusCode}.");
        }

        AuthoriseAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<AuthoriseAnswer>(ct);
        }
        catch (JsonException e)
        {
            throw new PaymentProviderUnavailableException("The provider answered an authorisation with no JSON body.", e);
        }

        // The body must agree with its status, and each verdict must carry what
        // it is a verdict about. A contradiction is a provider this adapter does
        // not understand — a fault, never an authorisation or a decline.
        // Longer than ProviderLimits is refused here rather than at the insert:
        // a verdict that cannot be recorded would leave money authorised with
        // no PaymentAuthorised committed for it.
        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return answer is { Status: "declined", Code: { } code } && Recordable(code, ProviderLimits.MaxReasonLength)
                ? new AuthorisationResult.Declined(code)
                : throw new PaymentProviderUnavailableException("The provider declined with a body that is not a decline.");
        }

        return answer is { Status: "approved", Reference: { } reference } && Recordable(reference, ProviderLimits.MaxReferenceLength)
            ? new AuthorisationResult.Authorised(reference)
            : throw new PaymentProviderUnavailableException("The provider approved with a body that is not an approval.");
    }

    // What PaymentIntent's factories accept: blank is refused there, after the
    // money has moved, so it is refused here before a verdict exists.
    private static bool Recordable(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    public async Task VoidAsync(VoidRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post, $"v1/authorisations/{Uri.EscapeDataString(request.Reference)}/void");
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // 200 with "voided" and nothing else: the wire format defines that pair
        // as the void having happened. A 202 is a void still pending, and a 200
        // saying anything else is a provider this adapter does not understand;
        // either would record a Refund and PaymentRefunded before money moved.
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new PaymentProviderUnavailableException(
                $"The provider answered a void with {(int)response.StatusCode}.");
        }

        VoidAnswer? answer;
        try
        {
            answer = await response.Content.ReadFromJsonAsync<VoidAnswer>(ct);
        }
        catch (JsonException e)
        {
            throw new PaymentProviderUnavailableException("The provider answered a void with no JSON body.", e);
        }

        if (answer is not { Status: "voided" })
            throw new PaymentProviderUnavailableException("The provider answered a void with a body that is not a void.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutRejectedException or BrokenCircuitException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new PaymentProviderUnavailableException("The provider did not answer within the budget.", e);
        }

        // A 409 is the provider refusing a key reused with different figures:
        // the same key and different money is a defect, and no retry fixes it.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            response.Dispose();
            throw new PaymentMismatchException("The provider refused a reused idempotency key with different figures.");
        }

        if (ProviderAttemptCounter.IsTransient(response.StatusCode))
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            throw new PaymentProviderUnavailableException($"The provider answered {(int)status} after every retry.");
        }

        return response;
    }

    // The factor PaymentAmounts.MinorUnitPlaces implies, derived rather than
    // written, so the mapper's refusal and this conversion cannot disagree
    // about how many places a payment has.
    private static readonly decimal MinorUnitFactor =
        Enumerable.Repeat(10m, PaymentAmounts.MinorUnitPlaces).Aggregate(1m, (factor, ten) => factor * ten);

    // A figure with more places than minor units is not a payment this
    // platform can state.
    private static long ToMinor(decimal amount)
    {
        decimal minor = amount * MinorUnitFactor;

        if (minor != decimal.Truncate(minor))
            throw new PaymentMismatchException($"An amount of {amount} has more precision than minor units.");

        return decimal.ToInt64(minor);
    }
}
```

`Provider/DependencyInjection.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Payments.Application.Provider;
using Polly;

namespace Payments.Infrastructure.Provider;

public static class DependencyInjection
{
    // The section is written once, so the two setting names cannot name
    // different sections; ApiKeyKey is the setting's name, never its value.
    private const string Section = "PaymentProvider";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string ApiKeyKey = $"{Section}:ApiKey";

    public static IServiceCollection AddPaymentProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Eager, as the broker's key is: a host that cannot name its provider
        // does not start, rather than failing its first authorisation. An empty
        // value is the chart's default, so a deploy that forgot it stops here.
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{BaseUrlKey} is not configured. Payments cannot reach a provider.");

        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is '{configured}', which is not an absolute HTTP(S) address.");
        }

        // HTTPS everywhere but Development, the rule AuthenticationExtensions
        // applies to the identity provider: the key below is a bearer
        // credential, and plain HTTP hands it to anyone on the path. The local
        // simulator is Development's, and the one plain-HTTP provider there is.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} is '{configured}', which is plain HTTP outside Development; the provider key would travel in the clear.");
        }

        // A trailing slash, always: without one a relative request replaces
        // the base address's last segment, so a provider at …/api would be
        // called at …/v1/authorisations.
        Uri baseAddress = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");

        // Required for the same reason, and §15.4 says so: a host must not
        // start and then call a provider unauthenticated.
        string? apiKey = configuration[ApiKeyKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"{ApiKeyKey} is not configured. Payments does not call a provider unauthenticated.");

        services.AddSingleton<ProviderMetrics>();
        services.AddTransient<ProviderAttemptCounter>();

        IHttpClientBuilder client = services.AddHttpClient<IPaymentProvider, HttpPaymentProvider>(http =>
        {
            http.BaseAddress = baseAddress;
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        });

        // Separate statements: AddStandardResilienceHandler returns the
        // pipeline's builder, not the client's, so a chained
        // AddHttpMessageHandler would not compile onto the client. Added after
        // the pipeline, the counter is inside it and sees every attempt.
        client.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
        {
            options.TotalRequestTimeout.Timeout = ProviderHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = ProviderHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = ProviderHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = ProviderHop.RetryDelay;
            options.Retry.MaxDelay = ProviderHop.MaxRetryDelay;

            // The circuit breaker's sampling window must be at least twice the
            // attempt timeout, which the library validates at startup.
            options.CircuitBreaker.SamplingDuration = ProviderHop.AttemptTimeout * 2;

            // An attempt timeout is the provider's, and this is the one place
            // it arrives distinguishable from the caller cancelling.
            ProviderMetrics metrics = sp.GetRequiredService<ProviderMetrics>();
            options.AttemptTimeout.OnTimeout = _ =>
            {
                metrics.Unavailable();
                return ValueTask.CompletedTask;
            };
        });
        client.AddHttpMessageHandler<ProviderAttemptCounter>();

        return services;
    }
}
```

The standard handler retries `POST` by default; that is what the keys allow,
and it is argued in `ProviderHop`'s summary rather than switched off.

In `Payments.Api/Program.cs`, beside the infrastructure registration:
`builder.Services.AddPaymentProvider(builder.Configuration, builder.Environment);`
with the comment "§3.2's anti-corruption layer; its address is read, and its
scheme checked, eagerly." `WebApplicationFactory` runs the host as
Development by default, which is what lets every test reach the in-process
server over plain HTTP.

`HostSmokeTests` and every other rendered test that builds the factory keep
compiling because the third parameter defaults to `UnreachableProvider`.

- [ ] **Step 4: Run the adapter tests and the suite**

Run: `dotnet test tests/Payments.Api.Tests`
Expected: green. The stalled test takes about twenty seconds by design.

- [ ] **Step 5: Commit**

```bash
git add src/Services/Payments tests/Payments.*
git commit -m "feat(payments): the provider adapter, its resilience budget and a per-attempt counter"
```

---

### Task 4: The meter's export, the simulator in Compose, and §15.4

**Files:**
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` —
  `.AddMeter("Payments.Provider")  // spec §12; the error queue sees only exhausted units`
  beside Ordering's two, in the service-prefixed group
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` — the name joins the
  asserted list; write this first and see it fail
- Modify: `deploy/compose/services/payments.yml` — the `psp-simulator`
  service, and `PaymentProvider__BaseUrl` / `PaymentProvider__ApiKey` on the
  API with `depends_on: psp-simulator: { condition: service_started }`
- Modify: `deploy/compose/README.md` — the ports table gains the simulator's
  row at 5190
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's table
  gains `PaymentProvider__BaseUrl` (plain configuration; the provider's
  address) and `PaymentProvider__ApiKey` (Secret, External Secrets)
- Modify: `.github/secret-scan/allowed/deploy.txt` — the entry for the local
  key in `deploy/compose/services/payments.yml`, which the scan flags

- [ ] **Step 1: The meter**

Add the name to `ObservabilityTests`' list, run
`dotnet test tests/Common.Web.Tests --filter ObservabilityTests` and see it
fail; add the `AddMeter` line; see it pass.

- [ ] **Step 2: The Compose unit**

In `services/payments.yml`, beside the migrator and API:

```yaml
  # §3.2's provider, simulated (spec section 9). Payments' own dependency,
  # so it lives in Payments' unit rather than the shared baseline, and
  # docker-compose.infra-only.yml keeps it, since a Payments host run from an
  # IDE needs it as much as a containerised one. The tag is
  # Directory.Packages.props' WireMock.Net pin: one engine, one version, one
  # set of mappings for this container and the tests.
  psp-simulator:
    image: sheyenrath/wiremock.net:1.8.11
    volumes:
      - ../psp-simulator/mappings:/app/__admin/mappings:ro
    ports:
      - "5190:80"
```

On `payments-api`'s environment:

```yaml
      PaymentProvider__BaseUrl: "http://psp-simulator/"
      # §14.1's local-development exception: the simulator ignores it.
      PaymentProvider__ApiKey: "local-dev-psp"
```

and under its `depends_on`, `psp-simulator: { condition: service_started }`.
Confirm the image's mapping directory and listening port against the pinned
image's documentation; the two values above are the image's defaults at
1.8.x, and `docker compose config` plus the check below prove them.

The ports table in `deploy/compose/README.md` gains
`| PSP simulator | http://localhost:5190 | /__admin/mappings — the scripted
amounts are in deploy/compose/psp-simulator/README.md |`.

Run the secret scan's suite and gate. The local key is flagged, as it was
in this plan's own text: add the entry to `.github/secret-scan/allowed/deploy.txt`
with the fingerprint the gate prints, stating it is §14.1's local default,
and run both again.

- [ ] **Step 3: §15.4's two rows**

Add both rows to the configuration table in its existing column form, keyed
by key like every other row. `PaymentProvider__BaseUrl`: plain configuration,
required, "the provider's address; the host refuses to start without it".
`PaymentProvider__ApiKey`: **Secret**, External
Secrets, required. Run `/check-links` and `/validate-blueprint`.

- [ ] **Step 4: Prove the simulator under Compose**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build --wait
curl -s -X POST http://localhost:5190/v1/authorisations -H "Idempotency-Key: authorise:probe" \
    -H "Content-Type: application/json" -d '{"amountMinor":4201,"currency":"EUR","payerId":"00000000-0000-0000-0000-000000000001"}'
curl -s -X POST http://localhost:5190/v1/authorisations -H "Idempotency-Key: authorise:probe" \
    -H "Content-Type: application/json" -d '{"amountMinor":4210,"currency":"EUR","payerId":"00000000-0000-0000-0000-000000000001"}'
docker compose -f deploy/compose/docker-compose.yml down -v
```

Expected: the first answers `{"status":"declined","code":"card_declined"}`,
the second `{"status":"approved","reference":"psp_authorise:probe"}`, and
`payments-api` is healthy.

- [ ] **Step 5: Commit**

```bash
git add src/BuildingBlocks/Common.Web tests/Common.Web.Tests deploy/compose docs/backend-architecture/15-cicd-deployment.md .github/secret-scan/allowed
git commit -m "feat(payments): the simulator runs beside Payments, its meter is exported, and §15.4 names both keys"
```

---

### Task 5: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green.
- [ ] `py -3.12 -m unittest discover -s .github/secret-scan` then
  `py -3.12 .github/secret-scan/secret_scan.py` — both exit 0.
- [ ] PR body: `| Class | A+D+E |`, touch set from the Global Constraints,
  and the two `curl` answers from Task 4 as evidence. Then `/ship`.

## Self-review

- Spec coverage: section 4's keys → Task 1; section 9's port, adapter,
  translation table, budget and simulator → Tasks 1–4, one test per
  translation row and per scripted amount; section 11's two keys and §15.4 →
  Tasks 3, 4; section 12's counter and its export → Tasks 3, 4.
- Not in this PR: any caller of the port (PR-3, PR-4).
- Types: `IPaymentProvider`, `AuthorisationRequest`, `AuthorisationResult.
  Authorised/Declined`, `VoidRequest`, `PaymentProviderUnavailableException`,
  `PaymentMismatchException`, `ProviderMetrics.MeterName`, `ProviderHop` and
  the factory's `providerBaseUrl` match PR-3 and PR-4.

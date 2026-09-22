# Shipping PR-2 — the carrier anti-corruption layer and its simulator — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Shipping the port §3.2's *Conformist* relationship stands
behind, an HTTP adapter that is the only code knowing the carrier's wire
format, and a WireMock.Net simulator that Compose runs and the tests load in
process from the same mapping files. Nothing calls the port yet.

**Architecture:** `ICarrierGateway` in `Shipping.Application` speaks the
domain's vocabulary: a booking is `Booked(reference, trackingNumber)` or
`Refused(reason)`, a cancellation is `Cancelled` or `TooLate`, and a page of
tracking is already translated to `TrackingStatus`. A transient fault is an
exception, never a result. `HttpCarrierGateway` in
`Shipping.Infrastructure.Carrier` is a typed `HttpClient` behind
`AddStandardResilienceHandler`, with a per-attempt counter inside the pipeline
and a circuit breaker sized to a worker's call rate rather than to an
endpoint's. Every call that writes carries an idempotency key derived from the
aggregate, which is what makes the in-client retry safe.

**Tech Stack:** `Microsoft.Extensions.Http.Resilience` (pinned, used by the
BFF and by Payments), WireMock.Net (pinned, named by §12's table and Appendix
B), `System.Diagnostics.Metrics`.

**Spec:** `docs/superpowers/specs/2026-09-22-shipping-service-design.md`,
sections 1 (who the carrier is), 3, 4 (the idempotency keys), 9 (the carrier
port, its answer table, the bounds on a stranger's input, `CarrierHop` and the
simulator's postal-code script), 10 (`Carrier__BaseUrl` and `Carrier__ApiKey`),
11 (the `Shipping.Outbound` meter and its `AddMeter` line), 12 (the adapter's
suite) and 13 (§15.4's two rows).

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Shipping/**`, `tests/Shipping.*`,
  `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`,
  `tests/Common.Web.Tests/ObservabilityTests.cs`, `deploy/compose/**`,
  `.gitattributes`, `.github/secret-scan/allowed/deploy.txt`,
  `docs/backend-architecture/15-cicd-deployment.md`, `docs/secrets.md`
  — paths only, comma-separated, no prose inside the cell and no trailing
  stop: the gate strips a token's backticks only when the token ends in one,
  so a full stop after the last path refuses the row.
  Why each: the service's own code and tests are A; the three package
  references (`Microsoft.Extensions.Http.Resilience` and
  `Microsoft.Extensions.Hosting.Abstractions` in `Shipping.Infrastructure`,
  `WireMock.Net` in `Shipping.Worker.Tests`, none with a `Version=`) are E;
  `Common.Web`'s one `AddMeter` line and its test are A's
  `src/BuildingBlocks/**` and `tests/**`; the simulator, its declared line
  endings, the allow-list entry and the two documents are D.
  `.github/locality-gate/locality_gate.py` admits `A+D+E` today — it is the
  one three-member cell the class row accepts — so no gate change is owed
  and the row is spelled exactly that way.
- Depends on PR-1 having merged: `Shipping.Domain`, `Shipping.Application`,
  `Shipping.Infrastructure`, `Shipping.Worker` and the four test projects
  exist, `TrackingStatus` and `ShipmentId` are PR-1's in
  `Shipping.Domain.Shipments`, `Shipping.Application`'s
  `DependencyInjection` registers `TimeProvider.System` as every other
  service's does, `Shipping.TestSupport` holds `ShippingWorkerFactory` over
  the worker's `Program`, and `deploy/compose/services/shipping.yml` exists.
  Where PR-1 spelled one of those differently, the spelling moves and nothing
  else in this plan does.
- No `Directory.Packages.props` change and no Appendix B row: all three
  packages are pinned and listed already. `WireMock.Net` is pinned at
  `2.12.0`, `Microsoft.Extensions.Http.Resilience` at `10.0.0` and
  `Microsoft.Extensions.Hosting.Abstractions` at `10.0.0`.
- The simulator's image tag equals `Directory.Packages.props`'s
  `WireMock.Net` pin, so the container and the in-process server are one
  engine at one version.
- The wire format appears in `HttpCarrierGateway` and the mapping files and
  nowhere else. No `Shipping.Application` type names HTTP.
- **Four of `docs/secrets.md`'s five places, and the fifth is PR-7's.** A
  required key owes Compose, the Aspire host (§14.2, not adopted, so
  nothing), Helm values, §15.4's table and the test fixture. Shipping has no
  chart until PR-7, and a values entry in a chart that does not exist cannot
  be written; the spec assigns the `carrier` capability to PR-7 for that
  reason, which is the shape Payments took — its PR-2 did Compose, §15.4 and
  the fixture, and its deploy PR did the chart. PR-7's touch set carries the
  Helm half.
- Comments say why and cite the owner — a section, an ADR or a symbol, never
  a pull request or a test — and no comment block runs past ten lines.
  Explicit local types, file-scoped namespaces, 120 columns. `py -3.12`.
- Every step that adds behaviour writes its test first.

---

### Task 1: The carrier port and its vocabulary

**Files:**
- Create: `src/Services/Shipping/Shipping.Application/Carrier/ICarrierGateway.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/DeliveryAddress.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/BookingRequest.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/BookingResult.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/CancellationRequest.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/CancellationResult.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/CarrierEvent.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/CarrierUnavailableException.cs`
- Create: `src/Services/Shipping/Shipping.Application/Carrier/CarrierLimits.cs`
- Test: `tests/Shipping.Application.Tests/CarrierPortTests.cs`

**Interfaces:**
- Consumes: `ShipmentId` and `TrackingStatus` from `Shipping.Domain.Shipments`.
- Produces:

```csharp
namespace Shipping.Application.Carrier;

public sealed record DeliveryAddress(string Line1, string? Line2, string City, string PostalCode, string Country);

public sealed record BookingRequest(ShipmentId ShipmentId, DeliveryAddress Address)
{
    public string IdempotencyKey => $"book:{ShipmentId.Value}";
}

public sealed record CancellationRequest(ShipmentId ShipmentId, string Reference)
{
    public string IdempotencyKey => $"cancel:{ShipmentId.Value}";
}

public abstract record BookingResult
{
    public sealed record Booked(string Reference, string TrackingNumber) : BookingResult;
    public sealed record Refused(string Reason) : BookingResult;
}

public abstract record CancellationResult
{
    public sealed record Cancelled : CancellationResult;
    public sealed record TooLate : CancellationResult;
}

public sealed record CarrierEvent(string CarrierEventId, TrackingStatus Status, DateTimeOffset OccurredAt);

public interface ICarrierGateway
{
    Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct);
    Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct);
    Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct);
}

public sealed class CarrierUnavailableException : Exception { /* the three standard constructors */ }

public static class CarrierLimits
{
    public const int MaxReferenceLength = ShipmentLimits.MaxCarrierReferenceLength;
    public const int MaxTrackingNumberLength = ShipmentLimits.MaxTrackingNumberLength;
    public const int MaxReasonLength = ShipmentLimits.MaxUnfulfillableReasonLength;
    public const int MaxCarrierEventIdLength = ShipmentLimits.MaxCarrierEventIdLength;
}
```

Every member is PR-1's `ShipmentLimits` and no member is a number: the widths
belong to the columns, and a literal here would be a second owner that drifts
the first time one of them moves.

- [ ] **Step 1: Write the failing test**

`tests/Shipping.Application.Tests/CarrierPortTests.cs`:

```csharp
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shouldly;
using Xunit;

namespace Shipping.Application.Tests;

public class CarrierPortTests
{
    [Fact]
    public void The_keys_are_the_shipment_and_differ_between_the_two_acts()
    {
        ShipmentId shipment = ShipmentId.New();

        string book = new BookingRequest(shipment, Address()).IdempotencyKey;
        string cancel = new CancellationRequest(shipment, "crr_x").IdempotencyKey;

        book.ShouldBe($"book:{shipment.Value}");
        cancel.ShouldBe($"cancel:{shipment.Value}");
        cancel.ShouldNotBe(book, "a cancel replayed under the booking's key would return the booking");
    }

    [Fact]
    public void A_booking_is_booked_or_refused_and_nothing_else()
    {
        typeof(BookingResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(BookingResult)))
            .Select(t => t.Name)
            .ShouldBe(["Booked", "Refused"], ignoreOrder: true,
                "a transient fault is an exception, so an outage can never reach the row as a refusal");
    }

    [Fact]
    public void A_cancellation_is_cancelled_or_too_late_and_nothing_else()
    {
        typeof(CancellationResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(CancellationResult)))
            .Select(t => t.Name)
            .ShouldBe(["Cancelled", "TooLate"], ignoreOrder: true,
                "section 6: the carrier either voids the shipment or says the parcel has gone");
    }

    [Fact]
    public void A_carrier_event_carries_no_link_of_the_carriers()
    {
        // Section 9's stored-no-URL rule, asserted on the shape rather than on
        // one answer: a link the carrier supplies is a phishing primitive once
        // anything renders it, and a field is the only way one could be kept.
        typeof(CarrierEvent).GetProperties()
            .Select(p => p.Name)
            .ShouldBe(["CarrierEventId", "Status", "OccurredAt"], ignoreOrder: true);
    }

    private static DeliveryAddress Address() =>
        new("1 Abay Avenue", null, "Almaty", "050000", "KZ");
}
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet test tests/Shipping.Application.Tests --filter "FullyQualifiedName~CarrierPortTests"`
Expected: compile failure — `Shipping.Application.Carrier` does not exist.

- [ ] **Step 3: Write the types**

`ICarrierGateway.cs`:

```csharp
using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// §3.2's anti-corruption layer: the carrier in this service's vocabulary.
/// </summary>
/// <remarks>
/// A transient fault throws <see cref="CarrierUnavailableException"/> and is
/// never a <see cref="BookingResult"/> or <see cref="CancellationResult"/>, so
/// "the carrier is down" cannot reach a shipment as a refusal (spec, section
/// 9). Each call that writes carries its idempotency key, which is what lets a
/// worker's pass repeat whole after the carrier answered and before the commit.
/// </remarks>
public interface ICarrierGateway
{
    Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct);

    Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct);

    /// <summary>
    /// The carrier's page for one booking, already translated. An empty page
    /// is an answer: a carrier that has not heard of the reference yet.
    /// </summary>
    Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct);
}
```

`DeliveryAddress.cs`:

```csharp
namespace Shipping.Application.Carrier;

/// <summary>
/// The address as the carrier is shown it, and the whole of what it is shown
/// besides the shipment's id (spec, section 9). It carries no recipient's
/// name, because <c>Order.ShippingAddress</c> holds none.
/// </summary>
public sealed record DeliveryAddress(string Line1, string? Line2, string City, string PostalCode, string Country);
```

`BookingRequest.cs`:

```csharp
using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// One shipment offered to the carrier. The key is the aggregate's (spec,
/// section 4): the crash that doubles the call is the one between the
/// carrier's answer and the commit, and the next pass repeats the call under
/// the same key and receives the first answer.
/// </summary>
public sealed record BookingRequest(ShipmentId ShipmentId, DeliveryAddress Address)
{
    public string IdempotencyKey => $"book:{ShipmentId.Value}";
}
```

`CancellationRequest.cs`, the same summary one act over, with
`$"cancel:{ShipmentId.Value}"` and the carrier's own `Reference` on the
record, because a cancel is addressed to the booking and not to the shipment.

`BookingResult.cs`:

```csharp
namespace Shipping.Application.Carrier;

/// <summary>The carrier's answer to a <see cref="BookingRequest"/>.</summary>
public abstract record BookingResult
{
    private BookingResult() { }

    public sealed record Booked(string Reference, string TrackingNumber) : BookingResult;

    public sealed record Refused(string Reason) : BookingResult;
}
```

`CancellationResult.cs` in the same shape, with `Cancelled` and `TooLate`
carrying nothing: the reference is the caller's and the reason is the state,
not a string.

`CarrierEvent.cs`:

```csharp
using Shipping.Domain.Shipments;

namespace Shipping.Application.Carrier;

/// <summary>
/// One translated fact from the carrier's feed. <see cref="CarrierEventId"/>
/// is the carrier's own id and half of <c>TrackingEvent</c>'s key, which is
/// what makes a repeated page free (spec, section 5).
/// </summary>
/// <remarks>
/// There is no link here on purpose (spec, section 9): the carrier's own
/// URLs are never stored, and whoever renders one rebuilds it from a host
/// its own configuration allow-lists.
/// </remarks>
public sealed record CarrierEvent(string CarrierEventId, TrackingStatus Status, DateTimeOffset OccurredAt);
```

`CarrierUnavailableException.cs` takes the three standard constructors, as
`PaymentProviderUnavailableException` does, with the summary "A fault from the
carrier that is not an answer: the worker's backoff owns it, and it never
becomes a refusal (spec, section 9)."

`CarrierLimits.cs`:

```csharp
namespace Shipping.Application.Carrier;

/// <summary>
/// What a carrier's answer may carry and still be recorded.
/// <c>Shipment.CarrierReference</c>, <c>Shipment.TrackingNumber</c> and
/// <c>Shipment.UnfulfillableReason</c> are these widths, and
/// <c>TrackingEvent</c>'s key holds the event id, so the adapter refuses a
/// longer answer before a row is written rather than at the insert: a
/// booking that cannot be recorded is a parcel the carrier is holding with
/// nothing on the row to cancel it by.
/// </summary>
public static class CarrierLimits
{
    public const int MaxReferenceLength = ShipmentLimits.MaxCarrierReferenceLength;

    public const int MaxTrackingNumberLength = ShipmentLimits.MaxTrackingNumberLength;

    public const int MaxReasonLength = ShipmentLimits.MaxUnfulfillableReasonLength;

    public const int MaxCarrierEventIdLength = ShipmentLimits.MaxCarrierEventIdLength;
}
```

Each member cites PR-1's `ShipmentLimits` in `Shipping.Domain.Shipments`,
which the EF configuration already reads, so the widths have one owner and
this file adds a name for the adapter's side of the seam and no number. The
file needs `using Shipping.Domain.Shipments;` above its namespace.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Shipping.Application.Tests --filter "FullyQualifiedName~CarrierPortTests"
dotnet build Platform.slnx
git add src/Services/Shipping/Shipping.Application src/Services/Shipping/Shipping.Infrastructure tests/Shipping.Application.Tests
git commit -m "feat(shipping): the carrier port, in the domain's vocabulary"
```

---

### Task 2: The simulator's mappings

**Files:**
- Create: `deploy/compose/carrier-simulator/mappings/book-refused.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-unavailable.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-stalled.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-transit.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-reversed.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-strange.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-late.json`
- Create: `deploy/compose/carrier-simulator/mappings/book-booked.json`
- Create: `deploy/compose/carrier-simulator/mappings/events-transit.json`
- Create: `deploy/compose/carrier-simulator/mappings/events-reversed.json`
- Create: `deploy/compose/carrier-simulator/mappings/events-strange.json`
- Create: `deploy/compose/carrier-simulator/mappings/events-default.json`
- Create: `deploy/compose/carrier-simulator/mappings/cancel-late.json`
- Create: `deploy/compose/carrier-simulator/mappings/cancel.json`
- Create: `deploy/compose/carrier-simulator/README.md`
- Modify: `.gitattributes` — the tree's line endings declared

The wire format these define, and the adapter in Task 3 speaks:

| | |
|---|---|
| Book | `POST /v1/shipments`, header `Idempotency-Key`, body `{"shipmentId":"…","address":{"line1":"…","line2":null,"city":"…","postalCode":"…","country":"KZ"}}` |
| Booked | `201 {"status":"booked","reference":"crr_SIM-OK","trackingNumber":"TRK-SIM-OK"}` |
| Refused | `422 {"status":"refused","code":"address_not_serviceable"}` |
| Cancel | `POST /v1/shipments/{reference}/cancel`, header `Idempotency-Key` → `200 {"status":"cancelled"}` |
| Too late | `409 {"status":"too_late","code":"already_collected"}` |
| Events | `GET /v1/shipments/{reference}/events` → `200 {"events":[{"id":"…","status":"collected","occurredAt":"…","url":"…"}]}`, or `404` |

- [ ] **Step 1: Write the eight booking mappings**

The scripted seven match on the body's `postalCode` at `Priority` 1; the plain
booking matches every request at `Priority` 10, so it answers only what the
seven did not. The body regex relies on `System.Text.Json`'s compact output,
which is what the adapter sends, and allows the whitespace a hand-typed
`curl` body carries.

**The reference is literal per script, and that is the whole state machine.**
The simulator holds none, so the booking's reference is what the later reads
key on: a postal code picks a reference, and the events and cancel mappings
match that reference's path. A real carrier mints a unique one and the port
does not care, which is the seam this file exists to prove.

`book-refused.json`:

```json
{
  "Guid": "7c4d1a20-0001-4000-8000-000000000001",
  "Title": "SIM-REFUSED is an address the carrier will not serve",
  "Priority": 1,
  "Request": {
    "Path": { "Matchers": [{ "Name": "ExactMatcher", "Pattern": "/v1/shipments" }] },
    "Methods": ["POST"],
    "Body": { "Matcher": { "Name": "RegexMatcher", "Pattern": "\"postalCode\"\\s*:\\s*\"SIM-REFUSED\"" } }
  },
  "Response": {
    "StatusCode": 422,
    "Headers": { "Content-Type": "application/json" },
    "BodyAsJson": { "status": "refused", "code": "address_not_serviceable" }
  }
}
```

`book-unavailable.json`: Guid `…0002`, pattern `SIM-DOWN`, `StatusCode` 503,
`BodyAsJson` `{ "status": "unavailable" }`.

`book-stalled.json`: Guid `…0003`, pattern `SIM-SLOW`, `StatusCode` 201,
`"Delay": 30000`, and the plain booking's body below with reference
`crr_SIM-SLOW` — the answer would be a booking, and the adapter gives up
before it arrives, because 30 s is past `CarrierHop.TotalRequestTimeout`.

`book-transit.json`: Guid `…0004`, pattern `SIM-TRANSIT`, `StatusCode` 201,
`BodyAsJson` `{ "status": "booked", "reference": "crr_SIM-TRANSIT",
"trackingNumber": "TRK-SIM-TRANSIT" }`.

`book-reversed.json`: Guid `…0005`, pattern `SIM-REVERSED`, the same with
`crr_SIM-REVERSED` and `TRK-SIM-REVERSED`.

`book-strange.json`: Guid `…0006`, pattern `SIM-STRANGE`, the same with
`crr_SIM-STRANGE` and `TRK-SIM-STRANGE`.

`book-late.json`: Guid `…0007`, pattern `SIM-LATE`, the same with
`crr_SIM-LATE` and `TRK-SIM-LATE`.

`book-booked.json`:

```json
{
  "Guid": "7c4d1a20-0010-4000-8000-000000000010",
  "Title": "Every other postal code books, and its feed runs collected then delivered",
  "Priority": 10,
  "Request": {
    "Path": { "Matchers": [{ "Name": "ExactMatcher", "Pattern": "/v1/shipments" }] },
    "Methods": ["POST"]
  },
  "Response": {
    "StatusCode": 201,
    "Headers": { "Content-Type": "application/json" },
    "BodyAsJson": { "status": "booked", "reference": "crr_SIM-OK", "trackingNumber": "TRK-SIM-OK" }
  }
}
```

- [ ] **Step 2: Write the four event feeds**

`events-default.json`:

```json
{
  "Guid": "7c4d1a20-0020-4000-8000-000000000020",
  "Title": "Any booking not scripted otherwise: collected, then delivered",
  "Priority": 10,
  "Request": {
    "Path": { "Matchers": [{ "Name": "WildcardMatcher", "Pattern": "/v1/shipments/*/events" }] },
    "Methods": ["GET"]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json" },
    "BodyAsJson": {
      "events": [
        {
          "id": "evt-collected",
          "status": "collected",
          "occurredAt": "2026-01-02T09:00:00Z",
          "url": "https://carrier.example/track/crr"
        },
        {
          "id": "evt-delivered",
          "status": "delivered",
          "occurredAt": "2026-01-03T15:30:00Z",
          "url": "https://carrier.example/track/crr"
        }
      ]
    }
  }
}
```

The `url` on the ordinary page is deliberate: the carrier sends one on every
answer, so the rule that none is kept is exercised by the happy path and not
only by the hostile one.

`events-transit.json`: Guid `…0021`, `Priority` 1, `ExactMatcher` on
`/v1/shipments/crr_SIM-TRANSIT/events`, the `collected` entry alone.

`events-reversed.json`: Guid `…0022`, `Priority` 1, `ExactMatcher` on
`/v1/shipments/crr_SIM-REVERSED/events`, the same two entries with
`evt-delivered` first on the page — the carrier orders nothing, which is why
section 5's promotion is monotonic by rank and not by arrival.

`events-strange.json`: Guid `…0023`, `Priority` 1, `ExactMatcher` on
`/v1/shipments/crr_SIM-STRANGE/events`:

```json
{
  "Guid": "7c4d1a20-0023-4000-8000-000000000023",
  "Title": "SIM-STRANGE: a status nobody agreed, a timestamp from the future, a link on a foreign host",
  "Priority": 1,
  "Request": {
    "Path": { "Matchers": [{ "Name": "ExactMatcher", "Pattern": "/v1/shipments/crr_SIM-STRANGE/events" }] },
    "Methods": ["GET"]
  },
  "Response": {
    "StatusCode": 200,
    "Headers": { "Content-Type": "application/json" },
    "BodyAsJson": {
      "events": [
        {
          "id": "evt-strange",
          "status": "teleported",
          "occurredAt": "2099-04-01T00:00:00Z",
          "url": "https://not-the-carrier.example/pay-now"
        }
      ]
    }
  }
}
```

- [ ] **Step 3: Write the two cancels**

`cancel-late.json`: Guid `…0030`, `Priority` 1, `ExactMatcher` on
`/v1/shipments/crr_SIM-LATE/cancel`, `POST`, `409` with `BodyAsJson`
`{ "status": "too_late", "code": "already_collected" }`.

`cancel.json`: Guid `…0031`, `Priority` 10, `WildcardMatcher` on
`/v1/shipments/*/cancel`, `POST`, `200` with `BodyAsJson`
`{ "status": "cancelled" }`.

- [ ] **Step 4: Write the README**

`deploy/compose/carrier-simulator/README.md`, in `psp-simulator/README.md`'s
three sections: what this is (§3.2's carrier, simulated, and a link to spec
section 9 as the specification); spec section 9's postal-code table copied as
the one place a person at the keyboard reads it; and how to watch each —
"place an order whose delivery postal code is `SIM-DOWN` and watch the
fulfilment worker back off, then book when the code changes". Say that the
reference is literal per script because the simulator holds no state, and that
the feed's timestamps are fixed for the same reason. Nothing about history.

- [ ] **Step 5: Declare the tree's line endings**

In `.gitattributes`, after the `deploy/compose/rabbitmq/**` paragraph:

```gitattributes
# deploy/compose/carrier-simulator/ joins on the rabbitmq/ tree's argument one
# directory over: these files are mounted into a Linux container and read by a
# server there, and the same bytes are loaded by an in-process server on
# Windows. A mapping's RegexMatcher pattern is matched against a body the
# adapter sends, so a tree that arrives CRLF on one machine and LF on another
# is a matcher whose result depends on who checked it out. The README beside
# them joins so the directory is one thing.
deploy/compose/carrier-simulator/** text eol=lf
```

- [ ] **Step 6: Commit**

```bash
git add deploy/compose/carrier-simulator .gitattributes
git commit -m "feat(shipping): the carrier simulator's mappings, scripted by postal code"
```

The mappings are proved by Task 3's tests, which load this directory.

---

### Task 3: The adapter, its budget, its breaker and its counter

**Files:**
- Modify: `src/Services/Shipping/Shipping.Infrastructure/Shipping.Infrastructure.csproj`
  — `<PackageReference Include="Microsoft.Extensions.Http.Resilience" />` and
  `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />`
  for the `IHostEnvironment` the registration takes, no `Version=`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Carrier/CarrierHop.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Carrier/CarrierMetrics.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Carrier/CarrierAttemptCounter.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Carrier/CarrierAnswerBuffer.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Carrier/HttpCarrierGateway.cs`
- Create: `src/Services/Shipping/Shipping.Infrastructure/Carrier/DependencyInjection.cs`
- Modify: `src/Services/Shipping/Shipping.Worker/Program.cs` —
  `builder.Services.AddCarrierGateway(builder.Configuration, builder.Environment);`
- Modify: `tests/Shipping.Worker.Tests/Shipping.Worker.Tests.csproj` —
  `<PackageReference Include="WireMock.Net" />`
- Modify: `tests/Shipping.TestSupport/ShippingWorkerFactory.cs` — a
  `string carrierBaseUrl = UnreachableCarrier` parameter and a
  `string? carrierApiKey = null` beside it, with `UnreachableCarrier` and
  `LocalCarrierApiKey` as constants
- Create: `tests/Shipping.TestSupport/SimulatorMappings.cs`
- Create: `tests/Shipping.Worker.Tests/UnavailableCounter.cs` — the counter
  reader both carrier suites use
- Test: `tests/Shipping.Worker.Tests/HttpCarrierGatewayTests.cs`
- Test: `tests/Shipping.Worker.Tests/CarrierFaultTests.cs` — the rows that end
  in a fault, each over a host of its own

**Interfaces:**
- Consumes: Task 1's port; Task 2's mapping directory.
- Produces: `CarrierHop`'s numbers; `CarrierMetrics.MeterName` of
  `Shipping.Outbound` with `Counter<long> shipping.carrier.unavailable`;
  `IServiceCollection AddCarrierGateway(IConfiguration, IHostEnvironment)`
  with `DependencyInjection.BaseUrlKey` and `ApiKeyKey`;
  `SimulatorMappings.Directory()`; the factory's two new parameters;
  `UnavailableCounter.Of(IServiceProvider)` and the `UnavailableCount` it
  returns, read by both carrier suites.

- [ ] **Step 1: Write the failing adapter tests**

`SimulatorMappings.cs` is `Payments.TestSupport`'s walk with the carrier's
directory: up from `AppContext.BaseDirectory` to the directory holding
`Platform.slnx`, then `deploy/compose/carrier-simulator/mappings` beneath it,
throwing a message naming the path when it is absent.

```csharp
namespace Shipping.TestSupport;

/// <summary>
/// The carrier simulator's mapping files, where Compose mounts them from
/// (§14.1), so a test over an in-process server loads the same stubs the
/// local stack runs rather than a copy that could drift from them.
/// </summary>
public static class SimulatorMappings
{
    public static string Directory()
    {
        string root = RepositoryRoot();
        string mappings = Path.Combine(root, "deploy", "compose", "carrier-simulator", "mappings");
        if (!System.IO.Directory.Exists(mappings))
        {
            throw new InvalidOperationException(
                $"Found the solution at {root} but no simulator mappings at {mappings} (§14.1).");
        }

        return mappings;
    }

    /// <summary>The directory holding <c>Platform.slnx</c>, walked up to from the test's own output.</summary>
    public static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Platform.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            $"No Platform.slnx above {AppContext.BaseDirectory}; the simulator's files cannot be found.");
    }
}
```

Both carrier suites read the same counter, and neither may read it by name, so
the reader is written once and takes the host's provider as a parameter. One
suite keeps a host for the class and the other a host per test, which is
exactly why the provider is an argument rather than a field the helper closes
over. `tests/Shipping.Worker.Tests/UnavailableCounter.cs`:

```csharp
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Infrastructure.Carrier;
using Shouldly;

namespace Shipping.Worker.Tests;

/// <summary>
/// One host's <c>shipping.carrier.unavailable</c> counter, never one matched
/// by name: a <c>MeterListener</c> is process-wide, so another host's carrier
/// would count into it. The provider is the caller's because the two suites
/// keep their hosts differently.
/// </summary>
internal static class UnavailableCounter
{
    public static UnavailableCount Of(IServiceProvider services)
    {
        services.GetRequiredService<CarrierMetrics>();
        Meter mine = services.GetRequiredService<IMeterFactory>().Create(CarrierMetrics.MeterName);
        UnavailableCount count = new(mine);
        count.Enabled.ShouldBeTrue("no counter on this host's meter was enabled, so a zero would prove nothing");
        return count;
    }
}

internal sealed class UnavailableCount : IDisposable
{
    private readonly MeterListener _listener = new();
    private long _counted;

    public UnavailableCount(Meter mine)
    {
        _listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, mine) && instrument.Name == "shipping.carrier.unavailable")
            {
                l.EnableMeasurementEvents(instrument);
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

`GetRequiredService<CarrierMetrics>()` before the meter is resolved is the
half that is easy to lose: the counter is created in the metrics type's
constructor, and a listener that starts before anything instantiated it sees
no instrument published and `Enabled` stays false — which the guard turns into
a failure rather than a silent zero.

`tests/Shipping.Worker.Tests/HttpCarrierGatewayTests.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.TestSupport;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;
using CarrierRegistration = Shipping.Infrastructure.Carrier.DependencyInjection;

namespace Shipping.Worker.Tests;

/// <summary>
/// The adapter over a real HTTP server loading the simulator's own mappings,
/// so the file Compose runs is the file these assert (§12: WireMock.Net for a
/// third-party API).
/// </summary>
public sealed class HttpCarrierGatewayTests : IClassFixture<HttpCarrierGatewayTests.CarrierHost>
{
    private const string UnreachableSql =
        "Server=sql.invalid;Database=Shipping;User Id=x;Password=x;TrustServerCertificate=true";

    private const string UnreachableRabbit = "amqp://shipping-svc:x@rabbit.invalid:5672";

    /// <summary>
    /// One server and one host for the class: a host over an unreachable
    /// broker can take seconds to stop, so only a test that needs a pipeline
    /// of its own builds one. Every case that leaves the breaker with a
    /// failure to remember is in <c>CarrierFaultTests</c> instead.
    /// </summary>
    public sealed class CarrierHost : IDisposable
    {
        public CarrierHost()
        {
            Server = WireMockServer.Start();
            Factory = new ShippingWorkerFactory(UnreachableSql, UnreachableRabbit, Server.Urls[0] + "/");
        }

        public WireMockServer Server { get; }

        public ShippingWorkerFactory Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            Server.Stop();
        }
    }

    private readonly WireMockServer _server;
    private readonly ShippingWorkerFactory _factory;

    public HttpCarrierGatewayTests(CarrierHost host)
    {
        _server = host.Server;
        _factory = host.Factory;

        // Each test starts from the simulator's files alone: no stub another
        // test added, and no request it made.
        _server.ResetLogEntries();
        _server.ResetMappings();
        _server.ReadStaticMappings(SimulatorMappings.Directory());
    }

    private ICarrierGateway Carrier() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<ICarrierGateway>();

    private static BookingRequest Booking(string postalCode, ShipmentId? shipment = null) =>
        new(shipment ?? ShipmentId.New(), new DeliveryAddress("1 Abay Avenue", null, "Almaty", postalCode, "KZ"));

    private int Calls(string path) =>
        _server.LogEntries.Count(e => e.RequestMessage!.Path == path);

    [Fact]
    public async Task An_ordinary_address_books_and_the_key_is_the_shipments()
    {
        ShipmentId shipment = ShipmentId.New();
        CancellationToken ct = TestContext.Current.CancellationToken;

        BookingResult result = await Carrier().BookAsync(Booking("050000", shipment), ct);

        BookingResult.Booked booked = result.ShouldBeOfType<BookingResult.Booked>();
        booked.Reference.ShouldBe("crr_SIM-OK");
        booked.TrackingNumber.ShouldBe("TRK-SIM-OK");
        _server.LogEntries.ShouldAllBe(e =>
            e.RequestMessage!.Headers!["Idempotency-Key"].Single() == $"book:{shipment.Value}");
    }

    [Fact]
    public async Task The_carrier_is_shown_the_address_and_the_shipments_id_and_nothing_else()
    {
        ShipmentId shipment = ShipmentId.New();

        await Carrier().BookAsync(Booking("050000", shipment), TestContext.Current.CancellationToken);

        ILogEntry call = _server.LogEntries.ShouldHaveSingleItem();
        string body = call.RequestMessage!.Body!;
        body.ShouldContain(shipment.Value.ToString());
        body.ShouldContain("Abay");
        body.ShouldNotContain("customer", Case.Insensitive,
            "section 7: the carrier is shown an address and the shipment's id");
        call.RequestMessage.Headers!["Authorization"].Single()
            .ShouldBe($"Bearer {ShippingWorkerFactory.LocalCarrierApiKey}");
    }

    [Fact]
    public async Task A_refused_address_is_an_answer_and_is_not_retried()
    {
        BookingResult result = await Carrier().BookAsync(Booking("SIM-REFUSED"), TestContext.Current.CancellationToken);

        result.ShouldBe(new BookingResult.Refused("address_not_serviceable"));
        Calls("/v1/shipments").ShouldBe(1, "a refusal is an answer, and the pipeline does not retry a 422");
    }

    [Fact]
    public async Task A_refused_connection_is_unavailable_rather_than_a_refusal()
    {
        using ShippingWorkerFactory dead = new(UnreachableSql, UnreachableRabbit, "http://carrier.invalid/");

        await Should.ThrowAsync<CarrierUnavailableException>(() => dead.Services.CreateScope().ServiceProvider
            .GetRequiredService<ICarrierGateway>()
            .BookAsync(Booking("050000"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Every_attempt_and_every_bounded_delay_fit_inside_the_total()
    {
        TimeSpan worst = CarrierHop.AttemptTimeout * (CarrierHop.MaxRetryAttempts + 1)
                         + CarrierHop.MaxRetryDelay * CarrierHop.MaxRetryAttempts;

        worst.ShouldBeLessThan(CarrierHop.TotalRequestTimeout,
            "PricingHop's argument: a total that cancels the last retry makes the retry count a fiction");

        CarrierHop.TotalRequestTimeout.ShouldBeLessThan(Common.Web.ServiceOptions.OperationTimeout,
            "§9.7: the outbound client total must be strictly below the service operation total");
    }

    [Fact]
    public void The_attempt_timeout_is_outside_the_band_a_waiting_caller_is_sized_to()
    {
        // §9.7's 1-2 s band is a caller's patience. Nobody waits on this hop:
        // a worker's row backs off, so the attempt is sized to a third party
        // behind an anti-corruption layer (spec, section 9).
        CarrierHop.AttemptTimeout.ShouldBeGreaterThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void The_breaker_samples_over_a_window_longer_than_it_breaks_for()
    {
        CarrierHop.CircuitBreakerSamplingDuration.ShouldBeGreaterThan(CarrierHop.CircuitBreakerBreakDuration,
            "a breaker that forgets its failures while open reopens on the first error after it closes");
        CarrierHop.CircuitBreakerSamplingDuration.ShouldBeGreaterThanOrEqualTo(CarrierHop.AttemptTimeout * 2,
            "the library validates this pair at startup, and a host that will not start is not a budget");
    }

    [Fact]
    public async Task A_cancel_goes_to_the_references_path_under_the_cancel_key()
    {
        ShipmentId shipment = ShipmentId.New();

        CancellationResult result = await Carrier()
            .CancelAsync(new CancellationRequest(shipment, "crr_SIM-OK"), TestContext.Current.CancellationToken);

        result.ShouldBe(new CancellationResult.Cancelled());
        ILogEntry call = _server.LogEntries.ShouldHaveSingleItem();
        call.RequestMessage!.Path.ShouldBe("/v1/shipments/crr_SIM-OK/cancel");
        call.RequestMessage.Headers!["Idempotency-Key"].Single().ShouldBe($"cancel:{shipment.Value}");
    }

    [Fact]
    public async Task A_carrier_that_has_already_collected_the_parcel_answers_too_late()
    {
        CancellationResult result = await Carrier()
            .CancelAsync(new CancellationRequest(ShipmentId.New(), "crr_SIM-LATE"), TestContext.Current.CancellationToken);

        result.ShouldBe(new CancellationResult.TooLate());
        Calls("/v1/shipments/crr_SIM-LATE/cancel").ShouldBe(1, "section 9: too late is an answer, not a fault");
    }

    [Fact]
    public async Task A_feed_is_translated_and_the_carriers_link_is_nowhere_in_it()
    {
        IReadOnlyList<CarrierEvent> page = await Carrier()
            .GetEventsAsync("crr_SIM-OK", TestContext.Current.CancellationToken);

        page.Select(e => (e.CarrierEventId, e.Status)).ShouldBe(
        [
            ("evt-collected", TrackingStatus.Collected),
            ("evt-delivered", TrackingStatus.Delivered)
        ]);
        page.ShouldAllBe(e => e.OccurredAt.Year == 2026);
        string.Join(" ", page.Select(e => e.ToString())).ShouldNotContain("carrier.example");
    }

    [Fact]
    public async Task A_page_the_carrier_ordered_backwards_is_returned_as_sent()
    {
        IReadOnlyList<CarrierEvent> page = await Carrier()
            .GetEventsAsync("crr_SIM-REVERSED", TestContext.Current.CancellationToken);

        // The adapter sorts nothing: the key makes a repeated page free and
        // section 5's promotion is monotonic by rank, so an order imposed here
        // would hide the case the domain exists to survive.
        page.Select(e => e.Status).ShouldBe([TrackingStatus.Delivered, TrackingStatus.Collected]);
    }

    [Fact]
    public async Task A_status_nobody_agreed_is_unrecognised_and_moves_nothing()
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                "{\"events\":[{\"id\":\"e1\",\"status\":\"teleported\",\"occurredAt\":\"2026-01-02T09:00:00Z\"}]}"));

        IReadOnlyList<CarrierEvent> page = await Carrier()
            .GetEventsAsync("crr_x", TestContext.Current.CancellationToken);

        page.ShouldHaveSingleItem().Status.ShouldBe(TrackingStatus.Unrecognised,
            "a conformist that faults on a new status stops tracking every shipment until a deploy");
    }

    [Fact]
    public async Task A_hostile_page_is_the_carrier_being_wrong_and_nothing_of_it_is_returned()
    {
        // SIM-STRANGE's timestamp is next year: a stored future instant would
        // sit ahead of every real one for ever, so the page is refused whole
        // rather than partly kept (spec, section 9).
        CarrierUnavailableException thrown = await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_SIM-STRANGE", TestContext.Current.CancellationToken));

        thrown.Message.ShouldNotContain("not-the-carrier.example");
    }

    [Fact]
    public async Task A_carrier_that_has_not_heard_of_the_booking_answers_an_empty_page()
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_new/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(404));

        (await Carrier().GetEventsAsync("crr_new", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        Calls("/v1/shipments/crr_new/events").ShouldBe(1, "a 404 is an answer, and the pipeline does not retry it");
    }

    [Theory]
    [InlineData(201, "{\"status\":\"refused\",\"reference\":\"crr_x\",\"trackingNumber\":\"t\"}")]
    [InlineData(201, "{\"status\":\"booked\",\"trackingNumber\":\"t\"}")]
    [InlineData(201, "{\"status\":\"booked\",\"reference\":\"crr_x\"}")]
    [InlineData(201, "{\"status\":\"booked\",\"reference\":\"   \",\"trackingNumber\":\"t\"}")]
    [InlineData(201, "not json")]
    [InlineData(422, "{\"status\":\"refused\"}")]
    [InlineData(422, "{\"status\":\"booked\",\"code\":\"address_not_serviceable\"}")]
    [InlineData(200, "{\"status\":\"booked\",\"reference\":\"crr_x\",\"trackingNumber\":\"t\"}")]
    public async Task A_booking_body_that_contradicts_its_status_is_unavailable_never_an_answer(int status, string body)
    {
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(200, "{\"status\":\"cancelled\",\"code\":null}", false)]
    [InlineData(200, "{\"status\":\"pending\"}", true)]
    [InlineData(202, "{\"status\":\"cancelled\"}", true)]
    [InlineData(409, "{\"status\":\"cancelled\"}", true)]
    public async Task A_cancel_answered_with_anything_else_is_the_carrier_being_wrong(int status, string body, bool throws)
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/*/cancel").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        Func<Task<CancellationResult>> call = () => Carrier()
            .CancelAsync(new CancellationRequest(ShipmentId.New(), "crr_x"), TestContext.Current.CancellationToken);

        if (throws)
            await Should.ThrowAsync<CarrierUnavailableException>(call);
        else
            (await call()).ShouldBe(new CancellationResult.Cancelled());
    }

    [Theory]
    [InlineData(CarrierLimits.MaxReferenceLength, true)]
    [InlineData(CarrierLimits.MaxReferenceLength + 1, false)]
    public async Task A_reference_longer_than_the_column_is_refused_before_it_is_recorded(int length, bool accepted)
    {
        string reference = new('r', length);
        _server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(201).WithBody(
                $"{{\"status\":\"booked\",\"reference\":\"{reference}\",\"trackingNumber\":\"t\"}}"));

        Func<Task<BookingResult>> call = () => Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken);

        if (accepted)
            (await call()).ShouldBe(new BookingResult.Booked(reference, "t"));
        else
            await Should.ThrowAsync<CarrierUnavailableException>(call);
    }

    [Fact]
    public async Task An_event_id_longer_than_its_key_is_refused_with_the_page()
    {
        string id = new('e', CarrierLimits.MaxCarrierEventIdLength + 1);
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $"{{\"events\":[{{\"id\":\"{id}\",\"status\":\"collected\",\"occurredAt\":\"2026-01-02T09:00:00Z\"}}]}}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_page_longer_than_the_bound_is_refused()
    {
        string events = string.Join(",", Enumerable.Range(0, CarrierHop.MaxEventsPerPage + 1).Select(i =>
            $"{{\"id\":\"e{i}\",\"status\":\"in_transit\",\"occurredAt\":\"2026-01-02T09:00:00Z\"}}"));
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($"{{\"events\":[{events}]}}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_answer_larger_than_the_bound_is_refused_before_it_is_read()
    {
        _server.Given(Request.Create().WithPath("/v1/shipments/crr_x/events").UsingGet())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("{\"events\":[" + new string('x', CarrierHop.MaxAnswerBytes + 1) + "]}"));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().GetEventsAsync("crr_x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_counted_against_the_carrier()
    {
        using UnavailableCount counted = UnavailableCounter.Of(_factory.Services);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Carrier().BookAsync(Booking("050000"), cancelled.Token));

        counted.Value.ShouldBe(0, "a pass cancelled at shutdown is not a carrier incident");
    }

    [Theory]
    [InlineData("http://carrier.example/", false)]
    [InlineData("https://carrier.example/", true)]
    public void Outside_development_only_an_https_carrier_is_accepted(string address, bool starts)
    {
        using ShippingWorkerFactory factory = new(UnreachableSql, UnreachableRabbit, address);
        using WebApplicationFactory<Program> production = factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

        if (starts)
            production.Services.GetRequiredService<ICarrierGateway>().ShouldNotBeNull();
        else
        {
            Should.Throw<InvalidOperationException>(() => production.Services)
                .Message.ShouldContain("plain HTTP outside Development");
        }
    }

    [Fact]
    public void A_missing_base_url_stops_the_host()
    {
        using ShippingWorkerFactory factory = new(UnreachableSql, UnreachableRabbit, carrierBaseUrl: "");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain(CarrierRegistration.BaseUrlKey);
    }

    [Fact]
    public void A_missing_carrier_key_stops_the_host()
    {
        using ShippingWorkerFactory factory = new(
            UnreachableSql, UnreachableRabbit, "https://carrier.example/", carrierApiKey: " ");

        Should.Throw<InvalidOperationException>(() => factory.Services)
            .Message.ShouldContain(CarrierRegistration.ApiKeyKey, Case.Sensitive,
                "§15.4 marks the key required; a host must not call a carrier unauthenticated");
    }
}
```

`ShippingWorkerFactory` gains the two parameters on
`PaymentsApiFactory`'s terms — defaulted, so every existing caller compiles
unchanged — and sets them with
`.UseSetting(CarrierRegistration.BaseUrlKey, carrierBaseUrl)` and
`.UseSetting(CarrierRegistration.ApiKeyKey, carrierApiKey ?? LocalCarrierApiKey)`.
A `using` alias is file-scoped, so the factory's header gains its own
`using CarrierRegistration = Shipping.Infrastructure.Carrier.DependencyInjection;`,
as `PaymentsApiFactory.cs` carries `ProviderRegistration`. The two constants
the parameters default to:

```csharp
/// <summary>
/// The carrier every host over this <c>Program</c> must name (§3.2).
/// Unreachable because <c>.invalid</c> never resolves, so a test that dials
/// the carrier by accident fails loudly rather than booking anything, and
/// plain HTTP because the factory runs the host as Development, the one
/// environment that allows it.
/// </summary>
public const string UnreachableCarrier = "http://carrier.invalid/";

/// <summary>
/// §14.1's local-development placeholder for the carrier key, which the
/// simulator ignores. Required by the host (§15.4), so a caller that names
/// none still gets one.
/// </summary>
public const string LocalCarrierApiKey = "local-dev-carrier";
```

**The rows that end in a fault get a host each, and that is the breaker's
doing.** `CarrierHop.CircuitBreakerMinimumThroughput` is sized to a worker's
call rate rather than to an endpoint's, so a handful of failed attempts fills
the sampling window — and the break outlasts the rest of a class's run. Behind
one `IClassFixture` the 503 case and the first stubbed status row alone would
open it, and every test after them would be refused without a request leaving
the process: `Calls(...)` would report the previous test's traffic and the
assertions would be about the fixture rather than about the carrier. Payments'
`ProviderHost` is shared safely because its breaker keeps the library's default
minimum throughput, which no suite reaches; a breaker asserted able to open
cannot be. So `tests/Shipping.Worker.Tests/CarrierFaultTests.cs` builds a
`CarrierHost` per test — the cost §12.4 asks to justify, paid here because it
is what makes each row's answer the row's:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;
using Shipping.Infrastructure.Carrier;
using Shipping.TestSupport;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Shipping.Worker.Tests;

/// <summary>
/// Section 9's transient rows, each over a host of its own because the
/// breaker they fill is sized to open (<c>CarrierHop</c>).
/// </summary>
public sealed class CarrierFaultTests : IDisposable
{
    private readonly HttpCarrierGatewayTests.CarrierHost _host = new();

    public CarrierFaultTests() =>
        _host.Server.ReadStaticMappings(SimulatorMappings.Directory());

    public void Dispose() => _host.Dispose();

    private WireMockServer Server => _host.Server;

    private ICarrierGateway Carrier() =>
        _host.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<ICarrierGateway>();

    private int Calls(string path) =>
        Server.LogEntries.Count(e => e.RequestMessage!.Path == path);

    private static BookingRequest Booking(string postalCode) =>
        new(ShipmentId.New(), new DeliveryAddress("1 Abay Avenue", null, "Almaty", postalCode, "KZ"));

    [Fact]
    public async Task A_503_is_retried_in_the_client_then_thrown_as_unavailable_and_counted_per_attempt()
    {
        using UnavailableCount counted = UnavailableCounter.Of(_host.Factory.Services);

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("SIM-DOWN"), TestContext.Current.CancellationToken));

        Calls("/v1/shipments").ShouldBe(CarrierHop.MaxRetryAttempts + 1);
        counted.Value.ShouldBe(CarrierHop.MaxRetryAttempts + 1, "one per failing attempt, not one per call");
    }

    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task A_timeout_a_throttle_or_a_server_fault_is_retried_then_thrown_as_unavailable(int status)
    {
        // Stubbed rather than scripted: section 9's table names all three and
        // the simulator scripts one, so without this a branch that dropped
        // either of the others would leave the suite green.
        Server.Given(Request.Create().WithPath("/v1/shipments").UsingPost())
            .AtPriority(0)
            .RespondWith(Response.Create().WithStatusCode(status));

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("050000"), TestContext.Current.CancellationToken));

        Calls("/v1/shipments").ShouldBe(CarrierHop.MaxRetryAttempts + 1);
    }

    [Fact]
    public async Task A_stalled_carrier_is_unavailable_within_the_total_budget_and_its_timeouts_count()
    {
        using UnavailableCount counted = UnavailableCounter.Of(_host.Factory.Services);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<CarrierUnavailableException>(() =>
            Carrier().BookAsync(Booking("SIM-SLOW"), TestContext.Current.CancellationToken));

        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(CarrierHop.TotalRequestTimeout + TimeSpan.FromSeconds(2));
        counted.Value.ShouldBeGreaterThanOrEqualTo(1, "an attempt timeout is the carrier's, counted by OnTimeout");
    }

    [Fact]
    public async Task An_open_circuit_makes_no_call_at_all()
    {
        // The breaker sits inside the retry, so one call is
        // MaxRetryAttempts + 1 attempts against the minimum throughput, and a
        // fresh host is what makes that arithmetic this test's alone.
        CancellationToken ct = TestContext.Current.CancellationToken;
        while (Calls("/v1/shipments") < CarrierHop.CircuitBreakerMinimumThroughput)
        {
            await Should.ThrowAsync<CarrierUnavailableException>(() => Carrier().BookAsync(Booking("SIM-DOWN"), ct));
        }

        int before = Calls("/v1/shipments");

        await Should.ThrowAsync<CarrierUnavailableException>(() => Carrier().BookAsync(Booking("SIM-DOWN"), ct));

        // The half that makes it a breaker rather than a slow failure: once
        // open it refuses without a request leaving this process, which is
        // what stops a worker hammering a carrier that is already down.
        Calls("/v1/shipments").ShouldBe(before);
    }
}
```

`UnavailableCounter.Of` is the helper above, and the provider it is given is
this class's own host — the one built for this test — rather than a meter
matched by name. Nothing else about the counter is repeated here.

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~Carrier"`
Expected: compile failure on `CarrierHop`, `CarrierMetrics`,
`Shipping.Infrastructure.Carrier.DependencyInjection` and the factory's two
new parameters.

- [ ] **Step 3: Write the budget, the meter and the two handlers**

`CarrierHop.cs`:

```csharp
namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// The carrier call's budget and the two intervals a worker runs at, in one
/// class because section 4's arithmetic spans them: the total sits under
/// <c>ServiceOptions.OperationTimeout</c>, each lease above the total.
/// </summary>
/// <remarks>
/// Public for the reason <c>Program</c> is (§4.2): one modifier commits less
/// than an <c>InternalsVisibleTo</c>. Retrying inside the budget is safe only
/// because a call that writes carries its idempotency key (spec, section 4).
/// </remarks>
public static class CarrierHop
{
    /// <summary>
    /// Deliberately above §9.7's one-to-two-second band, for that section's
    /// own reason: a third party behind an anti-corruption layer is sized to
    /// the wait above it, and nothing waits on this hop — the row backs off.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt, so one more request than this.</summary>
    public const int MaxRetryAttempts = 1;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The cap on one jittered delay. With jitter on, <see cref="RetryDelay"/>
    /// is a nominal and not a bound; this is what makes the budget arithmetic.
    /// </summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Strictly below <c>ServiceOptions.OperationTimeout</c> (§9.7), and
    /// inside the thirty-second drain a worker's pass has to fit.
    /// </summary>
    public static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(19);

    public const double CircuitBreakerFailureRatio = 0.5;

    /// <summary>
    /// Sized to a worker's call rate, which is what the endpoint default is
    /// not: a hundred calls in a sampling window is a threshold a loop ticking
    /// every few seconds never reaches, so the breaker would never open. Four
    /// attempts is two failed calls, and the third is refused.
    /// </summary>
    public const int CircuitBreakerMinimumThroughput = 4;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Shorter than the window above, so the breaker does not forget its
    /// failures while open and reopen on the first error after it closes.
    /// </summary>
    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most an answer may carry. A page of events is a few short fields
    /// each, so a larger body is a carrier this adapter does not understand.
    /// </summary>
    public const int MaxAnswerBytes = 128 * 1024;

    /// <summary>The most events one page may hold before it is refused whole.</summary>
    public const int MaxEventsPerPage = 200;

    /// <summary>
    /// How far ahead of this host's clock a carrier's timestamp may sit. A
    /// stored future instant sits ahead of every real one for ever, which is
    /// a promotion that never completes.
    /// </summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the fulfilment loop claims a batch. Paced by new orders.
    /// </summary>
    public static readonly TimeSpan FulfilmentTick = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the tracking loop asks the carrier. A latency number before
    /// it is a load number: nothing downstream learns of a despatch sooner
    /// than the next poll, so this is added to §13.7's two-second event
    /// target, and it is what keeps a busy table inside a carrier's rate
    /// limit.
    /// </summary>
    public static readonly TimeSpan TrackingPollInterval = TimeSpan.FromSeconds(30);
}
```

`CarrierMetrics.cs`:

```csharp
using System.Diagnostics.Metrics;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// One attempt that met a failing carrier: a fault rather than an answer.
/// §13.3's claim rule does not reach it — a pass that rolls back still met
/// a failing carrier.
/// </summary>
/// <remarks>
/// The meter is named for the work that leaves this service and not for the
/// carrier: the other two instruments it will carry are the address
/// adapter's and the waiting gauge's (§13.2).
/// </remarks>
public sealed class CarrierMetrics
{
    public const string MeterName = "Shipping.Outbound";

    private readonly Counter<long> _unavailable;

    public CarrierMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create(MeterName);
        _unavailable = meter.CreateCounter<long>(
            "shipping.carrier.unavailable",
            unit: "{attempt}",
            description: "Carrier attempts that ended in a fault rather than an answer; the row backs off.");
    }

    public void Unavailable() => _unavailable.Add(1);
}
```

`CarrierAttemptCounter.cs` — a `DelegatingHandler` inside the resilience
pipeline, so it sees each attempt, in `ProviderAttemptCounter`'s shape:

```csharp
using System.Net;

namespace Shipping.Infrastructure.Carrier;

internal sealed class CarrierAttemptCounter(CarrierMetrics metrics) : DelegatingHandler
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
            // A refused or broken connection is the carrier's. A cancelled
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

`CarrierAnswerBuffer.cs` is `ProviderAnswerBuffer` with
`CarrierHop.MaxAnswerBytes`, and the same summary: the body is read whole
inside the attempt, because `HttpClient` otherwise buffers after the pipeline
has returned and a carrier that sent its headers and then stalled would escape
the attempt timeout, the retries and the counter.

- [ ] **Step 4: Write the adapter**

`HttpCarrierGateway.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly;
using Shipping.Application.Carrier;
using Shipping.Domain.Shipments;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// The one place that knows the carrier's wire format (§3.2's anti-corruption
/// layer). Everything it returns is the port's vocabulary, and everything the
/// carrier sends is a stranger's input: bounded, translated, and never stored
/// as a link (spec, section 9).
/// </summary>
internal sealed class HttpCarrierGateway(HttpClient http, CarrierMetrics metrics, TimeProvider clock) : ICarrierGateway
{
    private const string KeyHeader = "Idempotency-Key";

    private sealed record AddressBody(string Line1, string? Line2, string City, string PostalCode, string Country);

    private sealed record BookBody(Guid ShipmentId, AddressBody Address);

    private sealed record BookAnswer(string Status, string? Reference, string? TrackingNumber, string? Code);

    private sealed record CancelAnswer(string Status, string? Code);

    // No link is declared, which is what makes "stores no URL" a property of
    // the type rather than of a line somebody could delete: System.Text.Json
    // drops what no member names.
    private sealed record EventAnswer(string? Id, string? Status, DateTimeOffset? OccurredAt);

    private sealed record EventsAnswer(IReadOnlyList<EventAnswer>? Events);

    public async Task<BookingResult> BookAsync(BookingRequest request, CancellationToken ct)
    {
        DeliveryAddress address = request.Address;
        using HttpRequestMessage message = new(HttpMethod.Post, "v1/shipments")
        {
            Content = JsonContent.Create(new BookBody(
                request.ShipmentId.Value,
                new AddressBody(address.Line1, address.Line2, address.City, address.PostalCode, address.Country)))
        };
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        // Only the two answers the wire format defines carry a body worth
        // reading; anything else is a carrier this adapter does not
        // understand, which is a fault rather than an answer.
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.UnprocessableEntity))
            throw Unavailable($"The carrier answered a booking with {(int)response.StatusCode}.");

        BookAnswer? answer = await ReadAsync<BookAnswer>(response, "a booking", ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            return answer is { Status: "refused", Code: { } code } && Recordable(code, CarrierLimits.MaxReasonLength)
                ? new BookingResult.Refused(code)
                : throw Unavailable("The carrier refused with a body that is not a refusal.");
        }

        return answer is { Status: "booked", Reference: { } reference, TrackingNumber: { } tracking }
               && Recordable(reference, CarrierLimits.MaxReferenceLength)
               && Recordable(tracking, CarrierLimits.MaxTrackingNumberLength)
            ? new BookingResult.Booked(reference, tracking)
            : throw Unavailable("The carrier booked with a body that is not a booking.");
    }

    public async Task<CancellationResult> CancelAsync(CancellationRequest request, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post, $"v1/shipments/{Uri.EscapeDataString(request.Reference)}/cancel");
        message.Headers.Add(KeyHeader, request.IdempotencyKey);

        using HttpResponseMessage response = await SendAsync(message, ct);

        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict))
            throw Unavailable($"The carrier answered a cancellation with {(int)response.StatusCode}.");

        CancelAnswer? answer = await ReadAsync<CancelAnswer>(response, "a cancellation", ct);

        // Section 6 turns each of these into a different terminal state, so a
        // body that disagrees with its status is a fault: guessing would void
        // a parcel that is moving, or leave a cancelled one on the row.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return answer is { Status: "too_late" }
                ? new CancellationResult.TooLate()
                : throw Unavailable("The carrier answered 409 with a body that is not a refusal to cancel.");
        }

        return answer is { Status: "cancelled" }
            ? new CancellationResult.Cancelled()
            : throw Unavailable("The carrier answered 200 with a body that is not a cancellation.");
    }

    public async Task<IReadOnlyList<CarrierEvent>> GetEventsAsync(string reference, CancellationToken ct)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Get, $"v1/shipments/{Uri.EscapeDataString(reference)}/events");

        using HttpResponseMessage response = await SendAsync(message, ct);

        // An answer, not a fault: the carrier has not heard of the booking
        // yet, which is ordinary between the booking and the first scan.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (response.StatusCode != HttpStatusCode.OK)
            throw Unavailable($"The carrier answered an events read with {(int)response.StatusCode}.");

        EventsAnswer? answer = await ReadAsync<EventsAnswer>(response, "an events read", ct);

        if (answer?.Events is not { } page)
            throw Unavailable("The carrier answered an events read with a body that is not a page.");

        if (page.Count > CarrierHop.MaxEventsPerPage)
            throw Unavailable($"The carrier sent {page.Count} events on one page.");

        DateTimeOffset ceiling = clock.GetUtcNow() + CarrierHop.MaxClockSkew;

        return [.. page.Select(e => Translate(e, ceiling))];
    }

    private CarrierEvent Translate(EventAnswer answer, DateTimeOffset ceiling)
    {
        if (answer is not { Id: { } id, Status: { } status, OccurredAt: { } occurredAt }
            || !Recordable(id, CarrierLimits.MaxCarrierEventIdLength))
        {
            throw Unavailable("The carrier sent an event this adapter cannot key.");
        }

        // The page is refused whole rather than partly kept: a stored instant
        // ahead of the clock outranks every real one for ever, and section 5's
        // promotion is by rank and then by time.
        if (occurredAt > ceiling)
            throw Unavailable("The carrier sent an event later than this clock allows.");

        return new CarrierEvent(id, Rank(status), occurredAt);
    }

    // A carrier adds statuses on its own schedule, so a word this platform has
    // not agreed is Unrecognised and moves nothing (spec, section 5).
    private static TrackingStatus Rank(string status) => status switch
    {
        "collected" => TrackingStatus.Collected,
        "in_transit" => TrackingStatus.InTransit,
        "delivered" => TrackingStatus.Delivered,
        _ => TrackingStatus.Unrecognised
    };

    // The adapter's own rule: a blank or over-long string records nothing, so
    // both are refused before a row is written.
    private static bool Recordable(string value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;

    private async Task<T?> ReadAsync<T>(HttpResponseMessage response, string act, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(ct);
        }
        catch (JsonException e)
        {
            throw Unavailable($"The carrier answered {act} with no JSON body.", e);
        }
    }

    // An answer the pipeline passed as a success, which this adapter cannot
    // read, is still an attempt that met a failing carrier (spec, section 11).
    // Statuses the pipeline retries, broken connections and timeouts are
    // counted inside it, and never here as well. The message never quotes the
    // body: a carrier-supplied string in a log is the link rule one layer up.
    private CarrierUnavailableException Unavailable(string message, Exception? inner = null)
    {
        metrics.Unavailable();
        return inner is null ? new(message) : new(message, inner);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.SendAsync(message, ct);
        }
        // ExecutionRejectedException is every refusal the pipeline makes on its
        // own account: a timeout, an open circuit, the concurrency limiter.
        catch (Exception e) when (e is HttpRequestException or ExecutionRejectedException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new CarrierUnavailableException("The carrier did not answer within the budget.", e);
        }

        if (CarrierAttemptCounter.IsTransient(response.StatusCode))
        {
            HttpStatusCode status = response.StatusCode;
            response.Dispose();
            throw new CarrierUnavailableException($"The carrier answered {(int)status} after every retry.");
        }

        return response;
    }
}
```

- [ ] **Step 5: Write the registration**

`Carrier/DependencyInjection.cs` is `Payments.Infrastructure.Provider`'s with
the section renamed and the breaker configured:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Shipping.Application.Carrier;

namespace Shipping.Infrastructure.Carrier;

/// <summary>
/// The carrier's registration, apart from <c>AddShippingInfrastructure</c>
/// because its scheme rule needs the host's environment, which that method is
/// not given.
/// </summary>
public static class DependencyInjection
{
    // The section is written once, so the two setting names cannot name
    // different sections; ApiKeyKey is the setting's name, never its value.
    private const string Section = "Carrier";
    public const string BaseUrlKey = $"{Section}:BaseUrl";
    public const string ApiKeyKey = $"{Section}:ApiKey";

    public static IServiceCollection AddCarrierGateway(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        // Eager, as the broker's key is: a host that cannot name its carrier
        // does not start, rather than failing its first booking.
        string? configured = configuration[BaseUrlKey];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException($"{BaseUrlKey} is not configured. Shipping cannot reach a carrier.");

        // No message below echoes the configured value: a startup failure is
        // logged, and an address can carry user information.
        if (!Uri.TryCreate(configured, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{BaseUrlKey} is not an absolute HTTP(S) address.");
        }

        // The carrier is authenticated by the key alone, and a credential in
        // the address would travel wherever the address is printed.
        if (parsed.UserInfo.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries user information; the carrier's credential is {ApiKeyKey} alone.");
        }

        // Every request resolves a relative path against the address, which
        // keeps its path and drops its query and fragment, so an address with
        // either would start clean and call a different endpoint.
        if (parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} carries a query or fragment, which no request to the carrier would keep.");
        }

        // HTTPS everywhere but Development, the rule AuthenticationExtensions
        // applies to the identity provider: the key below is a bearer
        // credential, and plain HTTP hands it to anyone on the path. The local
        // simulator is Development's, and the one plain-HTTP carrier there is.
        if (!environment.IsDevelopment() && parsed.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                $"{BaseUrlKey} names {parsed.Host} over plain HTTP outside Development; " +
                "the carrier key would travel in the clear.");
        }

        // A trailing slash, always: without one a relative request replaces
        // the base address's last segment, so a carrier at …/api would be
        // called at …/v1/shipments.
        Uri baseAddress = parsed.AbsoluteUri.EndsWith('/') ? parsed : new Uri(parsed.AbsoluteUri + "/");

        string? apiKey = configuration[ApiKeyKey];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"{ApiKeyKey} is not configured. Shipping does not call a carrier unauthenticated.");
        }

        services.AddSingleton<CarrierMetrics>();
        services.AddTransient<CarrierAttemptCounter>();
        services.AddTransient<CarrierAnswerBuffer>();

        IHttpClientBuilder client = services.AddHttpClient<ICarrierGateway, HttpCarrierGateway>(http =>
        {
            http.BaseAddress = baseAddress;
            http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        });

        // A followed 307 or 308 would replay the address to wherever the
        // carrier pointed, and take that answer as its booking. Unfollowed, a
        // redirect is a status the adapter does not define.
        client.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });

        // Separate statements: AddStandardResilienceHandler returns the
        // pipeline's builder, not the client's, so a chained
        // AddHttpMessageHandler would not compile onto the client. Added after
        // the pipeline, the counter is inside it and sees every attempt.
        client.AddStandardResilienceHandler().Configure((HttpStandardResilienceOptions options, IServiceProvider sp) =>
        {
            options.TotalRequestTimeout.Timeout = CarrierHop.TotalRequestTimeout;
            options.AttemptTimeout.Timeout = CarrierHop.AttemptTimeout;
            options.Retry.MaxRetryAttempts = CarrierHop.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = CarrierHop.RetryDelay;
            options.Retry.MaxDelay = CarrierHop.MaxRetryDelay;

            // A Retry-After replaces the backoff above and MaxDelay does not
            // cap it, so one long header would spend the total before the
            // retry CarrierHop's budget counts on.
            options.Retry.ShouldRetryAfterHeader = false;

            // The endpoint defaults never open for a loop that makes a handful
            // of calls a minute, which is what CarrierHop's own summary argues.
            options.CircuitBreaker.FailureRatio = CarrierHop.CircuitBreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = CarrierHop.CircuitBreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = CarrierHop.CircuitBreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = CarrierHop.CircuitBreakerBreakDuration;

            // An attempt timeout is the carrier's, and this is the one place
            // it arrives distinguishable from the caller cancelling.
            CarrierMetrics metrics = sp.GetRequiredService<CarrierMetrics>();
            options.AttemptTimeout.OnTimeout = _ =>
            {
                metrics.Unavailable();
                return ValueTask.CompletedTask;
            };
        });
        client.AddHttpMessageHandler<CarrierAttemptCounter>();

        // Inside the counter, so a body that breaks off or runs over is an
        // attempt it counts and the pipeline retries.
        client.AddHttpMessageHandler<CarrierAnswerBuffer>();

        return services;
    }
}
```

In `Shipping.Worker/Program.cs`, beside the infrastructure registration:
`builder.Services.AddCarrierGateway(builder.Configuration, builder.Environment);`
with the comment "§3.2's anti-corruption layer; its address is read, and its
scheme checked, eagerly." The standard handler retries `POST` by default;
that is what the keys allow, and it is argued in `CarrierHop`'s remarks rather
than switched off. `WebApplicationFactory` runs the host as Development by
default, which is what lets every test reach the in-process server over plain
HTTP.

In `Shipping.Infrastructure.csproj`, beside the existing references:

```xml
    <!-- The carrier's typed client, its budget and its breaker (§3.2's
         anti-corruption layer): the standard pipeline, configured in Carrier/. -->
    <PackageReference Include="Microsoft.Extensions.Http.Resilience" />
    <!-- IHostEnvironment, which AddCarrierGateway names in its signature for
         the scheme rule, as Common.Infrastructure references it. -->
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
```

- [ ] **Step 6: Run the adapter tests and the suite**

Run: `dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~HttpCarrierGatewayTests"`
Expected: green. The stalled test takes about nineteen seconds by design, and
the open-circuit test about four.

Then `dotnet test Platform.slnx --filter "Category!=Integration"` — green, and
`dotnet build Platform.slnx` — 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Services/Shipping tests/Shipping.TestSupport tests/Shipping.Worker.Tests
git commit -m "feat(shipping): the carrier adapter, its budget, its breaker and a per-attempt counter"
```

---

### Task 4: The meter's export, the simulator in Compose, §15.4 and docs/secrets.md

**Files:**
- Modify: `tests/Common.Web.Tests/ObservabilityTests.cs` — `Shipping.Outbound`
  joins the `Required` list; written first and seen to fail
- Modify: `src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs` —
  `.AddMeter("Shipping.Outbound")` in the service-prefixed group
- Modify: `deploy/compose/services/shipping.yml` — the `carrier-simulator`
  service, and `Carrier__BaseUrl` / `Carrier__ApiKey` on the worker with
  `depends_on: carrier-simulator: { condition: service_started }`
- Modify: `deploy/compose/README.md` — the ports table gains the simulator's
  row at 5191
- Modify: `docs/backend-architecture/15-cicd-deployment.md` — §15.4's table
  gains `Carrier__BaseUrl` and `Carrier__ApiKey`
- Modify: `docs/secrets.md` — the rotation sentence and the
  local-development exception table
- Modify: `.github/secret-scan/allowed/deploy.txt` — the entry for the local
  key in `deploy/compose/services/shipping.yml`

- [ ] **Step 1: The meter**

Add `"Shipping.Outbound"` to `ObservabilityTests`' `Required` list, after
`"Payments.Outbox"`. Run
`dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~ObservabilityTests"`
and see `Every_meter_an_alert_reads_from_is_collected` fail. Then add the line
to `ObservabilityExtensions`, beside Payments':

```csharp
                .AddMeter("Shipping.Outbound")                     // §3.2's carrier, and the address read
```

and see it pass. The test holds its own copy of the list on purpose, which is
why the assertion is written before the registration and not with it.

- [ ] **Step 2: The Compose unit**

In `services/shipping.yml`, beside the migrator and the worker:

```yaml
  # §3.2's carrier, simulated (spec section 9). Shipping's own dependency,
  # so it lives in Shipping's unit rather than the shared baseline, and
  # docker-compose.infra-only.yml keeps it, since a Shipping host run from an
  # IDE needs it as much as a containerised one. The tag is
  # Directory.Packages.props' WireMock.Net pin: one engine, one version, one
  # set of mappings for this container and the tests.
  carrier-simulator:
    image: sheyenrath/wiremock.net:2.12.0
    # The image does not read static mappings by default; without this flag
    # every request answers 404 and spec section 9's mappings never load.
    command: [ "--ReadStaticMappings", "true" ]
    volumes:
      - ../carrier-simulator/mappings:/app/__admin/mappings:ro
    ports: [ "127.0.0.1:5191:80" ]
```

On the worker's environment:

```yaml
      Carrier__BaseUrl: "http://carrier-simulator/"
      # §14.1's local-development exception: the simulator ignores it.
      Carrier__ApiKey: "local-dev-carrier"
```

and under its `depends_on`,
`carrier-simulator: { condition: service_started }`.
`docker-compose.infra-only.yml` gains nothing: the simulator is infrastructure
for a host-run worker, exactly as `psp-simulator` is.

The ports table in `deploy/compose/README.md` gains, after the PSP row:

```markdown
| Carrier simulator | http://localhost:5191 | `/__admin/mappings` — the scripted postal codes are in [`carrier-simulator/README.md`](carrier-simulator/README.md) |
```

Then the scan and its suite:

```bash
py -3.12 -m unittest discover -s .github/secret-scan
py -3.12 .github/secret-scan/secret_scan.py
```

The local key is flagged. Add the entry to
`.github/secret-scan/allowed/deploy.txt` with the fingerprint the gate prints,
in the file's existing four-column form, reading "Section 14.1's local default
for the simulated carrier's API key; the simulator ignores it." Run both
again, both exit 0. The same key printed in this plan's own text is a finding
under `.github/secret-scan/allowed/docs.txt`, owed by whichever pull request
commits the plan file.

- [ ] **Step 3: §15.4's two rows**

Add both rows to §15.4's configuration table in its existing column form,
after `PaymentProvider__ApiKey`:

```markdown
| `Carrier__BaseUrl` | Config | ConfigMap | ✓ — **Shipping only**; the carrier's address, and the host refuses to start without it |
| `Carrier__ApiKey` | Secret | External Secrets | ✓ — **Shipping only**; the carrier's credential, and the host refuses to start without it |
```

- [ ] **Step 4: `docs/secrets.md`'s two sentences**

In *Rotation*, the sentence naming the credentials a running host holds gains
the carrier's beside `PaymentProvider__ApiKey`: "and `Carrier__ApiKey`, for
Shipping's carrier behind §3.2's anti-corruption layer". The paragraph's own
note that a rule about who holds a credential is falsified by the next host
that holds one is already there and needs nothing.

In *Local development is a deliberate exception*, the table gains a row:

```markdown
| Carrier key | `local-dev-carrier` |
```

and the paragraph below it, which says the provider key has no seam because
the simulator ignores it, gains the carrier's key in the same sentence rather
than a second sentence saying the same thing twice.

The five places: Compose is Step 2, §15.4's row is Step 3, the fixture is
Task 3's `ShippingWorkerFactory`, §14.2's Aspire host is not adopted, and Helm
values arrive with Shipping's chart in PR-7, which is where the spec puts the
`carrier` capability.

Run `/check-links` and `/validate-blueprint`.

- [ ] **Step 5: Prove the simulator under Compose**

```bash
docker compose -f deploy/compose/docker-compose.yml up --build --wait
curl -s -X POST http://localhost:5191/v1/shipments -H "Idempotency-Key: book:probe" \
    -H "Content-Type: application/json" \
    -d '{"shipmentId":"00000000-0000-0000-0000-000000000001","address":{"line1":"1 Abay Avenue","line2":null,"city":"Almaty","postalCode":"SIM-REFUSED","country":"KZ"}}'
curl -s -X POST http://localhost:5191/v1/shipments -H "Idempotency-Key: book:probe" \
    -H "Content-Type: application/json" \
    -d '{"shipmentId":"00000000-0000-0000-0000-000000000002","address":{"line1":"1 Abay Avenue","line2":null,"city":"Almaty","postalCode":"050000","country":"KZ"}}'
curl -s http://localhost:5191/v1/shipments/crr_SIM-OK/events
curl -s -X POST http://localhost:5191/v1/shipments/crr_SIM-LATE/cancel -H "Idempotency-Key: cancel:probe"
docker compose -f deploy/compose/docker-compose.yml down -v
```

Expected: the first answers
`{"status":"refused","code":"address_not_serviceable"}`, the second
`{"status":"booked","reference":"crr_SIM-OK","trackingNumber":"TRK-SIM-OK"}`,
the third a page of `collected` then `delivered`, the fourth
`{"status":"too_late","code":"already_collected"}`, and `shipping-worker` is
healthy.

- [ ] **Step 6: Commit**

```bash
git add src/BuildingBlocks/Common.Web tests/Common.Web.Tests deploy/compose \
    docs/backend-architecture/15-cicd-deployment.md docs/secrets.md .github/secret-scan/allowed
git commit -m "feat(shipping): the simulator runs beside the worker, its meter is exported, and §15.4 names both keys"
```

---

### Task 5: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings.
- [ ] `dotnet test Platform.slnx` — green, with a Docker daemon running.
- [ ] `py -3.12 -m unittest discover -s .github/secret-scan` then
  `py -3.12 .github/secret-scan/secret_scan.py` — both exit 0.
- [ ] `git fetch origin main` then
  `py -3.12 .github/comment-gate/comment_gate.py --base origin/main` — exit 0;
  the gate takes the base branch as a required argument, and no comment block
  added here runs past ten lines, names a pull request or a test, or stresses
  a word.
- [ ] `git ls-files --eol deploy/compose/carrier-simulator` reports `lf` for
  every file, so the declaration in `.gitattributes` took effect.
- [ ] PR body: `| Class | A+D+E |`, the touch set as paths alone with the
  reasons under the table, and Task 4's `curl` answers as evidence. Then
  `/ship`.

## Self-review

**Spec coverage.**

- Section 1, the carrier is a WireMock.Net simulator behind a typed
  `HttpClient` adapter → Tasks 2 and 3.
- Section 3's PR-2 row — the port, the adapter, `CarrierHop`'s numbers, the
  translation and its bounds, the counter and its `AddMeter` line, the
  simulator with its line endings declared, §15.4's two rows → Tasks 1–4.
- Section 4's `book:{ShipmentId}` and `cancel:{ShipmentId}` → Task 1, asserted
  by `The_keys_are_the_shipment_and_differ_between_the_two_acts` and again on
  the journal in Task 3.
- Section 9's answer table, row by row → Task 3: 201, 422, 200, 409, 404,
  the transient set including an open circuit, and a body that is not the
  agreed shape. The stranger's-input rules — the body bound, every kept
  string bound, the refused future timestamp, `Unrecognised`, and no stored
  URL — each have a test, and the last has one on the type as well as on an
  answer.
- Section 9's `CarrierHop`, breaker included and asserted able to open →
  Task 3, in `CarrierFaultTests` with a host per test, because a breaker
  sized to open cannot sit behind a shared fixture.
- Section 9's simulator and its postal-code script → Task 2, every row of the
  table a mapping and every mapping exercised by Task 3 or by Task 4's
  `curl`.
- Section 10's `Carrier__BaseUrl` and `Carrier__ApiKey`, and the four of
  `docs/secrets.md`'s five places this PR can reach → Task 4.
- Section 11's meter, its first instrument and the `AddMeter` line → Tasks 3
  and 4; `shipping.address.refused` and `shipping.shipments.waiting` arrive
  with PR-5's address adapter and PR-6's gauge, and `CarrierMetrics`' remarks
  say so.
- Section 12's adapter suite, the inequality, the opened circuit and the
  hostile page → Task 3.
- Section 13's §15.4 rows → Task 4.

**Type consistency.** `ICarrierGateway`, `BookingRequest`, `DeliveryAddress`,
`BookingResult.Booked/Refused`, `CancellationRequest`,
`CancellationResult.Cancelled/TooLate`, `CarrierEvent`,
`CarrierUnavailableException` and `CarrierLimits` are produced by Task 1 and
consumed by Task 3 under those spellings; `CarrierHop`, `CarrierMetrics.
MeterName`, `DependencyInjection.BaseUrlKey`, `DependencyInjection.ApiKeyKey`,
`SimulatorMappings.Directory()` and `ShippingWorkerFactory`'s two new
parameters are produced by Task 3 and read by Tasks 3 and 4.
`UnavailableCounter.Of` and `UnavailableCount` are produced by Task 3 and read
by both carrier suites in the same task, one passing the class fixture's
provider and the other its per-test host's. `TrackingStatus` and `ShipmentId`
are PR-1's and are only consumed.

**Left to a later PR.**

- Any caller of the port: the fulfilment worker, the address read and the
  cancel are PR-5's, the tracking poll and the two events PR-6's.
  `CarrierHop.FulfilmentTick` and `TrackingPollInterval` are declared here
  because the budget and the intervals are one class, and nothing reads them
  yet.
- `IDeliveryAddressSource`, `AddressHop` and `shipping.address.refused` are
  PR-5's; `DeliveryAddress` is declared here because the booking needs one,
  and PR-5's address port returns this record rather than a second one.
- `shipping.shipments.waiting` is PR-6's gauge over rows past their first
  backoff.
- The `carrier` capability in the library chart, `Carrier__BaseUrl` and
  `Carrier__ApiKey` in `values.yaml`, and the canary row are PR-7's, which is
  where `docs/secrets.md`'s Helm place is met.
- Every worker-shaped test section 12 lists — the lease inequality, two
  overlapping passes, a pass that throws, the crash between the answer and
  the commit, both interleavings of section 6, the Kazakh-script round trip
  and the log export — belongs to the PR that builds the worker it is about.

# Retry-safe writes — a command id bound to its request, and a rule every write endpoint answers

Design spec, frozen at write time (2026-10-01, against `main` at `0ad41c02`).
Dated and named for its subject, as this folder's specs have been since PR-15.

**The request.** A client sends a write, the service applies it, and the answer
is lost. The client sends it again. The second arrival must be recognised as
the first one repeated and answered with the first one's result, without
running the work again; a request that only *looks* like a repeat must not be.
The contract asked for is the common one: an operation key, bound to a
fingerprint of the request and to the stored result; the first handler
reserves the key atomically; the same key with the same content replays; the
same key with different content is a 409; and the contract covers the
in-flight answer, the retention window and external effects. It is to hold for
every existing service and for every service the scaffold produces.

## What exists, and is kept

§8.5 already builds most of that contract, and none of it is redesigned here.

| The contract's part | Where it lives today |
|---|---|
| Operation key | `IIdempotentCommand.CommandId`, keyed `{subject}:{operation}:{commandId}` by `IdempotencyBehavior` |
| Atomic reservation | `IIdempotencyStore.TryClaimAsync`, a `SET NX` in `RedisIdempotencyStore` |
| Stored result, replayed | `IIdempotencyStore.CompleteAsync` and `IdempotencyBehavior.Replay` |
| In-flight answer | `ConcurrentRequestException`, 409 `request.in_progress` |
| Retention | `IdempotencyRetention.Window` for the claim; the durable marker beyond it (ADR-037, ADR-038, ADR-039) |
| Result gone, work durable | `CommandAlreadyCommittedException`, 409 `command.already_committed` |
| External effects once | the outbox row and the marker commit in the command's own transaction (§9.4, ADR-037); a provider call carries a key derived from the aggregate |

## The two gaps

**1. The key is not bound to the request.** `IdempotencyBehavior` replays
whatever is stored under the key and never asks whether the request is the one
that produced it. A client that reuses a `CommandId` for a *different* request
is answered 200 with the earlier request's result, and believes its new
request was applied. That is a success-shaped answer to work that never ran.

**2. Nothing makes a write endpoint take the decision.** The only gate,
`IdempotencyOptInTests`, checks that a command *carrying* a `CommandId`
declares `IIdempotentCommand`. A new POST that carries none is unprotected and
no test notices. Measured on the tree: two commands opt in (`PlaceOrder`,
`PublishProduct`); four HTTP writes do not (`CancelOrder`,
`ReleaseReservation`, `ReinstateReservation`, `SetOnHand`), and only one of
them says why, in a comment.

## Decision 1 — the result is stored with the fingerprint of the command that produced it

**What is hashed.** The command as the pipeline sees it: SHA-256 over
`JsonSerializer.SerializeToUtf8Bytes(command, options)` with `TValue` bound to
`TCommand` — the `Type`-taking overload is CA2263 under ADR-019 — in
lower-case hex. The command and not the HTTP body, for two reasons: §4.2
keeps HTTP out of `Common.Application`, where the behaviour runs, and a route
value is part of the request's content while it is no part of its body.

**The options are one `static readonly JsonSerializerOptions`** with
`DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault` and nothing
else changed. Omitting defaults is what lets a command gain an optional field
without changing the fingerprint of a request that does not send it, so a
retry that straddles a deploy still matches.

**Where it is computed.** A new `Common.Application/CommandFingerprint.cs`:
`internal static class CommandFingerprint` with
`static string Of<TCommand>(TCommand command)`. `IdempotencyBehavior` calls it
once, before the claim.

**Where it is stored.** In the payload `IdempotencyBehavior` already owns.
`Capture` writes `sha256:{fingerprint}:{json}`; `Replay` reads it back. The
store, its port, the Redis scripts and the marker row are **unchanged**:
`IIdempotencyStore` keeps its five members and its signatures, and no service
needs a migration.

**What the behaviour does on a held key**, in this order:

| The entry | The answer |
|---|---|
| absent, or in progress | `ConcurrentRequestException` — unchanged |
| completed, payload without the `sha256:` prefix | replay — an entry the previous release wrote; it carries no fingerprint to compare |
| completed, fingerprint equal (ordinal) | replay — unchanged |
| completed, fingerprint different | `CommandIdReusedException` |

A payload is recognised as the new shape by its first seven characters. No
JSON value begins with `s`, so no stored result can spell the prefix.

**The refusal.** `Common.Application/CommandIdReusedException.cs`, shaped as
`ConcurrentRequestException` is: `(Guid commandId)`, the id kept for the log
line only. `Common.Web/CommandIdReusedExceptionHandler.cs` answers 409 with
`code` `command.id_reused` and a `detail` that says the identifier was already
used for a different request and a changed request needs a new one. It is
registered in `ProblemDetailsExtensions` beside the other three. It is the
second 409 that does not say *retry*.

**What this does not do, and why.**

- *An in-flight duplicate with different content is told `request.in_progress`,
  not `command.id_reused`.* The fingerprint is recorded with the outcome, so
  there is nothing to compare until the first attempt completes. The client's
  retry then meets the mismatch. Recording it at the claim would change
  `TryClaimAsync`, `IdempotencyEntry`, both Redis scripts and every
  implementer of the port across five services' tests, to move one answer one
  retry earlier.
- *The marker row carries no fingerprint.* Once the claim has expired, any
  reuse of the key is refused with `command.already_committed` whatever its
  content, which is already a refusal. A column would be a migration in every
  service for no second outcome.
- *A refused command stores nothing*, as today: a failed `Result` releases
  the claim, so the same id may carry a corrected request.

**Residuals, stated so that nobody finds them later.**

- Removing or renaming a field of an idempotent command changes the
  fingerprint of requests already answered, so a retry that straddles that
  deploy is refused as reused. **The shape of an idempotent command is a
  compatibility surface**, as §8.5 already says of its result.
- The fingerprint is of the bound command, so two bodies that bind to equal
  commands match, and two that bind differently do not — a reordered list of
  lines is a different request. A retry re-sends the bytes it sent.
- During a rolling deploy, a replica on the previous release that meets a
  new-shape payload of a command that returns a value fails the replay with a
  500, because it deserialises the prefix; a void command's replay never reads
  the payload and is unaffected. Nothing is applied twice; the retry succeeds
  once the rollout completes.
- A command `System.Text.Json` cannot serialise now fails before the claim,
  where it used to run; and two numerals that bind to decimals of different
  scale, `10` and `10.0`, fingerprint differently.

**Rule record.** ADR-057, *A command id is bound to the fingerprint of the
command that claimed it*. It amends §8.5. §10.5's error table gains the row
for `command.id_reused`, because that table owns the status mapping.

## Decision 2 — every write endpoint is keyed, or declares why a repeat is harmless

**The rule.** An endpoint a POST, PUT, PATCH or DELETE reaches is exactly one
of:

1. **keyed** — it dispatches an `IIdempotentCommand`; or
2. **declared retry-safe** — it carries a `RetrySafety` kind.

An endpoint that is neither, or both, fails the build.

**The declarations**, in a new `Common.Web/RetrySafetyExtensions.cs`:

```csharp
public enum RetrySafety
{
    Convergent,
    ReadOnly
}

public sealed record RetrySafetyMetadata(RetrySafety Kind);

public sealed record IdempotentCommandMetadata(Type Command);

public static class RetrySafetyExtensions
{
    public static TBuilder RetrySafe<TBuilder>(this TBuilder builder, RetrySafety kind)
        where TBuilder : IEndpointConventionBuilder;

    public static RouteHandlerBuilder Idempotent<TCommand>(this RouteHandlerBuilder builder)
        where TCommand : IIdempotentCommand;
}
```

- `Convergent`: a repeat of the same request leaves the state the first one
  left and is answered as the first one was.
- `ReadOnly`: the endpoint writes nothing.
- An endpoint is **keyed** when its handler takes an `IIdempotentCommand` as a
  parameter — which the existing authentication gate already selects on — or
  when it carries `IdempotentCommandMetadata`, for an endpoint that builds the
  command from a route value and a request record.
- `RetrySafe` is generic over the builder because a gRPC service is declared
  on the builder `MapGrpcService` returns.

**The gate**, once, in `tests/Common.TestSupport/WriteEndpointRule.cs`:

```csharp
public static class WriteEndpointRule
{
    public static IReadOnlyList<Endpoint> Writes(IEnumerable<Endpoint> endpoints);
    public static IReadOnlyList<Endpoint> Unrestricted(IEnumerable<Endpoint> endpoints);
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints);
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints, Assembly application);
}
```

`Offenders` names every write endpoint that is neither or both, or that
declares two kinds; and a keyed endpoint that allows anonymous callers or
carries no `IAuthorizeData` (§8.5's subject rule, extended to the metadata
form). The overload taking the host's application assembly adds a keyed
command no endpoint reaches, compared both ways so that a scan of the wrong
assembly is named rather than empty. Each host's test project gets a
`WriteEndpointRuleTests` with two facts: `Offenders` is empty, and `Writes`
and `Unrestricted` are exactly what the host is known to map — the floor,
because an offender list is as green over an empty selection.

**An endpoint that names no method accepts every verb**, and the endpoint
table has three kinds of them: §13.5's probes, gRPC's unimplemented-method
fallbacks, and anything a host maps with a bare `Map`. Measured, the first two
are a bare `RequestDelegate` with no handler `MethodInfo`. So an endpoint that
names no method is a write when it has a handler and `Unrestricted` when it
has none, and the floor names every unrestricted one, which is what makes a
route a host adds in that shape fail a test instead of passing unread.

**Three project references are drawn**, all under `tests/`:
`Common.TestSupport` to `Common.Web` for the two metadata types;
`Web.Bff.Tests` to `Common.TestSupport`, because the BFF has no service
fixture; and `Common.Web.Tests` to `Common.TestSupport`, where the gate's own
suite lives, since `Common.TestSupport` is not a test project.

**The hosts under the gate** are the ones that map handlers: Catalog,
Ordering, Inventory, Payments and the BFF. The gateway maps a reverse proxy
and no handler of its own; Shipping's worker exposes no API and serves §13.5's
probes alone. Both exclusions are stated in ADR-058 rather than left as an
absence.

**gRPC is inside the gate.** A gRPC method is a POST in `EndpointDataSource`.
`MapGrpcService<PricingService>()` and
`MapGrpcService<DeliveryAddressService>()` both serve reads and are declared
`.RetrySafe(RetrySafety.ReadOnly)`. The declaration reaches every method of
the service, one added later included, so the floor names each method.

**Classification of every write endpoint on the tree.**

| Endpoint | Host | Decision | Why |
|---|---|---|---|
| `PlaceOrder` | Ordering | keyed, already | binds `PlaceOrderCommand` |
| `PublishProduct` | Catalog | keyed, already | binds `PublishProductCommand` |
| `ReinstateReservation` | Inventory | **becomes keyed** | Decision 3 |
| `CancelOrder` | Ordering | `Convergent` | `Order.Cancel` returns on a cancelled order, and cancelled is terminal |
| `ReleaseReservation` | Inventory | `Convergent` | a release of a released reservation gives nothing back; the command also arrives by message, where §9.5's inbox is its deduplication |
| `SetOnHand` | Inventory | `Convergent` | a PUT of an absolute value |
| `Quote` | BFF | `ReadOnly` | prices a basket and writes nothing |
| `PricingService` (gRPC) | Catalog | `ReadOnly` | |
| `DeliveryAddressService` (gRPC) | Ordering | `ReadOnly` | |

Payments maps one GET; its floor is that `Writes` is empty and its endpoint
table is not.

**`Convergent` has a limit, and the ADR names it.** A repeat that arrives
*after a different write* is not a repeat of the present state: a stale
`SetOnHand` re-applies an old count, and a stale release undoes a
reinstatement. `Convergent` is the right declaration where that interleaving
cannot occur or is an operator's deliberate act; where a stale repeat would
undo a later write that matters, the endpoint is keyed instead. `CancelOrder`
has no such interleaving. `ReleaseReservation` and `SetOnHand` are operator
acts behind `InventoryPermissions.Admin`, and the residual is accepted for
them and written down.

**Message-borne commands are outside this rule.** A command reached only
through a consumer is deduplicated by §9.5's inbox, and §8.5 already makes a
broker-only idempotent command a failure of the authentication gate by design.

**The scaffold.** `tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs` is a
Catalog file, so `tools/new-service` renders it and a scaffolded service is
born under the rule. Catalog's floor names `PublishProduct` and a gRPC method
a rendered host does not map, so the scaffold patches the copy's floor as it
already patches `IdempotencyOptInTests`'. It cannot omit a file per host
shape, so a rendered worker carries the suite too.

**Rule record.** ADR-058, *A write endpoint is keyed or declares why a repeat
is harmless*. It amends §8.5's opening rule, which says every non-idempotent
write command carries a `CommandId` and has had nothing behind it.

## Decision 3 — `ReinstateReservation` becomes keyed

A reinstatement whose answer is lost is retried into
`ReservationErrors.NotReinstatable`: the reservation is `Reserved` now, so the
repeat is refused for work that succeeded. That is neither convergent nor
read-only, so the endpoint is keyed.

- `ReinstateReservationCommand(Guid CommandId, Guid OrderId)
  : ICommand<Result>, IIdempotentCommand`, `OperationName`
  `"inventory.reservation.reinstate"`.
- A new `ReinstateReservationValidator` requires `CommandId` to be non-empty,
  as `PlaceOrderValidator` does, so an omitted id is a 400 before any claim.
- The wire: `POST /v1/inventory/reservations/{orderId}/reinstate` with body
  `{ "commandId": "<guid>" }`, bound to a
  `ReinstateReservationRequest(Guid CommandId)` record beside the endpoint.
  The route value stays in the route.
- Inventory holds inverted floors for the day the service gains its first
  idempotent command, in `IdempotencyOptInTests` and in
  `Inventory.Api.Tests/IdempotencyMarkerTests`; they flip to the form
  Ordering's have.
- `docs/runbooks/order-review.md` names the act and shows no request, so it
  does not move.
- A body that does not bind at all — absent, or `commandId` not a GUID — is a
  400 in `Production` and a 500 in `Development`, as it already is for every
  bound body on `main`: `BadHttpRequestException` has no handler in
  `Common.Web`. That is a defect of its own and is not fixed here.

This is a breaking change to one admin-only endpoint. Its one known caller is
the `blueprint-admin` console.

## Delivery

Three pull requests in `blueprint-backend`, each with its own plan.

| | Subject | Class | Depends on |
|---|---|---|---|
| **PR-A** | Decision 1: `CommandFingerprint`, `CommandIdReusedException`, its handler, ADR-057, §8.5 and §10.5 | C | — |
| **PR-B** | Decision 3: `ReinstateReservation` keyed | A (Inventory) | — |
| **PR-C** | Decision 2: `RetrySafetyExtensions`, `WriteEndpointRule`, every host's declarations and `WriteEndpointRuleTests`, the scaffold, ADR-058, §8.5 | C | PR-B merged; PR-A merged before its docs task |

PR-A and PR-B are independent and may run in parallel. PR-C needs PR-B, or
`ReinstateReservation` has no honest declaration to make; and it needs PR-A
before its docs task, because both amend §8.5 and both append a row to
Appendix A. ADR numbers are the
next free ones at write time; a number taken meanwhile moves to the next, by
`/new-adr`'s rule.

**Clients, each in its own repository and its own plan, after PR-A and PR-B.**

- `blueprint-frontend`: `core/errors/error-mapper.ts` learns
  `command.id_reused`. Its fallback for an unread 409 is already the kind that
  forbids a retry, so the client is safe before this lands and merely less
  exact.
- `blueprint-admin`: the console sends a `commandId` when reinstating.

## Testing

- **`CommandFingerprint`**: equal commands hash equal; one changed field
  hashes different; a defaulted optional field hashes as its absence does.
- **`IdempotencyBehavior`**: same key and same command replays; same key and
  a different command throws `CommandIdReusedException` and does not run the
  handler; a planted payload of the previous shape (`null`, a quoted GUID)
  replays; the stored payload carries the prefix; an in-flight duplicate with
  different content is still `ConcurrentRequestException`.
- **`CommandIdReusedExceptionHandler`**: 409, `code`, no key in the body.
- **End to end, Ordering**: one `CommandId`, two different baskets — the
  second is 409 `command.id_reused` and one order exists.
- **Inventory**: a reinstatement repeated under one id answers as the first
  did and takes the stock once; an omitted id is a 400.
- **`WriteEndpointRule`**: an undeclared write, a write declared twice, an
  anonymous keyed endpoint and an unreachable keyed command are each named;
  each host's floor fails on an empty selection.

## Out of scope

- An `Idempotency-Key` header. §8.5 leaves binding one at the boundary open
  and no endpoint does.
- Recognising a repeat that arrives under a *new* id. A client that loses its
  id has sent a new request by this contract; keeping the id is the client's
  half, and `blueprint-frontend`'s `CommandIdentity` is where it lives.
- Keying `CancelOrder`, `ReleaseReservation` or `SetOnHand`.

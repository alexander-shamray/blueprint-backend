# Retry-safe writes PR-B — ReinstateReservation becomes keyed — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Inventory's `ReinstateReservation` a keyed write — its command
carries a client-generated `CommandId` and declares `IIdempotentCommand` — so
that a reinstatement whose answer was lost is replayed instead of being
refused as `reservation.not_reinstatable`.

**Architecture:** The command opts into §8.5's mechanism as it stands on
`main`, and nothing in the mechanism changes: `IdempotencyBehavior` claims
`{subject}:inventory.reservation.reinstate:{commandId}` before the handler,
§6.3's `TransactionBehavior` writes the marker row in the reinstatement's own
transaction, and a repeat under the same key is answered with the recorded
success. The endpoint keeps the order in the route and binds the id from a
`ReinstateReservationRequest` body, as Ordering's `/cancel` binds
`CancelOrderRequest`, and a new validator refuses the empty id before any
claim. `ReinstateReservationHandler` is not edited.

**Tech Stack:** ASP.NET Core minimal APIs, FluentValidation, the
`Common.Application` pipeline (`ValidationBehavior`, `IdempotencyBehavior`,
`TransactionBehavior`), the Redis-backed `IIdempotencyStore` and the
`inventory.IdempotencyMarkers` table Inventory already has; xUnit v3,
Shouldly, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-01-retry-safe-writes-design.md` —
*Decision 3*, the `ReinstateReservation` row of Decision 2's classification
table, the PR-B row of *Delivery*, and the *Inventory* line of *Testing*.
Decision 1 is PR-A's and Decision 2 is PR-C's; neither is planned here.

**Measured on:** the code and every expected output below were prototyped in
a scratch clone of `main` at `b07cd0b2`. `main` stood at `96ef48c2` when this
was written, and no path under `src/Services/Inventory` or `tests/Inventory.*`
differs between the two. A test count below is a measurement on that commit:
on a later `main` the number may have moved, and what has to hold is
`Failed: 0` and the named tests.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan; and where a
  document and a green gate disagree about a fact, the gate is right
  (`docs/change-locality.md` §1).
- **Class A**, for Inventory's slice. The PR body's two rows, exactly:

  ```markdown
  | Class | A |
  | Touch set | `src/Services/Inventory/**`, `tests/Inventory.*` |
  ```

  Paths only, comma-separated, one backticked glob per item, no prose inside
  the cell and no trailing full stop: the gate refuses a row that carries
  anything else. Why each: `src/Services/Inventory/**` holds the command, its
  new validator, and the endpoint with its request record;
  `tests/Inventory.*` holds `Inventory.Application.Tests` and
  `Inventory.Api.Tests`, where the floors, the authentication gate and the
  end-to-end tests live. This row was run through
  `.github/locality-gate/locality_gate.py` with the thirteen paths this plan
  edits and passed both of its checks.
- **The runbook is not in the touch set, because it shows no request.** The
  spec keeps `docs/runbooks/order-review.md` out, since it shows no request;
  measured, the file names the act twice, at lines 501 and 503 ("ends in
  **reinstating** a reservation", "nothing to reinstate"), and nowhere states
  a path, a `curl` or a body. Task 1 repeats the measurement. If the file
  shows the request by then, the row gains `docs/runbooks/order-review.md`
  with that reason beside it — still Class A, whose tree set in
  `.github/locality-gate/classes.yml` holds `docs/runbooks/**` — and the line
  that shows the request gains the body.
- **No per-service mutex is needed**: `Program.cs`, the `DbContext` and its
  snapshot, every `.csproj` and `tests/Inventory.TestSupport/ServiceFixture.cs`
  are untouched, so the touch-set row names none.
- **No new package, no migration, no registration.** Verified on the tree:
  `AddInventoryApplication` registers `IdempotencyBehavior<,>` between
  validation and the transaction and registers `IdempotencyContext`;
  `AddValidatorsFromAssemblyContaining<SetOnHandValidator>()` scans the new
  validator in; `AddInventoryInfrastructure` calls `AddRedisConnections`,
  which registers `RedisIdempotencyStore`, and registers
  `EfIdempotencyMarkerStore`; migration
  `20260918071057_AddIdempotencyMarkers` created
  `inventory.IdempotencyMarkers`; `deploy/compose/services/inventory.yml` and
  `deploy/helm/inventory/values.yaml` already give the host both Redis
  connections; and Inventory's `ServiceFixture` already starts real Redis
  (`redis: true`).
- **Nothing PR-A or PR-C introduces is used**: no `CommandFingerprint`, no
  `CommandIdReusedException`, no `.Idempotent<TCommand>()`, no
  `.RetrySafe(...)`, no `WriteEndpointRule`. This PR lands before PR-C and is
  independent of PR-A.
- **`.cs` files are CRLF** (`.gitattributes`), the Write tool emits LF, and an
  LF `.cs` file can fail the build with one IDE0055 per line. Run
  `unix2dos -q` on every `.cs` file created, and on an edited one whose
  endings the check below reports as anything else; it leaves a CRLF file as
  it is. The check is `git ls-files --eol -m -o --exclude-standard`, where
  every `.cs` line must read `w/crlf`. **Never run `sed -i` on a `.cs` file
  under Git Bash**: it rewrites the file with LF endings, measured while
  prototyping this plan.
- `py -3.12`, never `python`.
- Code obeys `docs/style-guide.md` as written below, so paste it and do not
  reflow it: file-scoped namespaces, explicit local types except where the
  right-hand side names the type, 120 columns, no column of `=` or `=>`, and a
  comment that says why and cites its owner — a section or a symbol, never a
  pull request, a review or a test — inside the budget (a summary is one
  sentence, a block five lines).
- Work on a branch `/branch` creates, in a worktree under
  `.claude/worktrees/`. Never commit on `main`. Before every commit, run
  `git branch --show-current` and stop if it prints `main`.
- **This is a breaking change to one admin-only endpoint.** Its one known
  caller is the `blueprint-admin` console, a separate repository with its own
  plan, out of this one. Between this PR's merge and the console's change, a
  reinstatement sent with no body is refused: 400 in a deployed host, 500
  under `Development` (Review Focus, line 1).
- **Not touched**: `src/BuildingBlocks/**`, `tests/Common.*`, any other
  service or host, `CLAUDE.md`, every chapter, appendix and ADR under
  `docs/backend-architecture/`, `deploy/**`, `tools/new-service/**`, and
  `docs/superpowers/specs/`. A stale restatement met on the way is left where
  it is (`docs/change-locality.md` §2).
- `Inventory.Api.Tests` starts SQL Server, RabbitMQ and two Redis containers,
  so it needs a running Docker daemon; `Inventory.Application.Tests` and the
  two `Inventory.Api.Tests` classes built on `HostSmokeTests`' unreachable
  hosts do not.
- Every test is written before the code it covers. Three of Task 2's tests
  pass before and after, and the task names them and says why.
- A commit message goes in through `git commit -F -` and a quoted heredoc,
  as each commit step spells it, so its body keeps its line breaks. If the
  harness refuses that form, save the same text to a file in the session's
  scratchpad directory, never in the worktree, and pass the file to `-F`.
  Append the attribution trailer the session's own instructions give.

## Review Focus

Five inputs or failure modes the spec implies and no required test reaches.
Each was measured in the scratch clone; each is pinned by a test below or
carries the reason it is not.

1. **A request with no body, an empty body, `"commandId": null`, or a
   `commandId` that is not a GUID.** Binding refuses it before the dispatcher
   is called, so nothing is claimed and nothing is written. A deployed host
   answers 400, and Task 2's `ReinstateReservationRequestTests` pins that on a
   `Production` host whose every store is unreachable. **Under `Development`
   — the test host's default and Compose's setting — the same four bodies
   answer 500.** `RouteHandlerOptions.ThrowOnBadRequest` defaults to true
   there, so the refusal is raised as a `BadHttpRequestException` carrying
   status 400, it reaches `UseExceptionHandler`, and none of the four handlers
   `AddCommonProblemDetails` registers translates it. The same is true on
   `main` today of `SetOnHand` (`{"onHand":"three"}` answers 500 under
   `Development` and 400 under `Production`), so it is the platform's and not
   this endpoint's; the fix is a handler in `Common.Web`, which is Class B
   and in no plan under this spec. It is not pinned at 500 here: a test that
   asserts a defect is a test its fix has to edit in another slice.
2. **The same `commandId` reused for a different `orderId`.** On today's
   mechanism the second request answers 204 and reinstates nothing: the key
   is `{subject}:{operation}:{commandId}`, the order is no part of it, and
   `IdempotencyBehavior` replays whatever is stored under a held key. That is
   the spec's first gap, and PR-A changes the answer to 409
   `command.id_reused`, because its fingerprint is of the command and
   `OrderId` is a field of the command. No test pins today's answer here: PR-A
   would have to edit it outside its own touch set, in whichever order the
   two merge.
3. **A repeat after the reservation was released again.** Reinstate under an
   id, release, reinstate under the same id: the third request answers 204
   from the record and the reservation stays `Released`. This is the contract
   and not a gap — the id names one act, and an equal command has an equal
   fingerprint, so PR-A does not change it. Pinned in Task 2 by
   `A_command_id_names_one_act_so_its_repeat_after_a_later_release_retakes_nothing`.
   The console's half is that a new act takes a new id, and only a retry of a
   lost answer re-sends one.
4. **A different admin sending the same id.** The key's first segment is the
   caller, so a second admin's request under the first one's id runs the
   handler and is answered 422 `reservation.not_reinstatable` — the answer
   every repeat had before this PR. A retry taken over by a colleague is
   therefore not recognised as one. Pinned in Task 2 by
   `Another_admin_sending_the_same_command_id_is_judged_on_the_reservation_and_not_replayed`.
   No plan changes it: it is §8.5's subject rule.
5. **The coordination Redis unreachable.** Before this PR a reinstatement
   needed SQL Server alone. After it the claim comes first, so with Redis
   unreachable `TryClaimAsync` throws `RedisConnectionException` once
   StackExchange.Redis's backlog timeout runs out and the answer is 500 with
   no handler run — measured at 500 after six seconds on the unreachable
   host. That is §8.1's fail-closed rule doing its work, and `/health/ready`
   already reports `redis-coordination`, so such a pod is out of rotation. No
   test pins it: the one host with no Redis has no SQL Server either, so a
   500 there cannot say which store refused. No plan changes it.

## File Structure

| File | | Responsibility |
|---|---|---|
| `src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationCommand.cs` | modify | The command: gains `CommandId`, declares `IIdempotentCommand` and its `OperationName` |
| `src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationValidator.cs` | create | Refuses the empty `CommandId` before any claim |
| `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs` | modify | The reinstate endpoint binds `ReinstateReservationRequest`; the record is declared beside it |
| `tests/Inventory.Application.Tests/IdempotencyOptInTests.cs` | modify | Two inverted floors take the form Ordering's have |
| `tests/Inventory.Application.Tests/ReinstateReservationValidatorTests.cs` | create | The validator's two cases |
| `tests/Inventory.Application.Tests/DependencyInjectionTests.cs` | modify | The scan registers the new validator |
| `tests/Inventory.Api.Tests/IdempotencyMarkerTests.cs` | modify | The third inverted floor: the key-width gate now has an operation name to look at |
| `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs` | modify | The declared idempotent set is exactly this command, and its endpoint is authenticated and admin-only |
| `tests/Inventory.Api.Tests/ReservationTestSupport.cs` | modify | `Admin(fixture, caller)` and `ReinstateAsync(client, orderId, commandId)` |
| `tests/Inventory.Api.Tests/ReservationEndpointsTests.cs` | modify | The four reinstatements that reach the handler send the body |
| `tests/Inventory.Api.Tests/InventoryEventEndpointTests.cs` | modify | Its one reinstatement sends the body |
| `tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs` | create | §8.5 end to end for this command: replay, concurrency, the marker row, a refusal's release, the subject, the empty id |
| `tests/Inventory.Api.Tests/ReinstateReservationRequestTests.cs` | create | A body that does not bind is a 400 that never reaches the pipeline |

Two reinstate call sites stay as they are, on purpose:
`EndpointSecurityTests.cs:58` asserts 401 and
`ReservationEndpointsTests.cs:203–204` asserts 403, and both statuses are
written by the middleware before the endpoint binds anything. All 172 tests
of `Inventory.Api.Tests` passed in the prototype with those two unchanged.

---

### Task 1: The branch, the baseline and the runbook's measurement

**Files:** none edited.

**Interfaces:**
- Consumes: `/branch`; a running Docker daemon.
- Produces: a worktree on a branch that is not `main`, and three recorded
  measurements the later tasks compare against.

- [ ] **Step 1: Create the branch**

```
/branch ReinstateReservation takes a commandId and replays a repeated reinstatement
```

Then, from the worktree it moves the session into:

```bash
git branch --show-current
```

Expected: one line naming the new branch, a `feat/` name about the
reinstatement. If it prints `main`, stop.

- [ ] **Step 2: Check the daemon**

```bash
docker info --format "{{.ServerVersion}}"
```

Expected: a version number. An error naming the Docker endpoint means the
container suite cannot run; stop and say so rather than skipping it.

- [ ] **Step 3: Record the baseline**

```bash
dotnet build Platform.slnx
dotnet test tests/Inventory.Application.Tests
dotnet test tests/Inventory.Api.Tests
```

Expected, as measured on `b07cd0b2`: the build ends `0 Warning(s)`,
`0 Error(s)`; then

```
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41 - Inventory.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   161, Skipped:     0, Total:   161 - Inventory.Api.Tests.dll (net10.0)
```

The second run takes a little over a minute.

- [ ] **Step 4: Measure the runbook**

```bash
grep -n -i -E "v1/inventory|curl|commandId|/reinstate" docs/runbooks/order-review.md
```

Expected: no output and exit status 1. The runbook shows no request, so it
stays out of the touch set. Any output is a line that shows the request: add
the file to the touch-set row as *Global Constraints* says, and give that line
the body `{ "commandId": "<guid>" }` in Task 2's commit.

---

### Task 2: The command carries the id, and the endpoint binds it

**Files:**
- Modify: `src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationCommand.cs` (whole file, lines 1–5)
- Modify: `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs:42-53`
- Test: `tests/Inventory.Application.Tests/IdempotencyOptInTests.cs:64-67` and `:99-107`
- Test: `tests/Inventory.Api.Tests/IdempotencyMarkerTests.cs:223-232`
- Test: `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs:1` and before `:78`
- Test: `tests/Inventory.Api.Tests/ReservationTestSupport.cs:1` and `:18-25`
- Test: `tests/Inventory.Api.Tests/ReservationEndpointsTests.cs:99-100`, `:120-121`, `:135-136`, `:178-179`, `:221`
- Test: `tests/Inventory.Api.Tests/InventoryEventEndpointTests.cs:126-127`
- Create: `tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs`
- Create: `tests/Inventory.Api.Tests/ReinstateReservationRequestTests.cs`

**Interfaces:**
- Consumes, all as they are on `main`:

```csharp
// Common.Application
public interface IIdempotentCommand
{
    static abstract string OperationName { get; }
    Guid CommandId { get; }
}

Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken ct = default);   // IDispatcher

// Common.Web
public static IResult ToHttpResult(this Result result);

// Common.TestSupport.ServiceFixture<TFactory, TEntryPoint, TDbContext>
public Task<int> IdempotencyMarkerCountAsync(string key);
public IIdempotencyStore IdempotencyClaims { get; }

// Inventory.Api.Tests.HostSmokeTests
public sealed class AuthenticatedUnreachableFactory() : InventoryApiFactory(UnreachableSql, UnreachableRabbit);
```

- Produces:

```csharp
namespace Inventory.Application.Reservations.Reinstate;

public sealed record ReinstateReservationCommand(Guid CommandId, Guid OrderId) : ICommand<Result>, IIdempotentCommand
{
    public static string OperationName => "inventory.reservation.reinstate";
}

namespace Inventory.Api.Endpoints;

public sealed record ReinstateReservationRequest(Guid CommandId);

// tests/Inventory.Api.Tests/ReservationTestSupport.cs
public static HttpClient Admin(ServiceFixture fixture, Guid caller);
public static Task<HttpResponseMessage> ReinstateAsync(HttpClient client, Guid orderId, Guid commandId);
```

The wire: `POST /v1/inventory/reservations/{orderId}/reinstate`, body
`{ "commandId": "<guid>" }`. 204 on a reinstatement and on its repeat under
the same id; 404 and 422 as before; 409 `request.in_progress` for a repeat
that arrives while the first is in flight.

The command and the endpoint move in one commit because the endpoint is the
command's only caller and neither compiles without the other. The validator is
Task 3's, so this task's commit still accepts the empty id; do not open the
pull request between the two.

- [ ] **Step 1: Flip the two floors in `IdempotencyOptInTests`**

In `tests/Inventory.Application.Tests/IdempotencyOptInTests.cs`, replace

```csharp
        candidates.ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the two shape checks below " +
            "are vacuous. The day it does, this test fails — restore the ShouldNotBeEmpty " +
            "form, which is what keeps a vacuous gate from quietly becoming a permanent one.");
```

with

```csharp
        candidates.ShouldNotBeEmpty(
            "no command in this assembly implements IIdempotentCommand, so this test is " +
            "looking at nothing — the interface has been renamed, moved, or not yet applied.");
```

and replace

```csharp
        // With fewer than two idempotent commands the distinctness check cannot fail.
        names.ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the check below is "
            + "vacuous. The day it does, this test fails — replace it with the ShouldNotBeEmpty "
            + "form, which is what keeps a vacuous gate from quietly becoming a permanent one. "
            + "RESTORE OR EXTEND AuthorizationPolicyTests IN THE SAME CHANGE: §8.5 requires an "
            + "idempotent command's endpoint to be authenticated, an anonymous one collapses "
            + "every caller into the shared system subject, and the scaffold drops that suite "
            + "as a slice file.");
```

with

```csharp
        // With fewer than two idempotent commands the distinctness check cannot fail, so this fails on none.
        names.ShouldNotBeEmpty("Inventory declares an idempotent command; the selector above found none");
```

Both replacements are the text Ordering's copy of this file,
`tests/Ordering.Application.Tests/IdempotencyOptInTests.cs`, carries at the
same two places, with the service's name changed. The second floor's old
message asks for `AuthorizationPolicyTests` to be extended in the same change;
Step 4 does that.

- [ ] **Step 2: Run the floors and see them fail**

```bash
dotnet test tests/Inventory.Application.Tests --filter "FullyQualifiedName~IdempotencyOptInTests"
```

Expected: `Failed:     2, Passed:     5`. The two failures are
`Operation_names_are_distinct_within_this_service` ("names should not be
empty but was", "Inventory declares an idempotent command; the selector above
found none") and
`Idempotent_commands_return_a_result_shape_the_behaviour_rebuilds`
("candidates should not be empty but was").

- [ ] **Step 3: Flip the third floor, in `IdempotencyMarkerTests`**

The spec names this file beside `IdempotencyOptInTests`; it holds one more floor
written to fail on the same day. In
`tests/Inventory.Api.Tests/IdempotencyMarkerTests.cs`, replace

```csharp
    public async Task This_service_has_no_operation_names_for_the_gate_above_yet()
    {
        // The gate's subject, asserted apart, since ShouldBeEmpty is green on an empty selection.
        await Task.CompletedTask;

        Operations().ShouldBeEmpty(
            "This service opts no command into idempotency yet, so the width gate above is " +
            "vacuous. The day it does, this test fails — replace it with the ShouldNotBeEmpty " +
            "form, which is what keeps a vacuous gate from quietly becoming a permanent one.");
    }
```

with the form `tests/Ordering.Api.Tests/IdempotencyMarkerTests.cs` has:

```csharp
    public async Task The_gate_above_is_looking_at_this_service_s_operation_names()
    {
        // The gate's subject, asserted apart, since ShouldBeEmpty is green on an empty selection.
        await Task.CompletedTask;

        Operations().ShouldNotBeEmpty(
            "no command in this assembly declares IIdempotentCommand, so the width gate is " +
            "looking at nothing — the interface has been renamed, moved, or not yet applied");
    }
```

- [ ] **Step 4: Hold the command to an authenticated admin endpoint**

`tests/Ordering.Api.Tests/AuthorizationPolicyTests.cs` selects an idempotent
command's endpoint by a handler parameter that implements
`IIdempotentCommand`. The reinstate endpoint binds a request record, so that
selector cannot see it; this test finds it by name instead, and asserts that
the declared set is exactly this command, so a second idempotent command
fails here until its endpoint is named. PR-C replaces the by-name link with
endpoint metadata.

In `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs`, replace the first
two `using` lines

```csharp
using Inventory.Api;
using Microsoft.AspNetCore.Authorization;
```

with

```csharp
using Common.Application;
using Inventory.Api;
using Inventory.Application.Reservations.Reinstate;
using Microsoft.AspNetCore.Authorization;
```

and insert this test above `private static string? Name(Endpoint endpoint) =>`,
with a blank line after it:

```csharp
    [Fact]
    public void The_idempotent_command_is_reached_through_an_authenticated_admin_endpoint()
    {
        // §8.5: an anonymous caller keys under the shared "system" segment. Found by name, since the endpoint
        // binds a request record and no handler parameter is the command.
        Type[] declared =
        [
            .. typeof(Inventory.Application.DependencyInjection).Assembly
                .GetTypes()
                .Where(typeof(IIdempotentCommand).IsAssignableFrom)
                .Where(t => t is { IsClass: true, IsAbstract: false })
        ];

        declared.ShouldBe(
            [typeof(ReinstateReservationCommand)],
            "an idempotent command this test does not name has no endpoint held to authentication " +
            "here; name its endpoint below in the change that adds it (§8.5)");

        Endpoint endpoint = Endpoints
            .Where(e => Name(e) == "ReinstateReservation")
            .ShouldHaveSingleItem();

        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull(
            "an anonymous reinstatement would claim under the subject every anonymous caller shares (§8.5)");
        endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .ShouldContain(InventoryPermissions.Admin);
    }
```

- [ ] **Step 5: Give the test support a pinned caller and a keyed request**

In `tests/Inventory.Api.Tests/ReservationTestSupport.cs`, add
`using System.Net.Http.Json;` as the first `using` line, above
`using Common.Contracts.Inventory.V1;`, and replace

```csharp
    /// <summary>A client carrying <see cref="InventoryPermissions.Admin"/>.</summary>
    public static HttpClient Admin(ServiceFixture fixture)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }
```

with

```csharp
    /// <summary>A client carrying <see cref="InventoryPermissions.Admin"/>.</summary>
    public static HttpClient Admin(ServiceFixture fixture) => Admin(fixture, Guid.CreateVersion7());

    /// <summary>The same client as <paramref name="caller"/>, for a test whose subject is §8.5's key.</summary>
    public static HttpClient Admin(ServiceFixture fixture, Guid caller)
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, caller.ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);
        return client;
    }

    /// <summary>One reinstatement under <paramref name="commandId"/>, the caller's id for the attempt (§8.5).</summary>
    public static Task<HttpResponseMessage> ReinstateAsync(HttpClient client, Guid orderId, Guid commandId) =>
        client.PostAsJsonAsync(
            $"/v1/inventory/reservations/{orderId}/reinstate",
            new { commandId },
            TestContext.Current.CancellationToken);
```

- [ ] **Step 6: Send the body from the five call sites that reach the handler**

In `tests/Inventory.Api.Tests/ReservationEndpointsTests.cs`, four
replacements and one addition. Replace

```csharp
        HttpResponseMessage response = await client.PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusAsync(order)).ShouldBe("Reserved");
```

with

```csharp
        HttpResponseMessage response = await ReinstateAsync(client, order);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusAsync(order)).ShouldBe("Reserved");
```

Replace

```csharp
        HttpResponseMessage tombstone = await client.PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);
```

with

```csharp
        HttpResponseMessage tombstone = await ReinstateAsync(client, order);
```

Replace

```csharp
        HttpResponseMessage shortage = await client.PostAsync(
            $"/v1/inventory/reservations/{held}/reinstate", null, TestContext.Current.CancellationToken);
```

with

```csharp
        HttpResponseMessage shortage = await ReinstateAsync(client, held);
```

In `A_release_and_a_reinstate_at_once_end_in_exactly_one_state`, replace

```csharp
            client.PostAsync(
                $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken),
```

with

```csharp
            ReinstateAsync(client, order),
```

And at the end of the class, replace

```csharp
    private HttpClient Admin() => ReservationTestSupport.Admin(fixture);
}
```

with

```csharp
    private HttpClient Admin() => ReservationTestSupport.Admin(fixture);

    /// <summary>A fresh command id per call, or a second reinstatement would replay the first's (§8.5).</summary>
    private static Task<HttpResponseMessage> ReinstateAsync(HttpClient client, Guid orderId) =>
        ReservationTestSupport.ReinstateAsync(client, orderId, Guid.CreateVersion7());
}
```

Leave the reinstatement in
`Every_reservation_endpoint_requires_the_admin_permission` as it is: a 403 is
written before the endpoint binds.

In `tests/Inventory.Api.Tests/InventoryEventEndpointTests.cs`, replace

```csharp
        HttpResponseMessage reinstate = await Admin().PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate", null, TestContext.Current.CancellationToken);
```

with

```csharp
        HttpResponseMessage reinstate =
            await ReservationTestSupport.ReinstateAsync(Admin(), order, Guid.CreateVersion7());
```

- [ ] **Step 7: Write the end-to-end tests**

Create `tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs`:

```csharp
using System.Net;
using Common.Contracts.Inventory.V1;
using Inventory.TestSupport;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>§8.5 end to end for the reinstatement: HTTP, the registered pipeline, real Redis and SQL Server.</summary>
[Collection(nameof(IntegrationCollection))]
public sealed class ReinstateReservationIdempotencyTests(ServiceFixture fixture) : IAsyncLifetime
{
    // The key's middle segment, spelled out because a changed name orphans every live key (§8.5).
    private const string Operation = "inventory.reservation.reinstate";

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_reinstatement_repeated_under_one_command_id_answers_as_the_first_did_and_takes_stock_once()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        int levels = await LevelsAsync();
        using HttpClient client = Admin(Guid.CreateVersion7());
        var commandId = Guid.CreateVersion7();

        HttpResponseMessage first = await ReservationTestSupport.ReinstateAsync(client, order, commandId);
        HttpResponseMessage second = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(
            HttpStatusCode.NoContent,
            "the repeat is the first request again, so it is answered as the first was (§8.5)");
        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(1, "two of the three were taken, once");
        (await LevelsAsync()).ShouldBe(levels + 1, "a replay runs no handler, so it moves no level");
    }

    [Fact]
    public async Task Two_reinstatements_at_once_under_one_command_id_apply_one()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        int levels = await LevelsAsync();
        using HttpClient client = Admin(Guid.CreateVersion7());
        var commandId = Guid.CreateVersion7();

        HttpResponseMessage[] responses = await Task.WhenAll(
            ReservationTestSupport.ReinstateAsync(client, order, commandId),
            ReservationTestSupport.ReinstateAsync(client, order, commandId));

        responses.ShouldContain(r => r.StatusCode == HttpStatusCode.NoContent, "one of the two held the claim");
        foreach (HttpResponseMessage response in responses.Where(r => r.StatusCode != HttpStatusCode.NoContent))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the other met the claim, never the handler");
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .ShouldContain("request.in_progress");
        }

        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(1);
        (await LevelsAsync()).ShouldBe(levels + 1, "one handler ran, whichever answer the other was given");
    }

    [Fact]
    public async Task A_committed_reinstatement_leaves_its_marker_in_this_service_s_schema()
    {
        (_, Guid order) = await ReleasedAsync();
        var caller = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        using HttpClient client = Admin(caller);

        HttpResponseMessage response = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await fixture.IdempotencyMarkerCountAsync(Key(caller, commandId))).ShouldBe(
            1,
            "§6.3 writes the marker in the reinstatement's own transaction, under the key the claim took");
    }

    [Fact]
    public async Task A_refused_reinstatement_stores_nothing_so_the_same_id_carries_the_next_attempt()
    {
        (Guid product, Guid order) = await ReleasedAsync(available: 2);
        await fixture.ExecuteAsync("UPDATE inventory.StockItems SET Available = 1 WHERE ProductId = {0}", product);
        var caller = Guid.CreateVersion7();
        var commandId = Guid.CreateVersion7();
        using HttpClient client = Admin(caller);

        HttpResponseMessage shortage = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        shortage.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await fixture.IdempotencyMarkerCountAsync(Key(caller, commandId))).ShouldBe(0);
        (await fixture.IdempotencyClaims.GetAsync(Key(caller, commandId), TestContext.Current.CancellationToken))
            .ShouldBeNull("a refusal releases its claim (§8.5)");

        await fixture.ExecuteAsync("UPDATE inventory.StockItems SET Available = 2 WHERE ProductId = {0}", product);
        HttpResponseMessage retried = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        retried.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the id was never spent, so it is not replayed");
        (await StatusAsync(order)).ShouldBe("Reserved");
        (await Available(product)).ShouldBe(0);
    }

    [Fact]
    public async Task A_refusal_repeated_under_its_id_is_refused_again_rather_than_replayed_or_held()
    {
        using HttpClient client = Admin(Guid.CreateVersion7());
        var unknown = Guid.CreateVersion7();
        var missing = Guid.CreateVersion7();

        (await ReservationTestSupport.ReinstateAsync(client, unknown, missing))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReservationTestSupport.ReinstateAsync(client, unknown, missing))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound, "neither a replayed success nor a claim left held");

        var tombstoned = Guid.CreateVersion7();
        var refused = Guid.CreateVersion7();
        await client.PostAsync(
            $"/v1/inventory/reservations/{tombstoned}/release", null, TestContext.Current.CancellationToken);

        (await ReservationTestSupport.ReinstateAsync(client, tombstoned, refused))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReservationTestSupport.ReinstateAsync(client, tombstoned, refused))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task A_command_id_names_one_act_so_its_repeat_after_a_later_release_retakes_nothing()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        using HttpClient client = Admin(Guid.CreateVersion7());
        var commandId = Guid.CreateVersion7();
        (await ReservationTestSupport.ReinstateAsync(client, order, commandId))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync(
                $"/v1/inventory/reservations/{order}/release", null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage repeat = await ReservationTestSupport.ReinstateAsync(client, order, commandId);

        repeat.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the recorded answer, not a second reinstatement");
        (await StatusAsync(order)).ShouldBe("Released", "a new act takes a new id; this one was answered");
        (await Available(product)).ShouldBe(3);
    }

    [Fact]
    public async Task Another_admin_sending_the_same_command_id_is_judged_on_the_reservation_and_not_replayed()
    {
        (Guid product, Guid order) = await ReleasedAsync();
        var commandId = Guid.CreateVersion7();
        using HttpClient first = Admin(Guid.CreateVersion7());
        using HttpClient second = Admin(Guid.CreateVersion7());
        (await ReservationTestSupport.ReinstateAsync(first, order, commandId))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        HttpResponseMessage other = await ReservationTestSupport.ReinstateAsync(second, order, commandId);

        other.StatusCode.ShouldBe(
            HttpStatusCode.UnprocessableEntity,
            "the key's first segment is the caller, so one admin's id is no key of another's (§8.5)");
        (await other.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("reservation.not_reinstatable");
        (await Available(product)).ShouldBe(1);
    }

    /// <summary>§8.5's key as the behaviour builds it: subject, operation, command id.</summary>
    private static string Key(Guid caller, Guid commandId) => $"{caller}:{Operation}:{commandId}";

    /// <summary>Two of <paramref name="available"/> reserved and released again, as the runbook finds them.</summary>
    private async Task<(Guid Product, Guid Order)> ReleasedAsync(int available = 3)
    {
        var product = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();
        await ReservationTestSupport.SeedStock(fixture, product, available);
        await ReservationTestSupport.SendAsync(fixture, new ReserveStock(order, [new StockLine(product, 2)]));
        await ReservationTestSupport.EventuallyStatus(fixture, order, "Reserved");
        await ReservationTestSupport.SendAsync(fixture, new ReleaseStock(order));
        await ReservationTestSupport.EventuallyStatus(fixture, order, "Released");

        return (product, order);
    }

    private async Task<int> LevelsAsync() =>
        (await fixture.OutboxAsync())
            .Count(r => r.MessageType.Contains("StockLevelChanged", StringComparison.Ordinal));

    private Task<int> Available(Guid product) => ReservationTestSupport.Available(fixture, product);

    private Task<string> StatusAsync(Guid orderId) => ReservationTestSupport.StatusAsync(fixture, orderId);

    private HttpClient Admin(Guid caller) => ReservationTestSupport.Admin(fixture, caller);
}
```

`Operation` spells the name out on purpose: the test that reads the marker by
its key is what fails if `OperationName` is ever changed, and a changed name
orphans every live key (§8.5).

- [ ] **Step 8: Write the binding test**

Create `tests/Inventory.Api.Tests/ReinstateReservationRequestTests.cs`:

```csharp
using System.Net;
using System.Text;
using Inventory.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>What the deployed host answers a reinstatement whose body never binds, with no store to reach.</summary>
public class ReinstateReservationRequestTests(HostSmokeTests.AuthenticatedUnreachableFactory factory)
    : IClassFixture<HostSmokeTests.AuthenticatedUnreachableFactory>
{
    [Fact]
    public async Task A_body_that_does_not_bind_is_400_before_the_pipeline()
    {
        // Production, since Development's RouteHandlerOptions.ThrowOnBadRequest raises the same refusal as an
        // exception no §10.5 handler translates.
        using WebApplicationFactory<Program> production =
            factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using HttpClient client = production.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, InventoryPermissions.Admin);

        string?[] bodies = [null, "", """{"commandId":null}""", """{"commandId":"not-a-guid"}"""];

        foreach (string? body in bodies)
        {
            HttpResponseMessage response = await client.PostAsync(
                $"/v1/inventory/reservations/{Guid.CreateVersion7()}/reinstate",
                body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);

            response.StatusCode.ShouldBe(
                HttpStatusCode.BadRequest,
                $"'{body}' reached §6.3's pipeline, which on this host can only fail on an unreachable store");
        }
    }
}
```

It needs no container. Every store its host names is unreachable, so the
only way to answer 400 is to refuse before the pipeline; and the host is
`Production` for the reason Review Focus, line 1 gives.

- [ ] **Step 9: Convert the two new files and run everything red**

```bash
unix2dos -q tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs tests/Inventory.Api.Tests/ReinstateReservationRequestTests.cs
dotnet test tests/Inventory.Api.Tests --filter "FullyQualifiedName~ReinstateReservationIdempotencyTests|FullyQualifiedName~ReinstateReservationRequestTests|FullyQualifiedName~AuthorizationPolicyTests|FullyQualifiedName~IdempotencyMarkerTests"
```

Expected: it compiles, because no test names a type that does not exist yet,
and ends `Failed:     7, Passed:    13, Skipped:     0, Total:    20`. The run
takes about a minute, nearly all of it
`A_body_that_does_not_bind_is_400_before_the_pipeline` waiting on an
unreachable SQL Server. The seven failures, each for the reason the change
exists:

| Test | Fails with |
|---|---|
| `AuthorizationPolicyTests.The_idempotent_command_is_reached_through_an_authenticated_admin_endpoint` | `declared` should be `[ReinstateReservationCommand]` but was `[]` |
| `IdempotencyMarkerTests.The_gate_above_is_looking_at_this_service_s_operation_names` | `Operations()` should not be empty but was |
| `A_reinstatement_repeated_under_one_command_id_answers_as_the_first_did_and_takes_stock_once` | `second.StatusCode` should be `NoContent` but was `UnprocessableEntity` — the spec's defect, observed |
| `Two_reinstatements_at_once_under_one_command_id_apply_one` | should be `Conflict` but was `UnprocessableEntity` |
| `A_committed_reinstatement_leaves_its_marker_in_this_service_s_schema` | should be `1` but was `0` |
| `A_command_id_names_one_act_so_its_repeat_after_a_later_release_retakes_nothing` | should be `"Released"` but was `"Reserved"` |
| `ReinstateReservationRequestTests.A_body_that_does_not_bind_is_400_before_the_pipeline` | should be `BadRequest` but was `InternalServerError` |

Three of the new tests pass now and must still pass after Step 12, because
they pin what the key must not change:
`A_refused_reinstatement_stores_nothing_so_the_same_id_carries_the_next_attempt`,
`A_refusal_repeated_under_its_id_is_refused_again_rather_than_replayed_or_held`
and
`Another_admin_sending_the_same_command_id_is_judged_on_the_reservation_and_not_replayed`.
A refusal and a second caller are answered today as the handler answers
them, and the mechanism's promise is that they still are: a failed `Result`
releases its claim and stores nothing, and the key's first segment is the
caller.

- [ ] **Step 10: Key the command**

Replace the whole of
`src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationCommand.cs`
with:

```csharp
using Common.Application;

namespace Inventory.Application.Reservations.Reinstate;

/// <summary>The runbook's reinstatement, keyed so that a repeat whose answer was lost is replayed (§8.5).</summary>
public sealed record ReinstateReservationCommand(Guid CommandId, Guid OrderId) : ICommand<Result>, IIdempotentCommand
{
    /// <summary>Declared, never derived from the type name, so a rename cannot change a live key (§8.5).</summary>
    public static string OperationName => "inventory.reservation.reinstate";
}
```

`ReinstateReservationHandler` reads `command.OrderId` alone and is not edited.

- [ ] **Step 11: Bind the id at the endpoint**

In `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs`,
replace the end of the file, from the reinstate mapping down,

```csharp
        group
            .MapPost(
                "/{orderId:guid}/reinstate",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(new ReinstateReservationCommand(orderId), ct);

                    return result.ToHttpResult();
                })
            .WithName("ReinstateReservation");
    }
}
```

with

```csharp
        // A request record, since the order is the route's and the body carries the attempt's id alone (§8.5).
        group
            .MapPost(
                "/{orderId:guid}/reinstate",
                async (
                    Guid orderId,
                    ReinstateReservationRequest request,
                    IDispatcher dispatcher,
                    CancellationToken ct) =>
                {
                    Result result = await dispatcher.SendAsync(
                        new ReinstateReservationCommand(request.CommandId, orderId), ct);

                    return result.ToHttpResult();
                })
            .WithName("ReinstateReservation");
    }
}

/// <summary>The caller's id for this attempt, in the body because §8.5 keeps it a field of the command.</summary>
public sealed record ReinstateReservationRequest(Guid CommandId);
```

The file's `using` lines do not change.

- [ ] **Step 12: Run everything green**

```bash
dotnet build Platform.slnx
dotnet test tests/Inventory.Application.Tests
dotnet test tests/Inventory.Api.Tests
```

Expected: the build ends `0 Warning(s)`, `0 Error(s)`; then

```
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41 - Inventory.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   170, Skipped:     0, Total:   170 - Inventory.Api.Tests.dll (net10.0)
```

170 is the baseline's 161 and the nine tests this task adds. The 161 include
`A_release_and_a_reinstate_at_once_end_in_exactly_one_state`, which races a
keyed reinstatement against a release, and the two tests whose call sites
were left alone.

- [ ] **Step 13: Check the line endings and commit**

```bash
git ls-files --eol -m -o --exclude-standard
git branch --show-current
```

Expected: ten lines, each reading `w/crlf`, and the branch's name, not
`main`. Run `unix2dos -q` on any file whose line reads otherwise, and check
again.

```bash
git add src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationCommand.cs src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs tests/Inventory.Application.Tests/IdempotencyOptInTests.cs tests/Inventory.Api.Tests/IdempotencyMarkerTests.cs tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs tests/Inventory.Api.Tests/ReservationTestSupport.cs tests/Inventory.Api.Tests/ReservationEndpointsTests.cs tests/Inventory.Api.Tests/InventoryEventEndpointTests.cs tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs tests/Inventory.Api.Tests/ReinstateReservationRequestTests.cs
git commit -F - <<'EOF'
feat(inventory): ReinstateReservationCommand carries a CommandId and opts into §8.5's claim

A reinstatement whose answer is lost was retried into
reservation.not_reinstatable: the first attempt left the reservation
Reserved, so the repeat was refused for work that had succeeded. The
command now declares IIdempotentCommand under
inventory.reservation.reinstate, the endpoint binds the id from a
ReinstateReservationRequest body and keeps the order in the route, and
a repeat under the same id is answered 204 from the record without
taking the stock again.

The three floors written to fail on this service's first idempotent
command take the form Ordering's have. AuthorizationPolicyTests holds
the command to an authenticated admin endpoint by name, because the
endpoint binds a request record and no handler parameter is the
command.

The wire breaks for a caller that sends no body: its one known caller
is the blueprint-admin console. The empty id is still accepted by this
commit; the validator that refuses it is the next one.

Class A. Touch set: src/Services/Inventory/**, tests/Inventory.*
EOF
```

The class and the touch set close the first commit's body because there is
no issue to carry them (`docs/change-locality.md` §5).

---

### Task 3: The validator refuses the empty id before any claim

**Files:**
- Create: `src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationValidator.cs`
- Create: `tests/Inventory.Application.Tests/ReinstateReservationValidatorTests.cs`
- Test: `tests/Inventory.Application.Tests/DependencyInjectionTests.cs:167-168`
- Test: `tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs` (Task 2's file)

**Interfaces:**
- Consumes: Task 2's `ReinstateReservationCommand(Guid CommandId, Guid OrderId)`
  and `ReservationTestSupport.Admin(ServiceFixture fixture, Guid caller)`;
  `ValidationBehavior`, which runs before `IdempotencyBehavior` (§6.3);
  `ValidationExceptionHandler`, which answers a field-keyed 400 (§10.5);
  `IIdempotencyStore.GetAsync(string key, CancellationToken ct)`.
- Produces:

```csharp
namespace Inventory.Application.Reservations.Reinstate;

public sealed class ReinstateReservationValidator : AbstractValidator<ReinstateReservationCommand>
```

registered by the scan `AddInventoryApplication` already runs.

The validator asks for the `CommandId` alone, as the spec says. An empty
`orderId` in the route stays what it is today, a 404 from the handler.

- [ ] **Step 1: Write the validator's tests**

Create
`tests/Inventory.Application.Tests/ReinstateReservationValidatorTests.cs`:

```csharp
using FluentValidation.TestHelper;
using Inventory.Application.Reservations.Reinstate;
using Xunit;

namespace Inventory.Application.Tests;

public class ReinstateReservationValidatorTests
{
    private readonly ReinstateReservationValidator _validator = new();

    [Fact]
    public void An_empty_command_id_is_refused_before_any_claim()
    {
        _validator.TestValidate(new ReinstateReservationCommand(Guid.Empty, Guid.CreateVersion7()))
            .ShouldHaveValidationErrorFor(c => c.CommandId);
    }

    [Fact]
    public void A_command_id_is_all_the_validator_asks_for()
    {
        _validator.TestValidate(new ReinstateReservationCommand(Guid.CreateVersion7(), Guid.CreateVersion7()))
            .ShouldNotHaveAnyValidationErrors();
    }
}
```

In `tests/Inventory.Application.Tests/DependencyInjectionTests.cs`, in
`AddInventoryApplication_registers_the_slice_handlers`, replace

```csharp
        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<ReinstateReservationCommand, Result>));
```

with

```csharp
        services.ShouldContain(d =>
            d.ServiceType == typeof(ICommandHandler<ReinstateReservationCommand, Result>));
        services.ShouldContain(d =>
            d.ServiceType == typeof(IValidator<ReinstateReservationCommand>));
```

- [ ] **Step 2: Run them and see the build fail**

```bash
unix2dos -q tests/Inventory.Application.Tests/ReinstateReservationValidatorTests.cs
dotnet test tests/Inventory.Application.Tests
```

Expected: no test runs. The build fails with
`error CS0246: The type or namespace name 'ReinstateReservationValidator' could not be found`
at `ReinstateReservationValidatorTests.cs(9,22)`.

- [ ] **Step 3: Write the end-to-end test for the omitted and the empty id**

In `tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs`, three
additions. Add `using System.Text;` as the second `using` line, under
`using System.Net;`.

Insert this test after
`Another_admin_sending_the_same_command_id_is_judged_on_the_reservation_and_not_replayed`
and above the `Key` helper's summary, with a blank line after it:

```csharp
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"commandId":"00000000-0000-0000-0000-000000000000"}""")]
    public async Task An_omitted_or_empty_command_id_is_400_naming_the_field_and_writes_nothing(string body)
    {
        (Guid product, Guid order) = await ReleasedAsync();
        var caller = Guid.CreateVersion7();
        using HttpClient client = Admin(caller);

        HttpResponseMessage response = await PostAsync(client, order, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("CommandId");
        (await StatusAsync(order)).ShouldBe("Released");
        (await Available(product)).ShouldBe(3);
        (await fixture.IdempotencyClaims.GetAsync(Key(caller, Guid.Empty), TestContext.Current.CancellationToken))
            .ShouldBeNull("validation runs before any claim (§6.3)");
    }
```

And insert this helper after the `Key` helper, with a blank line before it:

```csharp
    /// <summary>A raw body, so a shape no client library would send still reaches the endpoint.</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid order, string body) =>
        client.PostAsync(
            $"/v1/inventory/reservations/{order}/reinstate",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
```

- [ ] **Step 4: See why the validator is needed**

The Application test project does not compile yet, so run this one alone:

```bash
dotnet test tests/Inventory.Api.Tests --filter "FullyQualifiedName~An_omitted_or_empty_command_id"
```

Expected: `Failed:     2, Passed:     0`, both cases with
`response.StatusCode should be HttpStatusCode.BadRequest but was
HttpStatusCode.NoContent`. An omitted `commandId` binds `Guid.Empty`, and
without the validator the reinstatement runs under that id.

- [ ] **Step 5: Write the validator**

Create
`src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationValidator.cs`:

```csharp
using FluentValidation;

namespace Inventory.Application.Reservations.Reinstate;

public sealed class ReinstateReservationValidator : AbstractValidator<ReinstateReservationCommand>
{
    public ReinstateReservationValidator()
    {
        // An omitted id binds Guid.Empty, which would key every such request alike; refused before any claim (§6.3).
        RuleFor(c => c.CommandId).NotEmpty();
    }
}
```

It carries no summary, as `ReleaseStockValidator` beside it carries none: the
name says what a summary would.

- [ ] **Step 6: Run everything green**

```bash
unix2dos -q src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationValidator.cs
dotnet build Platform.slnx
dotnet test tests/Inventory.Application.Tests
dotnet test tests/Inventory.Api.Tests
```

Expected: the build ends `0 Warning(s)`, `0 Error(s)`; then

```
Passed!  - Failed:     0, Passed:    43, Skipped:     0, Total:    43 - Inventory.Application.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   172, Skipped:     0, Total:   172 - Inventory.Api.Tests.dll (net10.0)
```

- [ ] **Step 7: Check the line endings and commit**

```bash
git ls-files --eol -m -o --exclude-standard
git branch --show-current
```

Expected: four lines, each reading `w/crlf`, and the branch's name. Run
`unix2dos -q` on any file whose line reads otherwise, and check again.

```bash
git add src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationValidator.cs tests/Inventory.Application.Tests/ReinstateReservationValidatorTests.cs tests/Inventory.Application.Tests/DependencyInjectionTests.cs tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs
git commit -F - <<'EOF'
feat(inventory): ReinstateReservationValidator refuses an empty CommandId before any claim

An omitted commandId binds Guid.Empty, which would key every such
request of one caller alike, so the second would be answered with the
first one's result for an order it never touched. ValidationBehavior
runs before IdempotencyBehavior (§6.3), so the refusal is a 400 keyed
by CommandId and no key is claimed for it.
EOF
```

---

### Task 4: Verification and the pull request

**Files:** none edited.

**Interfaces:**
- Consumes: the two commits of Tasks 2 and 3.
- Produces: a pull request in the house form, with its two locality rows.

- [ ] **Step 1: The build**

```bash
dotnet build Platform.slnx
```

Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 2: The suites that need no daemon**

```bash
dotnet test Platform.slnx --filter "Category!=Integration"
```

Expected: every project line reads `Failed:     0`; among them
`Passed:    43` for `Inventory.Application.Tests.dll`, `Passed:    27` for
`Inventory.Domain.Tests.dll` and `Passed:    53` for
`Inventory.Api.Tests.dll`. In the prototype's run of this command on a busy
machine, three timing tests in suites this change does not touch failed and
then passed when run alone:
`Common.Infrastructure.Tests.IntegrationEventConsumerTests.The_delivery_lag_is_measured_from_the_messages_own_timestamp`,
`Payments.Api.Tests.HttpPaymentProviderTests.A_scripted_decline_is_a_decline_with_the_providers_code`
and
`Shipping.Worker.Tests.HttpCarrierGatewayTests.A_body_in_a_charset_nobody_can_decode_is_unavailable_and_counted`.
If one of them fails, rerun it alone with its line below and report both
results; do not edit it.

```bash
dotnet test tests/Common.Infrastructure.Tests --filter "FullyQualifiedName~The_delivery_lag_is_measured_from_the_messages_own_timestamp"
dotnet test tests/Payments.Api.Tests --filter "FullyQualifiedName~A_scripted_decline_is_a_decline_with_the_providers_code"
dotnet test tests/Shipping.Worker.Tests --filter "FullyQualifiedName~A_body_in_a_charset_nobody_can_decode_is_unavailable_and_counted"
```

- [ ] **Step 3: Inventory's container suite**

```bash
dotnet test tests/Inventory.Api.Tests
```

Expected:

```
Passed!  - Failed:     0, Passed:   172, Skipped:     0, Total:   172 - Inventory.Api.Tests.dll (net10.0)
```

- [ ] **Step 4: The formatter and the comment gate**

```bash
git fetch origin main
dotnet format Platform.slnx --verify-no-changes --include $(git diff --name-only origin/main...HEAD | tr '\n' ' ')
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
```

Expected: `dotnet format` prints nothing and exits 0. The comment gate ends
`judged the added lines of 13 file(s) it reads, 0 finding(s)` and exits 0.

- [ ] **Step 5: The diff is the touch set**

```bash
git diff --name-only origin/main...HEAD
```

Expected: exactly these thirteen paths, every one under
`src/Services/Inventory/` or `tests/Inventory.`:

```
src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs
src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationCommand.cs
src/Services/Inventory/Inventory.Application/Reservations/Reinstate/ReinstateReservationValidator.cs
tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs
tests/Inventory.Api.Tests/IdempotencyMarkerTests.cs
tests/Inventory.Api.Tests/InventoryEventEndpointTests.cs
tests/Inventory.Api.Tests/ReinstateReservationIdempotencyTests.cs
tests/Inventory.Api.Tests/ReinstateReservationRequestTests.cs
tests/Inventory.Api.Tests/ReservationEndpointsTests.cs
tests/Inventory.Api.Tests/ReservationTestSupport.cs
tests/Inventory.Application.Tests/DependencyInjectionTests.cs
tests/Inventory.Application.Tests/IdempotencyOptInTests.cs
tests/Inventory.Application.Tests/ReinstateReservationValidatorTests.cs
```

A fourteenth path is either the runbook, under Task 1's Step 4, or a mistake.

- [ ] **Step 6: Open the pull request**

```
/pr feat(inventory): ReinstateReservation takes a commandId and replays a repeated reinstatement
```

The body's metadata table opens with:

```markdown
| | |
|---|---|
| Class | A |
| Touch set | `src/Services/Inventory/**`, `tests/Inventory.*` |
```

Under *What changed*, one block per commit, from the two commit bodies. Under
the honest cost, say these four things, because a reviewer will otherwise
find them:

- The wire breaks. `blueprint-admin` must send `{ "commandId": "<guid>" }`,
  a fresh id for each act and the same id for a retry of it, and its change
  is its own repository's.
- The runbook is unchanged because it shows no request; quote Task 1's
  Step 4.
- Reusing one id for a different order is answered 204 without reinstating
  the second order until PR-A lands (Review Focus, line 2).
- A body that does not bind answers 500 under `Development` and 400
  elsewhere, on this endpoint as on `SetOnHand` today, and the fix is a
  `Common.Web` change of its own (Review Focus, line 1).

Class A, so `/validate-blueprint` does not run.

- [ ] **Step 7: Record the pull request for the owner**

`TODO.md` at the main checkout's root lists open pull requests, and a session
in a worktree cannot write it. Say in the final report that the entry is
owed, with the pull request's number and title.

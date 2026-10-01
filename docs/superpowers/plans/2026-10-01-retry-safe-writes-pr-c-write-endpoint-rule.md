# Retry-safe writes PR-C — the write-endpoint rule and its gate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every endpoint a write verb reaches say what a repeat of its
request does — it is keyed by an `IIdempotentCommand`, or it declares a
`RetrySafety` kind — and fail the build for one that says neither or both.

**Architecture:** Two metadata records and two extension methods in
`Common.Web` are the declarations. `WriteEndpointRule` in
`tests/Common.TestSupport` reads a host's `EndpointDataSource` and names every
endpoint that breaks the rule; each host that maps handlers holds it in a
`WriteEndpointRuleTests` of two facts, the offender list and the floor that
names what the rule selected. Catalog's copy is template, so the scaffold
renders it with the floor inverted and a new service is born under the rule.

**Tech Stack:** ASP.NET Core endpoint metadata (`IEndpointConventionBuilder`,
`EndpointDataSource`), xUnit v3, Shouldly, `WebApplicationFactory`, stdlib
Python for the scaffold. No package and no pin is added.

**Spec:** `docs/superpowers/specs/2026-10-01-retry-safe-writes-design.md`,
*Decision 2* in full, the `WriteEndpointRule` line of *Testing*, the PR-C row
of *Delivery*, and *Out of scope*'s third bullet.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class C**, and the PR body's touch-set cell is exactly this, as one line —
  paths only, comma-separated, no prose inside the cell and no trailing stop:

  ```
  `src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs`, `tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs`, `tests/Common.Web.Tests/Common.Web.Tests.csproj`, `tests/Common.TestSupport/WriteEndpointRule.cs`, `tests/Common.TestSupport/Common.TestSupport.csproj`, `tests/*/WriteEndpointRuleTests.cs`, `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs`, `src/Services/Catalog/Catalog.Api/Program.cs`, `src/Services/Ordering/Ordering.Api/Program.cs`, `src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs`, `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs`, `src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs`, `src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs`, `tools/new-service/scaffold/render.py`, `tools/new-service/scaffold/patch.py`, `tools/new-service/test_new_service.py`, `docs/backend-architecture/adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md`, `docs/backend-architecture/appendix-a-adrs.md`, `docs/backend-architecture/08-caching-redis.md`
  ```

  Why each: `RetrySafetyExtensions.cs` and its suite are the declarations;
  `WriteEndpointRule.cs` is the gate, and the two project files beside it and
  `Web.Bff.Tests.csproj` carry the three references it needs;
  `tests/*/WriteEndpointRuleTests.cs` is the gate's own suite and the five
  hosts'; Inventory's `AuthorizationPolicyTests.cs` loses the by-name test
  PR-B left for this PR to replace; the two `Program.cs` files and the four
  endpoint files take the declarations; the three scaffold files reconcile
  the template's new file; the ADR, its index row and §8.5 are the rule's
  record.
- **Mutex surfaces.** None of the repo-wide ones: `Platform.slnx`,
  `Directory.Packages.props` and `tests/Common.TestSupport/ServiceFixture.cs`
  are not edited. Per-service mutexes this PR takes, each named in the row:
  Catalog's and Ordering's `Program.cs`, and the project files of
  `Common.TestSupport`, `Common.Web.Tests` and `Web.Bff.Tests`.
- **`C`, not `C+E`.** Three `ProjectReference` lines are drawn between
  projects that exist — `Common.TestSupport → Common.Web`,
  `Common.Web.Tests → Common.TestSupport`,
  `Web.Bff.Tests → Common.TestSupport` — and no package, pin or project is
  added, which is what Class E's row names. All three files are under
  `tests/**`, inside Class C's set in `.github/locality-gate/classes.yml`.
  Measured on 2026-10-01: `locality_gate.py` over this plan's prototype diff,
  class `C` and the row above, answered that every changed path is inside
  both sets. Each reference is drawn for a member that cannot be written
  without it, and its `.csproj` comment says which.
- **Depends on PR-B having merged.** This plan stands on:
  `ReinstateReservationCommand(Guid CommandId, Guid OrderId)` in
  `Inventory.Application.Reservations.Reinstate`, implementing
  `ICommand<Result>` and `IIdempotentCommand`;
  `ReinstateReservationRequest(Guid CommandId)`, public, at the foot of
  `ReservationEndpoints.cs`; the `ReinstateReservation` endpoint building
  the command from the route value and that record; and
  `The_idempotent_command_is_reached_through_an_authenticated_admin_endpoint`
  in `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs`. Where PR-B
  spelled one of those differently, the spelling moves and nothing else in
  this plan does.
- **Depends on PR-A for one number and one neighbour.** ADR-058 assumes
  ADR-057 is on `main`, and Task 8 appends its Appendix A row under PR-A's.
  So this branch is cut from, or rebased onto, a `main` that holds ADR-057
  before Task 8: Task 0 checks it. Where PR-A merges after the branch was
  cut, the rebase is `bash .claude/scripts/git-rebase-onto-main.sh <branch>
  start` once the branch is pushed, the one rebase the harness grants. If
  the highest ADR under `docs/backend-architecture/adr/` is not 057 when
  Task 8 runs, this PR's number is that highest plus one, by `/new-adr`'s
  rule, and it replaces `058` everywhere this plan writes it: the file
  name, the row, §8.5's link, and every `ADR-058` in a comment. PR-A edits
  §8.5's code sample and adds a callout further down; it leaves the opening
  rule to this PR.
- **This plan and the spec are not in this PR.** `docs/superpowers/**` is
  outside Class C's set, so the locality gate refuses them here. They land in
  their own docs PR first; Task 0 requires a clean tree, and every `git add`
  in this plan names its paths.
- `.cs` files are CRLF (`.gitattributes`). The Write tool emits LF, and an LF
  `.cs` file fails the build with one IDE0055 per line: after creating one,
  run `unix2dos <path>`, and never `sed -i` a `.cs` file under Git Bash,
  which rewrites it LF. `git ls-files --eol -m -o --exclude-standard` must
  show `w/crlf` on every `.cs` line before a commit.
- `py -3.12`, never `python`.
- British spelling in prose and comments; identifiers keep their spelling.
  Explicit local types, file-scoped namespaces, 120 columns, a broken fluent
  chain with every call on its own line, no column of `=` or `=>`.
- A comment says why and cites its owner — a section, an ADR or a symbol,
  never a pull request, a review or a test. A summary is one sentence, a
  `<remarks>` cites and is four lines or fewer, a block is five or fewer.
- Every step that adds behaviour writes its test first.
- Branch with `/branch`, into a worktree under `.claude/worktrees/`. Never
  commit on `main`, and check `git branch --show-current` before every
  commit: the main checkout is shared.
- After the Catalog change: the scaffold's suite, then the dogfood in
  `docs/repo-map.md`, for both host shapes (Task 3).
- `/validate-blueprint` after the docs task, because this is Class C. Run it
  last and from a subagent: it narrows `Edit` to `docs/` for the rest of the
  session that runs it.
- **Not touched:** `tests/Common.TestSupport/ServiceFixture.cs`, `CLAUDE.md`,
  Appendix D, every existing ADR, `docs/repo-map.md`, `docs/testing.md`,
  `docs/superpowers/**`, and `tools/new-service/README.md`, whose test counts
  are a restatement `docs/change-locality.md` §2 leaves alone.
- **Out of scope, and named so that nobody finds it later.**
  `Every_idempotent_command_reaches_this_service_through_an_authenticated_endpoint`
  in Catalog's and Ordering's `AuthorizationPolicyTests` stays: it is true,
  it is green, and the scaffold's inverted floors cite its file. §11.4's
  `CancelOrder` sample is an excerpt and is not given the declaration.
  Nothing here checks that an `IIdempotentCommand` serialises under
  `System.Text.Json`, which PR-A's fingerprint needs: that is a property of
  the command's shape, whose gate is each service's `IdempotencyOptInTests`,
  not of an endpoint.

## Review Focus

The five ways this rule is most likely to pass over something, and the test
that pins each. None of them is reached by the obvious cases.

1. **An endpoint that names no method.** `Map` with no verb carries no
   `IHttpMethodMetadata` at all and answers a POST as readily as a GET, so a
   selector on the method list alone never sees it; `MapMethods` with a read
   and a write answers one too. With a handler such an endpoint is a write;
   as a bare `RequestDelegate` it is left out and named by the host's floor.
   Pinned in Task 2: `A_route_handler_mapped_with_no_method_is_a_write`,
   `A_handler_mapped_for_a_read_and_a_write_is_a_write`,
   `A_request_delegate_a_host_maps_with_no_method_is_unrestricted_and_not_a_write`,
   `The_probes_are_left_out_and_named_as_unrestricted`,
   `A_grpc_fallback_is_left_out_and_named_as_unrestricted`; and in every
   host's floor, whose second list names each endpoint left out.
2. **A declaration made above the endpoint.** `RetrySafe` on a route group,
   or on the builder `MapGrpcService` returns, reaches endpoints mapped
   later: a keyed endpoint added to a declared group, a writing RPC added to
   a service declared `ReadOnly`. Pinned in Task 1:
   `RetrySafe_on_a_group_reaches_every_endpoint_mapped_in_it`; in Task 2:
   `A_declaration_on_a_group_makes_a_keyed_endpoint_inside_it_an_offender`
   and `A_write_declared_with_two_kinds_is_named`; and in Tasks 3 and 4,
   where the floor names each gRPC method, so a second one fails it.
3. **A floor nobody updated.** A new write that is correctly declared passes
   the offender list; only the floor can say the host gained it. Every floor
   is an exact `ShouldBe` over names, never a `ShouldContain`. Pinned in
   Task 6 by mutation: Payments' one GET turned into a POST fails both facts.
4. **A command the handler's signature does not show.** A command inside an
   `[AsParameters]` object, or built from a route value and a request
   record, is not a parameter, so the endpoint reads as not keyed. That
   fails closed, and `Idempotent<TCommand>()` is the way out. Pinned in
   Task 2:
   `A_command_bound_inside_a_parameter_object_is_not_seen_until_the_endpoint_says_so`,
   `An_endpoint_that_builds_its_command_is_keyed_by_saying_so`,
   `The_subject_rule_reaches_an_endpoint_keyed_by_declaration`, and
   `A_scan_of_the_wrong_assembly_is_named_rather_than_empty` for the other
   end of the same link.
5. **A scaffolded service's floor.** Catalog's floor names `PublishProduct`
   and a gRPC method; a rendered host maps neither, so the copy is born red
   unless the scaffold inverts it, and born ungated if the scaffold drops
   it. Pinned in Task 3: `CarriesTheWriteEndpointRule` in the scaffold's
   suite, and the dogfood, which runs the rendered suite for an API and for
   a worker.

## Measured basis of the selector

Measured on 2026-10-01 against `main` at `b07cd0b2`, by a throwaway test per
host that printed every endpoint in `EndpointDataSource` with its display
name, route pattern, `IHttpMethodMetadata`, endpoint name, handler
`MethodInfo` and metadata types. Inventory's `reinstate` row is PR-B's shape.

| Shape | `IHttpMethodMetadata` | Handler `MethodInfo` | Named by | Where |
|---|---|---|---|---|
| Route handler (`MapPost`, `MapPut`, `MapGet`) | the one verb | yes | `IEndpointNameMetadata` | all five hosts |
| OpenAPI document | `GET` | yes | display name `HTTP: GET /openapi/{documentName}.json` | the four services |
| gRPC method | `POST` | none | display name `gRPC - /<service>/<method>`; carries `Grpc.AspNetCore.Server.GrpcMethodMetadata` and the service class's `AuthorizeAttribute` | Catalog, Ordering |
| gRPC fallback, one per service | none | none | display name `gRPC - Unimplemented method for <service>`, pattern `<service>/{unimplementedMethod:grpcunimplemented}` | Catalog, Ordering |
| gRPC fallback, one per host | none | none | display name `gRPC - Unimplemented service`, pattern `{unimplementedService}/{unimplementedMethod:grpcunimplemented}` | Catalog, Ordering |
| §13.5 probe, three per host | none | none | display name `Health checks`; `AllowAnonymous` | every host, the gateway and Shipping's worker included |
| YARP route | the route's `Methods`, or none | none | the route id | the gateway |

The writes each host maps, by the name its floor uses:

| Host | Writes | Left out by shape |
|---|---|---|
| Catalog | `PublishProduct`, `gRPC - /catalog.pricing.v1.Pricing/GetPrices` | three probes, two gRPC fallbacks |
| Ordering | `PlaceOrder`, `CancelOrder`, `gRPC - /ordering.delivery.v1.DeliveryAddresses/Get` | three probes, two gRPC fallbacks |
| Inventory | `SetOnHand`, `ReleaseReservation`, `ReinstateReservation` | three probes |
| Payments | none; it maps `GetPayment` and the OpenAPI document | three probes |
| BFF | `Quote` | three probes |

Three things the measurement settled:

- **A gRPC fallback is not a POST.** The fallbacks carry no method metadata
  at all, and nothing but their display name marks them as gRPC's. They
  are the same shape as §13.5's probes: a bare `RequestDelegate` naming no
  method. The selector therefore reads the shape, not the package, and
  `Common.TestSupport` needs no reference to `Grpc.AspNetCore.Server`.
- **Metadata on `MapGrpcService`'s builder reaches the method endpoint and
  both fallbacks**, the host-wide one included. Measured by declaring
  `RetrySafe` on it and printing the table again. The fallbacks are not
  writes, so the kind they inherit is never read.
- **Every host's table can be read with no container**, from
  `HostSmokeTests.UnreachableInfrastructureFactory` for a service and from
  `BffFactory` for the BFF, so no `WriteEndpointRuleTests` joins the
  integration collection.

The selector that follows:

- An endpoint is a **write** when its method list holds POST, PUT, PATCH or
  DELETE, or when it names no method and has a handler `MethodInfo`.
- An endpoint is **unrestricted** when it names no method and has no handler
  `MethodInfo`. It is not a write, and each host's floor names every one.
- An endpoint is **keyed** when a handler parameter is an
  `IIdempotentCommand`, or it carries `IdempotentCommandMetadata`.

## File Structure

| File | Its one responsibility |
|---|---|
| Create `src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs` | `RetrySafety`, the two metadata records, `RetrySafe` and `Idempotent` |
| Create `tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs` | what the two declarations leave on an endpoint |
| Create `tests/Common.TestSupport/WriteEndpointRule.cs` | the gate: `Writes`, `Unrestricted`, and `Offenders` in two forms |
| Modify `tests/Common.TestSupport/Common.TestSupport.csproj` | the reference to `Common.Web` the gate's metadata types need |
| Create `tests/Common.Web.Tests/WriteEndpointRuleTests.cs` | the gate's own suite, over endpoints mapped for the purpose |
| Modify `tests/Common.Web.Tests/Common.Web.Tests.csproj` | the reference to `Common.TestSupport` that suite needs |
| Create `tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs` | Catalog's two facts; the scaffold's template for every service |
| Modify `src/Services/Catalog/Catalog.Api/Program.cs` | `PricingService` declared `ReadOnly` |
| Modify `tools/new-service/scaffold/render.py` | the new template file classified as copied |
| Modify `tools/new-service/scaffold/patch.py` | the moved `MapGrpcService` anchor, and the inverted floor |
| Modify `tools/new-service/test_new_service.py` | the rendered suite's shape for both hosts, and the file count |
| Create `tests/Ordering.Api.Tests/WriteEndpointRuleTests.cs` | Ordering's two facts |
| Modify `src/Services/Ordering/Ordering.Api/Program.cs` | `DeliveryAddressService` declared `ReadOnly` |
| Modify `src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs` | `CancelOrder` declared `Convergent` |
| Create `tests/Inventory.Api.Tests/WriteEndpointRuleTests.cs` | Inventory's two facts |
| Modify `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs` | `ReleaseReservation` declared `Convergent`; `ReinstateReservation` declared keyed |
| Modify `src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs` | `SetOnHand` declared `Convergent` |
| Modify `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs` | PR-B's by-name test removed, since the gate holds it by metadata |
| Create `tests/Payments.Api.Tests/WriteEndpointRuleTests.cs` | Payments' two facts, the floor being that it maps no write |
| Create `tests/Web.Bff.Tests/WriteEndpointRuleTests.cs` | the BFF's two facts |
| Modify `tests/Web.Bff.Tests/Web.Bff.Tests.csproj` | the reference to `Common.TestSupport` that suite needs |
| Modify `src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs` | `Quote` declared `ReadOnly` |
| Create `docs/backend-architecture/adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md` | the rule's record |
| Modify `docs/backend-architecture/appendix-a-adrs.md` | the ADR's index row |
| Modify `docs/backend-architecture/08-caching-redis.md` | §8.5's opening rule |

Line numbers below are `main` at `b07cd0b2`. PR-A and PR-B move some of
them; every edit is given by the text it replaces, which is what to trust.

---

### Task 0: Branch, and confirm what this plan stands on

**Files:** none.

**Interfaces:**
- Consumes: PR-B's four symbols and PR-A's ADR number, as *Global
  Constraints* lists them.
- Produces: a branch in a worktree, and the ADR number this PR uses.

- [ ] **Step 1: Confirm the tree is clean, then branch**

```bash
git status --short
```

Expected: no output. If `docs/superpowers/` files are listed, stop: the
docs PR that carries the spec and the three plans has not merged, and
`/branch` would carry them into a Class C pull request.

Run `/branch the write-endpoint rule and its gate`, then:

```bash
git branch --show-current
git rev-parse --show-toplevel
```

Expected: a `feat/…` name the command derived, never `main`; and a path
under `.claude/worktrees/`.

- [ ] **Step 2: Confirm PR-B's shape**

```bash
git grep -n "ReinstateReservationCommand(Guid CommandId, Guid OrderId)" -- src/Services/Inventory
git grep -n "record ReinstateReservationRequest(Guid CommandId)" -- src/Services/Inventory/Inventory.Api
git grep -n "new ReinstateReservationCommand(request.CommandId, orderId)" -- src/Services/Inventory/Inventory.Api
git grep -n "The_idempotent_command_is_reached_through_an_authenticated_admin_endpoint" -- tests/Inventory.Api.Tests
```

Expected: one line from each. A command that prints nothing means PR-B has
not merged or spelled that symbol differently; stop and read its diff
before going on.

- [ ] **Step 3: Fix the ADR number**

```bash
ls docs/backend-architecture/adr | tail -1
git grep -n "Every non-idempotent write command carries a client-generated" -- docs/backend-architecture/08-caching-redis.md
```

Expected: the first prints a file beginning `ADR-057-`, and the second
prints one line, §8.5's opening sentence. If the highest ADR is 056, PR-A
has not merged: wait for it, or go on and rebase before Task 8. If it is
above 057, take the highest plus one and use it for every `058` below. If
the second prints nothing, §8.5's opening rule has moved under this plan:
stop and reconcile Task 8's replacement with what is there.

- [ ] **Step 4: Confirm the baseline**

```bash
dotnet build Platform.slnx
docker info --format '{{.ServerVersion}}'
```

Expected: `0 Warning(s)` and `0 Error(s)`; and a version number. Without a
daemon Task 9's container run fails on `Failed to connect to Docker
endpoint`, which is a statement about the machine: start Docker Desktop
before Task 9, and do not skip the run.

---

### Task 1: The two declarations

**Files:**
- Create: `src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs`
- Test: `tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs`

**Interfaces:**
- Consumes: `IIdempotentCommand` from `Common.Application`, which
  `Common.Web` already references; `IEndpointConventionBuilder`,
  `RouteHandlerBuilder` and `WithMetadata` from ASP.NET Core.
- Produces:

```csharp
namespace Common.Web;

public enum RetrySafety { Convergent, ReadOnly }

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

Four types in one file, as the spec draws it; the corpus already does this
where the types are one thing, `PlaceOrderCommand.cs` among them.

- [ ] **Step 1: Write the failing test**

`tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs`:

```csharp
using Common.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What §8.5's two declarations leave on an endpoint (ADR-058).</summary>
public class RetrySafetyExtensionsTests
{
    private sealed record Keyed(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.keyed";
    }

    private sealed class Recording : IEndpointConventionBuilder
    {
        public List<Action<EndpointBuilder>> Conventions { get; } = [];

        public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);
    }

    [Fact]
    public void RetrySafe_leaves_its_kind_on_the_endpoint()
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        app.MapPut("/stock/{id:guid}", (Guid id) => Results.NoContent()).RetrySafe(RetrySafety.Convergent);

        Endpoint endpoint = Endpoints(app).ShouldHaveSingleItem();

        endpoint.Metadata
            .GetOrderedMetadata<RetrySafetyMetadata>()
            .ShouldBe([new RetrySafetyMetadata(RetrySafety.Convergent)]);
    }

    [Fact]
    public void RetrySafe_on_a_group_reaches_every_endpoint_mapped_in_it()
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        RouteGroupBuilder group = app.MapGroup("/v1").RetrySafe(RetrySafety.ReadOnly);
        group.MapPost("/quote", () => Results.Ok());
        group.MapPost("/estimate", () => Results.Ok());

        IReadOnlyList<Endpoint> endpoints = Endpoints(app);

        endpoints.Count.ShouldBe(2);
        foreach (Endpoint endpoint in endpoints)
        {
            endpoint.Metadata
                .GetOrderedMetadata<RetrySafetyMetadata>()
                .ShouldBe([new RetrySafetyMetadata(RetrySafety.ReadOnly)], endpoint.DisplayName);
        }
    }

    [Fact]
    public void RetrySafe_takes_any_convention_builder_and_hands_it_back()
    {
        // MapGrpcService's builder is not a RouteHandlerBuilder, and a gRPC service is declared on it (§9.7).
        Recording builder = new();

        Recording returned = builder.RetrySafe(RetrySafety.ReadOnly);

        returned.ShouldBeSameAs(builder);

        RouteEndpointBuilder endpoint = new(_ => Task.CompletedTask, RoutePatternFactory.Parse("/probe"), order: 0);
        foreach (Action<EndpointBuilder> convention in builder.Conventions)
            convention(endpoint);

        endpoint.Metadata.OfType<RetrySafetyMetadata>().ShouldBe([new RetrySafetyMetadata(RetrySafety.ReadOnly)]);
    }

    [Fact]
    public void Idempotent_names_the_command_the_handler_builds()
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        app.MapPost("/reservations/{id:guid}/reinstate", (Guid id) => Results.NoContent()).Idempotent<Keyed>();

        Endpoint endpoint = Endpoints(app).ShouldHaveSingleItem();

        endpoint.Metadata
            .GetOrderedMetadata<IdempotentCommandMetadata>()
            .ShouldBe([new IdempotentCommandMetadata(typeof(Keyed))]);
    }

    private static IReadOnlyList<Endpoint> Endpoints(IEndpointRouteBuilder app) =>
        [.. app.DataSources.SelectMany(source => source.Endpoints)];
}
```

Then `unix2dos tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs`.

- [ ] **Step 2: Run to see it fail**

Run:

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~RetrySafetyExtensionsTests"
```

Expected: a compile failure in `RetrySafetyExtensionsTests.cs` —
`error CS1061: 'RouteHandlerBuilder' does not contain a definition for
'RetrySafe'`, `error CS0103: The name 'RetrySafety' does not exist in the
current context` and `error CS0246: The type or namespace name
'RetrySafetyMetadata' could not be found`.

- [ ] **Step 3: Write the declarations**

`src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs`:

```csharp
using Common.Application;
using Microsoft.AspNetCore.Builder;

namespace Common.Web;

/// <summary>Why a repeat of a write endpoint's request is harmless, for an endpoint that is not keyed (§8.5).</summary>
public enum RetrySafety
{
    /// <summary>A repeat of the same request leaves the state the first left, and is answered as it was.</summary>
    Convergent,

    /// <summary>The endpoint writes nothing.</summary>
    ReadOnly
}

/// <summary>The kind <see cref="RetrySafetyExtensions.RetrySafe{TBuilder}"/> declared.</summary>
public sealed record RetrySafetyMetadata(RetrySafety Kind);

/// <summary>The <see cref="IIdempotentCommand"/> an endpoint builds from its request rather than binds.</summary>
public sealed record IdempotentCommandMetadata(Type Command);

/// <summary>The two declarations a write endpoint makes where its handler's signature cannot (ADR-058).</summary>
public static class RetrySafetyExtensions
{
    /// <summary>Declares that a repeat is harmless, and which kind of harmless.</summary>
    /// <remarks>Over any builder, since a gRPC service is declared on <c>MapGrpcService</c>'s own (§9.7).</remarks>
    public static TBuilder RetrySafe<TBuilder>(this TBuilder builder, RetrySafety kind)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RetrySafetyMetadata(kind));

    /// <summary>Declares the endpoint keyed by <typeparamref name="TCommand"/>, which its handler constructs.</summary>
    public static RouteHandlerBuilder Idempotent<TCommand>(this RouteHandlerBuilder builder)
        where TCommand : IIdempotentCommand =>
        builder.WithMetadata(new IdempotentCommandMetadata(typeof(TCommand)));
}
```

Then `unix2dos src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs`.

- [ ] **Step 4: Run to see it pass**

Run:

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~RetrySafetyExtensionsTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4
```

- [ ] **Step 5: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs
git commit -m "feat(web): RetrySafe and Idempotent declare what a repeat of a write endpoint does" -m "A write endpoint that binds no IIdempotentCommand had nowhere to say why a repeat of its request is harmless, and one that builds its command from a route value and a request record had nowhere to say it is keyed. RetrySafetyMetadata and IdempotentCommandMetadata are that place: metadata on the route, where a gate can read it and a reviewer reads the reason beside it.

RetrySafe is generic over the builder because a gRPC service is declared on the builder MapGrpcService returns, which is no RouteHandlerBuilder; Idempotent is constrained to IIdempotentCommand, so the declaration cannot name a command the pipeline would not protect. Nothing reads either yet."
```

Expected from the first command: both new files on a line showing `w/crlf`.

---

### Task 2: `WriteEndpointRule` and its own suite

**Files:**
- Create: `tests/Common.TestSupport/WriteEndpointRule.cs`
- Modify: `tests/Common.TestSupport/Common.TestSupport.csproj:27-30`
- Modify: `tests/Common.Web.Tests/Common.Web.Tests.csproj:29-31`
- Test: `tests/Common.Web.Tests/WriteEndpointRuleTests.cs`

**Interfaces:**
- Consumes: `RetrySafety`, `RetrySafetyMetadata`, `IdempotentCommandMetadata`
  and the two extension methods from Task 1; `IIdempotentCommand`;
  `Endpoint`, `IHttpMethodMetadata`, `IEndpointNameMetadata`, `IAllowAnonymous`
  and `IAuthorizeData` from ASP.NET Core.
- Produces:

```csharp
namespace Common.TestSupport;

public static class WriteEndpointRule
{
    public static IReadOnlyList<Endpoint> Writes(IEnumerable<Endpoint> endpoints);
    public static IReadOnlyList<Endpoint> Unrestricted(IEnumerable<Endpoint> endpoints);
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints);
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints, Assembly application);
}
```

All four members are the spec's. Two of them are forced by what a host can
give the gate:

- `Offenders(endpoints, application)` is where "a keyed command no endpoint
  reaches" lives. That offence needs the host's Application assembly, which
  the one-argument form cannot be given, and the BFF has none to
  give. The overload returns everything the first form returns, then
  compares the assembly's idempotent commands with the commands endpoints
  reach in both directions, so a scan pointed at the wrong assembly is
  named instead of finding nothing.
- `Unrestricted` is the floor for what `Writes` leaves out by shape. The
  selection cannot tell §13.5's probes and gRPC's fallbacks from a route a
  host maps the same way, so each host's suite names them.

`Offenders` names a write that is neither keyed nor declared; one that is
both; one that declares two kinds; a keyed endpoint that allows anonymous
callers; a keyed endpoint with no `IAuthorizeData`; and, in the two-argument
form, a command no endpoint reaches and an endpoint keyed by a command the
assembly does not declare. "Declares two kinds" is in the spec's list;
it follows from "it carries a `RetrySafety` kind", singular, and a group
declared one way with an endpoint declared the other is the case.

The suite lives in `Common.Web.Tests` because `Common.TestSupport` is not a
test project and no building-block suite references it; `Common.Web.Tests`
is the suite of the block whose metadata the gate reads. The endpoints are
mapped on a `WebApplication` that is built and never started, so each
carries the metadata routing really gives it; the gRPC shapes are built by
hand to the measured table, because this suite has no gRPC package and the
real ones are under Catalog's and Ordering's floors.

- [ ] **Step 1: Draw the two references**

In `tests/Common.TestSupport/Common.TestSupport.csproj`, replace

```xml
    <!-- The outbox, inbox and marker types and the passes the fixture drives (§9.4, §9.5, §8.5). -->
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Infrastructure\Common.Infrastructure.csproj" />
```

with

```xml
    <!-- The outbox, inbox and marker types and the passes the fixture drives (§9.4, §9.5, §8.5). -->
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Infrastructure\Common.Infrastructure.csproj" />
    <!-- RetrySafetyMetadata and IdempotentCommandMetadata, which WriteEndpointRule reads (ADR-058). -->
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Web\Common.Web.csproj" />
```

In `tests/Common.Web.Tests/Common.Web.Tests.csproj`, replace

```xml
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Web\Common.Web.csproj" />
```

with

```xml
    <ProjectReference Include="..\..\src\BuildingBlocks\Common.Web\Common.Web.csproj" />
    <!-- WriteEndpointRule, whose unit suite sits beside the metadata it reads (ADR-058). -->
    <ProjectReference Include="..\Common.TestSupport\Common.TestSupport.csproj" />
```

No architecture test refuses either: §4.2's cross-service gate reads a
service's five assemblies, and ADR-056 has `Common.TestSupport` reference
building blocks and no service.

- [ ] **Step 2: Write the failing test**

`tests/Common.Web.Tests/WriteEndpointRuleTests.cs`:

```csharp
using Common.Application;
using Common.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What <see cref="WriteEndpointRule"/> selects and names, over endpoints mapped for the purpose.</summary>
public class WriteEndpointRuleTests
{
    public sealed record Reached(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.reached";
    }

    public sealed record Built(Guid CommandId, Guid Id) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.built";
    }

    public sealed record Unreached(Guid CommandId) : ICommand<Result>, IIdempotentCommand
    {
        public static string OperationName => "probe.unreached";
    }

    public sealed record Wrapper(Guid Id, [FromBody] Reached Command);

    [Fact]
    public void A_table_that_keeps_the_rule_has_no_offender()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).RequireAuthorization().WithName("Keyed");
            app
                .MapPut("/set/{id:guid}", (Guid id) => Results.NoContent())
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Set");
            app.MapGet("/read", () => Results.Ok()).WithName("Read");
        });

        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Keyed", "Set"]);
    }

    [Fact]
    public void An_undeclared_write_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/cancel/{id:guid}", (Guid id) => Results.NoContent())
                .RequireAuthorization()
                .WithName("Cancel"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Cancel accepts a write and is neither keyed nor declared retry-safe");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Every_write_verb_is_a_write(string verb)
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapMethods("/thing/{id:guid}", [verb], (Guid id) => Results.NoContent()).WithName("Thing"));

        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Thing"]);
        WriteEndpointRule.Offenders(endpoints).ShouldHaveSingleItem().ShouldStartWith("Thing accepts a write");
    }

    [Fact]
    public void A_write_that_is_both_keyed_and_declared_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/keyed", (Reached command) => Results.NoContent())
                .RequireAuthorization()
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Keyed"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and declared Convergent");
    }

    [Fact]
    public void A_declaration_on_a_group_makes_a_keyed_endpoint_inside_it_an_offender()
    {
        // The group's metadata reaches every endpoint mapped in it, a later one included.
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            RouteGroupBuilder group = app.MapGroup("/v1").RequireAuthorization().RetrySafe(RetrySafety.ReadOnly);
            group.MapPost("/quote", () => Results.Ok()).WithName("Quote");
            group.MapPost("/keyed", (Reached command) => Results.NoContent()).WithName("Keyed");
        });

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and declared ReadOnly");
    }

    [Fact]
    public void A_write_declared_with_two_kinds_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            RouteGroupBuilder group = app.MapGroup("/v1").RetrySafe(RetrySafety.ReadOnly);
            group
                .MapPut("/set/{id:guid}", (Guid id) => Results.NoContent())
                .RetrySafe(RetrySafety.Convergent)
                .WithName("Set");
        });

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Set declares ReadOnly and Convergent");
    }

    [Fact]
    public void A_keyed_endpoint_that_allows_anonymous_callers_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapGroup("/v1")
                .RequireAuthorization()
                .MapPost("/keyed", (Reached command) => Results.NoContent())
                .AllowAnonymous()
                .WithName("Keyed"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and allows anonymous callers");
    }

    [Fact]
    public void A_keyed_endpoint_with_no_authorisation_data_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).WithName("Keyed"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Keyed is keyed by Reached and requires no authorisation");
    }

    [Fact]
    public void An_endpoint_that_builds_its_command_is_keyed_by_saying_so()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app
                .MapPost("/built/{id:guid}", (Guid id) => Results.NoContent())
                .RequireAuthorization()
                .Idempotent<Built>()
                .WithName("Built"));

        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
    }

    [Fact]
    public void The_subject_rule_reaches_an_endpoint_keyed_by_declaration()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/built/{id:guid}", (Guid id) => Results.NoContent()).Idempotent<Built>().WithName("Built"));

        WriteEndpointRule
            .Offenders(endpoints)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Built is keyed by Built and requires no authorisation");
    }

    [Fact]
    public void A_command_bound_inside_a_parameter_object_is_not_seen_until_the_endpoint_says_so()
    {
        // The selector reads the handler's own parameters, so a command one level down fails closed.
        IReadOnlyList<Endpoint> hidden = Map(app =>
            app
                .MapPost("/wrapped/{id:guid}", ([AsParameters] Wrapper request) => Results.NoContent())
                .RequireAuthorization()
                .WithName("Wrapped"));

        WriteEndpointRule
            .Offenders(hidden)
            .ShouldHaveSingleItem()
            .ShouldStartWith("Wrapped accepts a write and is neither keyed nor declared retry-safe");

        IReadOnlyList<Endpoint> declared = Map(app =>
            app
                .MapPost("/wrapped/{id:guid}", ([AsParameters] Wrapper request) => Results.NoContent())
                .RequireAuthorization()
                .Idempotent<Reached>()
                .WithName("Wrapped"));

        WriteEndpointRule.Offenders(declared).ShouldBeEmpty();
    }

    [Fact]
    public void A_get_is_ignored()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app => app.MapGet("/read", () => Results.Ok()).WithName("Read"));

        WriteEndpointRule.Writes(endpoints).ShouldBeEmpty();
        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
    }

    [Fact]
    public void A_route_handler_mapped_with_no_method_is_a_write()
    {
        // Map with a handler and no verb answers a POST as readily as a GET.
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.Map("/anything/{id:guid}", (Guid id) => Results.NoContent()).WithName("Anything"));

        endpoints.ShouldHaveSingleItem().Metadata.GetMetadata<IHttpMethodMetadata>().ShouldBeNull();
        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Anything"]);
        WriteEndpointRule.Unrestricted(endpoints).ShouldBeEmpty();
        WriteEndpointRule.Offenders(endpoints).ShouldHaveSingleItem().ShouldStartWith("Anything accepts a write");
    }

    [Fact]
    public void A_handler_mapped_for_a_read_and_a_write_is_a_write()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapMethods("/both", ["GET", "POST"], () => Results.Ok()).WithName("Both"));

        Names(WriteEndpointRule.Writes(endpoints)).ShouldBe(["Both"]);
    }

    [Fact]
    public void A_grpc_method_is_a_write_and_is_named_until_its_service_is_declared()
    {
        // A gRPC method as routing holds it: a POST with no handler MethodInfo, named by its display name alone.
        Endpoint undeclared = Bare("gRPC - /probe.v1.Probe/Get", new HttpMethodMetadata(["POST"]));
        Endpoint declared = Bare(
            "gRPC - /probe.v1.Probe/Get",
            new HttpMethodMetadata(["POST"]),
            new RetrySafetyMetadata(RetrySafety.ReadOnly));

        WriteEndpointRule.Writes([undeclared]).ShouldHaveSingleItem();
        WriteEndpointRule
            .Offenders([undeclared])
            .ShouldHaveSingleItem()
            .ShouldStartWith("gRPC - /probe.v1.Probe/Get accepts a write and is neither keyed nor declared");

        WriteEndpointRule.Writes([declared]).ShouldHaveSingleItem();
        WriteEndpointRule.Offenders([declared]).ShouldBeEmpty();
    }

    [Fact]
    public void A_grpc_fallback_is_left_out_and_named_as_unrestricted()
    {
        // A bare RequestDelegate naming no method, which the service's own declaration reaches.
        Endpoint fallback = Bare("gRPC - Unimplemented service", new RetrySafetyMetadata(RetrySafety.ReadOnly));

        WriteEndpointRule.Writes([fallback]).ShouldBeEmpty();
        WriteEndpointRule.Offenders([fallback]).ShouldBeEmpty();
        WriteEndpointRule.Unrestricted([fallback]).ShouldBe([fallback]);
    }

    [Fact]
    public void The_probes_are_left_out_and_named_as_unrestricted()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHealthChecks();
        using WebApplication app = builder.Build();

        app.MapCommonHealthEndpoints(ownsNoReadinessDependencies: true);

        IReadOnlyList<Endpoint> endpoints = [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(s => s.Endpoints)];

        WriteEndpointRule.Writes(endpoints).ShouldBeEmpty();
        WriteEndpointRule.Offenders(endpoints).ShouldBeEmpty();
        WriteEndpointRule
            .Unrestricted(endpoints)
            .Select(endpoint => endpoint.DisplayName)
            .ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    [Fact]
    public void A_request_delegate_a_host_maps_with_no_method_is_unrestricted_and_not_a_write()
    {
        // The shape the selection cannot tell from the framework's own, which is why a host's suite names each.
        RequestDelegate raw = _ => Task.CompletedTask;
        IReadOnlyList<Endpoint> endpoints = Map(app => app.Map("/raw", raw).WithName("Raw"));

        WriteEndpointRule.Writes(endpoints).ShouldBeEmpty();
        Names(WriteEndpointRule.Unrestricted(endpoints)).ShouldBe(["Raw"]);
    }

    [Fact]
    public void A_keyed_command_no_endpoint_reaches_is_named()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
        {
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).RequireAuthorization();
            app
                .MapPost("/built/{id:guid}", (Guid id) => Results.NoContent())
                .RequireAuthorization()
                .Idempotent<Built>();
        });

        IReadOnlyList<string> offenders =
            WriteEndpointRule.Offenders(endpoints, typeof(WriteEndpointRuleTests).Assembly);

        offenders.ShouldContain(offender =>
            offender.StartsWith("Unreached is an idempotent command no endpoint reaches", StringComparison.Ordinal));
        offenders.ShouldNotContain(offender => offender.StartsWith("Reached ", StringComparison.Ordinal));
        offenders.ShouldNotContain(offender => offender.StartsWith("Built ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scan_of_the_wrong_assembly_is_named_rather_than_empty()
    {
        IReadOnlyList<Endpoint> endpoints = Map(app =>
            app.MapPost("/keyed", (Reached command) => Results.NoContent()).RequireAuthorization());

        WriteEndpointRule
            .Offenders(endpoints, typeof(IIdempotentCommand).Assembly)
            .ShouldHaveSingleItem()
            .ShouldBe(
                "Reached keys an endpoint and is not an idempotent command of Common.Application, " +
                "so this scan is reading the wrong assembly");
    }

    private static IReadOnlyList<Endpoint> Map(Action<IEndpointRouteBuilder> map)
    {
        using WebApplication app = WebApplication.CreateSlimBuilder().Build();

        map(app);

        return [.. ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)];
    }

    private static Endpoint Bare(string displayName, params object[] metadata) =>
        new(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), displayName);

    private static string?[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)];
}
```

Then `unix2dos tests/Common.Web.Tests/WriteEndpointRuleTests.cs`.

- [ ] **Step 3: Run to see it fail**

Run:

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: a compile failure — `error CS0103: The name 'WriteEndpointRule'
does not exist in the current context`.

- [ ] **Step 4: Write the gate**

`tests/Common.TestSupport/WriteEndpointRule.cs`:

```csharp
using System.Reflection;
using Common.Application;
using Common.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Common.TestSupport;

/// <summary>§8.5's rule over a host's endpoint table: a write is keyed, or declares why a repeat is harmless.</summary>
/// <remarks>ADR-058 owns the rule and the hosts outside it; a host's suite pins what this selects.</remarks>
public static class WriteEndpointRule
{
    private static readonly string[] WriteVerbs =
    [
        HttpMethods.Post,
        HttpMethods.Put,
        HttpMethods.Patch,
        HttpMethods.Delete
    ];

    /// <summary>The endpoints a write verb reaches.</summary>
    public static IReadOnlyList<Endpoint> Writes(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Where(AcceptsAWrite)];

    /// <summary>The endpoints naming neither a method nor a handler, which <see cref="Writes"/> leaves out.</summary>
    /// <remarks>The framework's own, so a host's suite names each and a route of its own shows (ADR-058).</remarks>
    public static IReadOnlyList<Endpoint> Unrestricted(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Where(endpoint => NamesNoMethod(endpoint) && !HasHandler(endpoint))];

    /// <summary>Every endpoint that breaks the rule, each as a sentence naming it.</summary>
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints)
    {
        List<string> offenders = [];

        foreach (Endpoint endpoint in endpoints)
        {
            string name = NameOf(endpoint);
            string[] commands = [.. CommandsOf(endpoint).Select(command => command.Name)];
            RetrySafety[] kinds = [.. KindsOf(endpoint)];

            if (commands.Length > 0)
                offenders.AddRange(Unauthenticated(endpoint, name, commands[0]));

            if (!AcceptsAWrite(endpoint))
                continue;

            if (commands.Length == 0 && kinds.Length == 0)
            {
                offenders.Add(
                    $"{name} accepts a write and is neither keyed nor declared retry-safe: bind an " +
                    "IIdempotentCommand, or say .Idempotent<TCommand>() or .RetrySafe(kind) (§8.5)");
            }

            if (commands.Length > 0 && kinds.Length > 0)
            {
                offenders.Add(
                    $"{name} is keyed by {commands[0]} and declared {kinds[0]}: a write endpoint is one or " +
                    "the other, and a declaration on its group reaches it (§8.5)");
            }

            if (kinds.Length > 1)
                offenders.Add($"{name} declares {string.Join(" and ", kinds)}: a repeat is harmless for one reason");
        }

        return offenders;
    }

    /// <summary>The same, and every idempotent command of <paramref name="application"/> no endpoint reaches.</summary>
    /// <remarks>Both directions, so a scan of the wrong assembly fails rather than finds nothing (§8.5).</remarks>
    public static IReadOnlyList<string> Offenders(IEnumerable<Endpoint> endpoints, Assembly application)
    {
        Endpoint[] table = [.. endpoints];
        List<string> offenders = [.. Offenders(table)];

        Type[] declared =
        [
            .. application
                .GetTypes()
                .Where(typeof(IIdempotentCommand).IsAssignableFrom)
                .Where(type => type is { IsClass: true, IsAbstract: false })
        ];
        Type[] reached = [.. table.SelectMany(CommandsOf).Distinct()];

        offenders.AddRange(
            declared
                .Except(reached)
                .Select(command =>
                    $"{command.Name} is an idempotent command no endpoint reaches: one reached only by a " +
                    "consumer is the inbox's to deduplicate and declares no IIdempotentCommand (§8.5, §9.5)"));

        offenders.AddRange(
            reached
                .Except(declared)
                .Select(command =>
                    $"{command.Name} keys an endpoint and is not an idempotent command of " +
                    $"{application.GetName().Name}, so this scan is reading the wrong assembly"));

        return offenders;
    }

    // An endpoint naming no method takes every verb. A bare RequestDelegate naming none is how the framework
    // maps §13.5's probes and gRPC's fallbacks, so only a route handler is read that way (ADR-058).
    private static bool AcceptsAWrite(Endpoint endpoint)
    {
        if (NamesNoMethod(endpoint))
            return HasHandler(endpoint);

        return endpoint.Metadata
            .GetMetadata<IHttpMethodMetadata>()!
            .HttpMethods
            .Any(method => WriteVerbs.Contains(method, StringComparer.OrdinalIgnoreCase));
    }

    private static bool NamesNoMethod(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is null or [];

    private static bool HasHandler(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<MethodInfo>() is not null;

    // Keyed by a handler parameter, or by the declaration an endpoint makes when it builds the command itself.
    private static Type[] CommandsOf(Endpoint endpoint) =>
    [
        .. (endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters() ?? [])
            .Select(parameter => parameter.ParameterType)
            .Where(typeof(IIdempotentCommand).IsAssignableFrom)
            .Concat(endpoint.Metadata.GetOrderedMetadata<IdempotentCommandMetadata>().Select(keyed => keyed.Command))
            .Distinct()
    ];

    private static IEnumerable<RetrySafety> KindsOf(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<RetrySafetyMetadata>().Select(declared => declared.Kind).Distinct();

    // §8.5's subject rule: an anonymous caller claims under the shared system subject.
    private static IEnumerable<string> Unauthenticated(Endpoint endpoint, string name, string command)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            yield return
                $"{name} is keyed by {command} and allows anonymous callers, who all claim under the " +
                "shared system subject (§8.5)";
        }

        if (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
        {
            yield return
                $"{name} is keyed by {command} and requires no authorisation, so the caller has no " +
                "subject to key on (§8.5)";
        }
    }

    private static string NameOf(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ??
        endpoint.DisplayName ??
        "an unnamed endpoint";
}
```

Then `unix2dos tests/Common.TestSupport/WriteEndpointRule.cs`.

- [ ] **Step 5: Run to see it pass**

Run:

```bash
dotnet test tests/Common.Web.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:    22, Skipped:     0, Total:    22
```

Then the whole suite and the solution, since two project files moved:

```bash
dotnet test tests/Common.Web.Tests
dotnet build Platform.slnx
```

Expected: `Failed:     0` from the first; `0 Warning(s)` and `0 Error(s)`
from the second.

- [ ] **Step 6: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add tests/Common.TestSupport/WriteEndpointRule.cs tests/Common.TestSupport/Common.TestSupport.csproj tests/Common.Web.Tests/WriteEndpointRuleTests.cs tests/Common.Web.Tests/Common.Web.Tests.csproj
git commit -m "test: WriteEndpointRule names a write endpoint that is neither keyed nor declared retry-safe" -m "The gate behind ADR-058, once, for every host. Writes selects the endpoints a write verb reaches; Offenders names one that is neither keyed nor declared, one that is both, one that declares two kinds, and a keyed endpoint an anonymous caller can reach, which is §8.5's subject rule reaching the declared form. The overload taking the host's Application assembly adds the idempotent command no endpoint reaches, and compares in both directions so that a scan of the wrong assembly is named instead of finding nothing.

An endpoint naming no method takes every verb. With a handler it is a write. As a bare RequestDelegate it is how the framework maps §13.5's probes and gRPC's fallbacks, measured, and nothing else marks those, so Unrestricted returns them for a host's suite to name.

It sits in Common.TestSupport, which gains a reference to Common.Web for the two metadata types, and its own suite sits in Common.Web.Tests, which gains one to Common.TestSupport. No host holds it yet."
```

---

### Task 3: Catalog, and the scaffold that renders it

**Files:**
- Create: `tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs`
- Modify: `src/Services/Catalog/Catalog.Api/Program.cs:56`
- Modify: `tools/new-service/scaffold/render.py:93`
- Modify: `tools/new-service/scaffold/patch.py:192` and before `:594`
- Modify: `tools/new-service/test_new_service.py:323` and `:2389`

**Interfaces:**
- Consumes: `WriteEndpointRule` from Task 2, reached through
  `Catalog.TestSupport`'s reference to `Common.TestSupport`; `RetrySafe`
  from Task 1; `HostSmokeTests.UnreachableInfrastructureFactory`, which
  builds the host with no container.
- Produces: Catalog's `WriteEndpointRuleTests`, which is also the text every
  rendered service's suite is made from.

A Catalog file is added and a line the scaffold anchors on is changed, so
the scaffold reconciles in this task and the commit holds both: a commit
with one and not the other leaves the scaffold's suite red.

- [ ] **Step 1: Write the failing test**

`tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs`:

```csharp
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Api.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        WriteEndpointRule
            .Offenders(Endpoints, typeof(Catalog.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(
            ["PublishProduct", "gRPC - /catalog.pricing.v1.Pricing/GetPrices"]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(
            [
                "Health checks",
                "Health checks",
                "Health checks",
                "gRPC - Unimplemented method for catalog.pricing.v1.Pricing",
                "gRPC - Unimplemented service"
            ]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
```

Then `unix2dos tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs`.

The two `ShouldBe` statements are the scaffold's anchors in Step 6. Keep
their text exactly as written.

- [ ] **Step 2: Run to see it fail**

Run:

```bash
dotnet test tests/Catalog.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: `Failed:     1, Passed:     1`. The floor passes, because the
selection does not depend on a declaration. The rule fails with one offender:

```
["gRPC - /catalog.pricing.v1.Pricing/GetPrices accepts a write and is neither keyed nor declared retry-safe: bind an IIdempotentCommand, or say .Idempotent<TCommand>() or .RetrySafe(kind) (§8.5)"]
```

`PublishProduct` is not named: its handler takes `PublishProductCommand`.

- [ ] **Step 3: Declare the gRPC service**

In `src/Services/Catalog/Catalog.Api/Program.cs`, replace

```csharp
app.MapGrpcService<PricingService>();
```

with

```csharp
app.MapGrpcService<PricingService>().RetrySafe(RetrySafety.ReadOnly);   // §9.7 — GetPrices reads, and writes nothing
```

The four comment lines above it stay as they are, and the file already has
`using Common.Web;`.

- [ ] **Step 4: Run to see it pass**

Run:

```bash
dotnet test tests/Catalog.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

- [ ] **Step 5: Run the scaffold's suite to see it fail, then write its tests**

```bash
cd tools/new-service && py -3.12 -m unittest; cd ../..
```

Expected: `FAILED`, with most of the suite failing on one refusal —
`tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs is not classified. Add
it to COPIED if every service needs it, or to OMITTED if it belongs to
Catalog's slice`.

In `tools/new-service/test_new_service.py`, insert this class above
`class OmitsTheSlice(unittest.TestCase):`, with two blank lines after it:

```python
class CarriesTheWriteEndpointRule(unittest.TestCase):
    """ADR-058's gate travels to both host shapes, over a floor that says the host maps no write."""

    def suites(self):
        yield render().created[f"tests/{PROBE}.Api.Tests/WriteEndpointRuleTests.cs"]
        yield worker().created[f"tests/{PROBE}.Worker.Tests/WriteEndpointRuleTests.cs"]

    def test_the_rule_reads_the_rendered_service_s_own_commands(self):
        for suite in self.suites():
            self.assertIn(
                f".Offenders(Endpoints, typeof({PROBE}.Application.DependencyInjection).Assembly)", suite)

    def test_the_floor_is_inverted_and_still_looks_at_a_table(self):
        # A floor naming the template's writes would fail on a host that maps
        # none, and one deleted would leave the rule green over nothing.
        for suite in self.suites():
            self.assertIn("public void This_host_maps_no_write_for_the_rule_above_to_look_at_yet()", suite)
            self.assertIn("Names(WriteEndpointRule.Writes(Endpoints)).ShouldBeEmpty(", suite)
            self.assertIn(
                "Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe("
                '["Health checks", "Health checks", "Health checks"]);', suite)
            self.assertNotIn("gRPC", suite)
```

and, in `test_a_successful_run_reports_what_it_wrote_and_exits_zero`, replace

```python
            self.assertIn("70 files created, 6 updated", out)
```

with

```python
            self.assertIn("71 files created, 6 updated", out)
```

That number is the test's own pin, and the render writes one file more.

- [ ] **Step 6: Reconcile the scaffold**

In `tools/new-service/scaffold/render.py`, inside `COPIED`, replace

```python
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
```

with

```python
        "tests/Catalog.Api.Tests/TransientFaultInjection.cs",
        # ADR-058's gate travels, so a rendered host is born under the rule;
        # PATCHES inverts the floor that names the template's own writes.
        "tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs",
```

In `tools/new-service/scaffold/patch.py`, in the `Program.cs` entry, replace

```python
            "app.MapGrpcService<PricingService>();\n",
```

with

```python
            "app.MapGrpcService<PricingService>().RetrySafe(RetrySafety.ReadOnly);"
            "   // §9.7 — GetPrices reads, and writes nothing\n",
```

and insert this entry above
`"tests/Catalog.Api.Tests/DatabaseSmokeTests.cs": (`:

```python
    # ADR-058's floor names the writes the template maps, and a rendered host
    # maps none, so it is inverted on IdempotencyOptInTests' argument above:
    # the test fails the day the service maps its first write, and says what
    # to restore. The second list loses the template's gRPC fallbacks.
    "tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs": (
        (
            "    public void The_rule_above_is_looking_at_the_writes_this_host_maps()\n",
            "    public void This_host_maps_no_write_for_the_rule_above_to_look_at_yet()\n",
        ),
        (
            "        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(\n"
            '            ["PublishProduct", "gRPC - /catalog.pricing.v1.Pricing/GetPrices"]);\n',
            "        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBeEmpty(\n"
            '            "This host maps no write endpoint yet, so the rule above is '
            'vacuous. The day it maps " +\n'
            '            "one, this test fails — replace it with the ShouldBe form '
            'naming that endpoint, " +\n'
            '            "which is what keeps a vacuous gate from quietly becoming '
            'a permanent one (§8.5).");\n',
        ),
        (
            "        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(\n"
            "            [\n"
            '                "Health checks",\n'
            '                "Health checks",\n'
            '                "Health checks",\n'
            '                "gRPC - Unimplemented method for catalog.pricing.v1.Pricing",\n'
            '                "gRPC - Unimplemented service"\n'
            "            ]);\n",
            "        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe("
            '["Health checks", "Health checks", "Health checks"]);\n',
        ),
    ),
```

The file is copied for a worker too. `WORKER_PATCHES` has no way to drop a
file, and none is wanted: a worker's table is its three probes, so the same
inverted floor is true of it.

- [ ] **Step 7: Run the scaffold's suite to see it pass**

```bash
cd tools/new-service && py -3.12 -m unittest; cd ../..
```

Expected: `OK`, over two tests more than the suite ran before this task.

- [ ] **Step 8: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add tests/Catalog.Api.Tests/WriteEndpointRuleTests.cs src/Services/Catalog/Catalog.Api/Program.cs tools/new-service/scaffold/render.py tools/new-service/scaffold/patch.py tools/new-service/test_new_service.py
git commit -m "feat(catalog): PricingService declares ReadOnly, and the scaffold renders WriteEndpointRuleTests" -m "Catalog maps two writes. PublishProduct binds its command and needs nothing. The pricing RPC is a POST to routing and reads prices, so its service is declared ReadOnly on the builder MapGrpcService returns; the floor names the method, which is what makes a second RPC on that service a failed test and not a silently inherited declaration.

The suite is a Catalog file, so the scaffold classifies it as copied and a rendered host is born under the rule. Its floor names Catalog's writes and Catalog's gRPC fallbacks, neither of which a rendered host maps, so both lists are patched: the first is inverted, on the argument IdempotencyOptInTests' floors already make, and the second keeps the three probes. The Program.cs patch that removes the pricing hop anchors on the line this commit changes, and moves with it."
```

- [ ] **Step 9: Dogfood both host shapes**

`tests/Catalog.*` changed, and the scaffold's suite reads rendered text
without compiling it. Step 8 comes first on purpose: the cleanup below
checks out four tracked paths, and nothing of this task may still be
uncommitted when it runs.

```bash
py -3.12 tools/new-service/new_service.py Yankee --port 5199
dotnet build tests/Yankee.Api.Tests/Yankee.Api.Tests.csproj
dotnet test tests/Yankee.Api.Tests --no-build --filter "FullyQualifiedName~WriteEndpointRuleTests"
py -3.12 tools/new-service/new_service.py Xray --worker
dotnet build tests/Xray.Worker.Tests/Xray.Worker.Tests.csproj
dotnet test tests/Xray.Worker.Tests --no-build --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected, in order: `Yankee: 71 files created, 8 updated, API on port
5199.`; `0 Error(s)`; `Passed!  - Failed:     0, Passed:     2`;
`Xray: 71 files created, 8 updated, publishing no port.`; `0 Error(s)`;
`Passed!  - Failed:     0, Passed:     2`.

Then the cleanup, and the proof it was complete:

```bash
rm -rf src/Services/Yankee tests/Yankee.* deploy/compose/services/yankee.yml
rm -rf src/Services/Xray tests/Xray.* deploy/compose/services/xray.yml
git checkout -- Platform.slnx deploy/compose/ .github/secret-scan/allowed/ src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs
git status --short
```

Expected: no output from `git status`. A fix the render exposed is its own
commit, made with `git add -p` before the cleanup, never copied aside.

---

### Task 4: Ordering

**Files:**
- Create: `tests/Ordering.Api.Tests/WriteEndpointRuleTests.cs`
- Modify: `src/Services/Ordering/Ordering.Api/Program.cs:54`
- Modify: `src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs:60-61`

**Interfaces:**
- Consumes: `WriteEndpointRule`, `RetrySafe`, and Ordering's
  `HostSmokeTests.UnreachableInfrastructureFactory`.
- Produces: Ordering's two facts.

- [ ] **Step 1: Write the failing test**

`tests/Ordering.Api.Tests/WriteEndpointRuleTests.cs`:

```csharp
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        WriteEndpointRule
            .Offenders(Endpoints, typeof(Ordering.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(
            ["CancelOrder", "PlaceOrder", "gRPC - /ordering.delivery.v1.DeliveryAddresses/Get"]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(
            [
                "Health checks",
                "Health checks",
                "Health checks",
                "gRPC - Unimplemented method for ordering.delivery.v1.DeliveryAddresses",
                "gRPC - Unimplemented service"
            ]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
```

Then `unix2dos tests/Ordering.Api.Tests/WriteEndpointRuleTests.cs`.

- [ ] **Step 2: Run to see it fail**

Run:

```bash
dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: `Failed:     1, Passed:     1`, the rule failing with two
offenders, each ending as Catalog's did:
`gRPC - /ordering.delivery.v1.DeliveryAddresses/Get accepts a write and is
neither keyed nor declared retry-safe` and `CancelOrder accepts a write and
is neither keyed nor declared retry-safe`. `PlaceOrder` is not named.

- [ ] **Step 3: Declare the two endpoints**

In `src/Services/Ordering/Ordering.Api/Program.cs`, replace

```csharp
app.MapGrpcService<DeliveryAddressService>();
```

with

```csharp
app.MapGrpcService<DeliveryAddressService>().RetrySafe(RetrySafety.ReadOnly);   // ADR-052 — Get reads one address
```

In `src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs`, replace

```csharp
            .RequireAuthorization(OrderingPermissions.Cancel)
            .WithName("CancelOrder");
```

with

```csharp
            .RequireAuthorization(OrderingPermissions.Cancel)
            // Order.Cancel returns on a cancelled order, and cancelled is terminal (§5.4).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("CancelOrder");
```

Both files already have `using Common.Web;`.

- [ ] **Step 4: Run to see it pass**

Run:

```bash
dotnet test tests/Ordering.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

- [ ] **Step 5: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add tests/Ordering.Api.Tests/WriteEndpointRuleTests.cs src/Services/Ordering/Ordering.Api/Program.cs src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs
git commit -m "feat(ordering): CancelOrder declares Convergent and DeliveryAddressService ReadOnly" -m "Ordering maps three writes. PlaceOrder binds its command. CancelOrder carries no CommandId and says why it needs none: Order.Cancel returns on a cancelled order and cancelled is terminal (§5.4), so a repeat leaves the state the first left and no later write can come between them. ADR-052's address read is a POST to routing and reads one address, so its service is declared ReadOnly.

WriteEndpointRuleTests holds the rule over this host and names the three, so a fourth write is a failed floor before it is anything else."
```

---

### Task 5: Inventory

**Files:**
- Create: `tests/Inventory.Api.Tests/WriteEndpointRuleTests.cs`
- Modify: `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs`
  — the tails of the `ReleaseReservation` and `ReinstateReservation` chains
  (lines 39–40 and 50–51 on `main` at `b07cd0b2`; PR-B moves the second)
- Modify: `src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs:25-26`
- Modify: `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs` — PR-B's
  test and the two `using` lines it brought

**Interfaces:**
- Consumes: `WriteEndpointRule`, `RetrySafe`, `Idempotent<TCommand>`,
  PR-B's `ReinstateReservationCommand`, and Inventory's
  `HostSmokeTests.UnreachableInfrastructureFactory`.
- Produces: Inventory's two facts, and the metadata link that replaces
  PR-B's link by name.

- [ ] **Step 1: Write the failing test**

`tests/Inventory.Api.Tests/WriteEndpointRuleTests.cs`:

```csharp
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        WriteEndpointRule
            .Offenders(Endpoints, typeof(Inventory.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(
            ["ReinstateReservation", "ReleaseReservation", "SetOnHand"]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
```

Then `unix2dos tests/Inventory.Api.Tests/WriteEndpointRuleTests.cs`.

- [ ] **Step 2: Run to see it fail**

Run:

```bash
dotnet test tests/Inventory.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: `Failed:     1, Passed:     1`, the rule failing with four
offenders: `SetOnHand`, `ReleaseReservation` and `ReinstateReservation`
each `accepts a write and is neither keyed nor declared retry-safe`, and
`ReinstateReservationCommand is an idempotent command no endpoint reaches`.
The last two are one fact seen from both ends: the endpoint builds the
command, so no parameter names it.

- [ ] **Step 3: Declare the three endpoints**

In `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs`,
replace

```csharp
            .WithName("ReleaseReservation");
```

with

```csharp
            // A released reservation gives nothing back, so a repeat moves no stock (ADR-024).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("ReleaseReservation");
```

and replace

```csharp
            .WithName("ReinstateReservation");
```

with

```csharp
            // Built from the route and the body, so no parameter names the command (§8.5).
            .Idempotent<ReinstateReservationCommand>()
            .WithName("ReinstateReservation");
```

In `src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs`,
replace

```csharp
            .WithName("SetOnHand");
```

with

```csharp
            // An absolute count, so a repeat sets what the first set (ADR-058).
            .RetrySafe(RetrySafety.Convergent)
            .WithName("SetOnHand");
```

Both files already have `using Common.Web;`, and `ReservationEndpoints.cs`
already has `using Inventory.Application.Reservations.Reinstate;`. Each
declaration goes on the chain that ends in the `WithName` it sits above,
after the handler's closing `})`. The group's
`RequireAuthorization(InventoryPermissions.Admin)` is what satisfies §8.5's
subject rule for the keyed endpoint.

- [ ] **Step 4: Run to see it pass**

Run:

```bash
dotnet test tests/Inventory.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

- [ ] **Step 5: Remove the link by name**

PR-B could not see the command from the endpoint, so it added
`The_idempotent_command_is_reached_through_an_authenticated_admin_endpoint`
to `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs`, which finds the
endpoint by its name. Each thing it asserts now has an owner that reads the
metadata or was already there: that the declared commands are reached, and
that the keyed endpoint is neither anonymous nor without authorisation, are
`WriteEndpointRuleTests`' first fact; that `ReinstateReservation` requires
`InventoryPermissions.Admin` is `No_inventory_endpoint_is_anonymous`, which
lists it.

In `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs`, delete this
test and the blank line after it:

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

and replace the first four `using` lines

```csharp
using Common.Application;
using Inventory.Api;
using Inventory.Application.Reservations.Reinstate;
using Microsoft.AspNetCore.Authorization;
```

with

```csharp
using Inventory.Api;
using Microsoft.AspNetCore.Authorization;
```

That text is PR-B's plan's. If it merged worded differently, delete the test
of that name whole, and drop a `using` only when nothing left in the file
needs it.

- [ ] **Step 6: Run the two suites that moved**

Run:

```bash
dotnet test tests/Inventory.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests|FullyQualifiedName~AuthorizationPolicyTests"
```

Expected, `AuthorizationPolicyTests`' three and this suite's two:

```
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5
```

- [ ] **Step 7: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add tests/Inventory.Api.Tests/WriteEndpointRuleTests.cs tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs
git commit -m "feat(inventory): ReinstateReservation declares its command, ReleaseReservation and SetOnHand declare Convergent" -m "Inventory maps three writes and none binds a command. ReinstateReservation builds one from the route and a request record, so it says Idempotent<ReinstateReservationCommand>() and the gate reads the link off the endpoint. ReleaseReservation is Convergent on ADR-024's terms: a released reservation gives nothing back. SetOnHand is Convergent because it sets an absolute count.

ADR-058 carries the limit of that word for the last two: a stale repeat after a different write re-applies an old count or undoes a reinstatement, and both are operator acts behind InventoryPermissions.Admin, for which the residual is accepted.

AuthorizationPolicyTests loses the test that found the keyed endpoint by name. What it asserted is held by WriteEndpointRuleTests through the metadata, and the Admin policy by the test beside it that already lists the endpoint."
```

---

### Task 6: Payments

**Files:**
- Create: `tests/Payments.Api.Tests/WriteEndpointRuleTests.cs`

**Interfaces:**
- Consumes: `WriteEndpointRule`, and Payments'
  `HostSmokeTests.UnreachableInfrastructureFactory`.
- Produces: Payments' two facts. No source file changes: the host maps one
  GET and the OpenAPI document.

There is nothing to turn green here, so the failing run is made by
mutation: the suite is written, seen to pass, and then seen to fail when
the host is given a write.

- [ ] **Step 1: Write the test**

`tests/Payments.Api.Tests/WriteEndpointRuleTests.cs`:

```csharp
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(HostSmokeTests.UnreachableInfrastructureFactory factory)
    : IClassFixture<HostSmokeTests.UnreachableInfrastructureFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        WriteEndpointRule
            .Offenders(Endpoints, typeof(Payments.Application.DependencyInjection).Assembly)
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBeEmpty(
            "Payments maps reads alone; a write it gains is named here, keyed or declared (§8.5)");

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
```

Then `unix2dos tests/Payments.Api.Tests/WriteEndpointRuleTests.cs`.

- [ ] **Step 2: Run to see it pass**

Run:

```bash
dotnet test tests/Payments.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

- [ ] **Step 3: See both facts fail when the host gains a write**

In `src/Services/Payments/Payments.Api/Endpoints/PaymentEndpoints.cs`,
change `.MapGet(` to `.MapPost(` with the Edit tool, and run the same
command.

Expected: `Failed:     2, Passed:     0`. The rule names
`GetPayment accepts a write and is neither keyed nor declared retry-safe`,
and the floor fails with `should be empty but had 1 item and was
["GetPayment"]` under its own message, `Payments maps reads alone; a write
it gains is named here, keyed or declared (§8.5)`.

- [ ] **Step 4: Undo the mutation**

Change `.MapPost(` back to `.MapGet(` with the Edit tool, then:

```bash
git status --short src/Services/Payments
dotnet test tests/Payments.Api.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: no output from the first; `Passed:     2` from the second.

- [ ] **Step 5: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add tests/Payments.Api.Tests/WriteEndpointRuleTests.cs
git commit -m "test(payments): WriteEndpointRuleTests pins that the host maps no write" -m "Payments maps one GET and the OpenAPI document, so the rule has nothing to judge here today and its floor says so: the write selection is empty, and the endpoints left out by shape are the three probes, which is also what says the table was read. The first write Payments maps fails both facts until it is keyed or declared and named."
```

---

### Task 7: The BFF

**Files:**
- Modify: `tests/Web.Bff.Tests/Web.Bff.Tests.csproj:30-35`
- Create: `tests/Web.Bff.Tests/WriteEndpointRuleTests.cs`
- Modify: `src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs:2` and `:112-113`

**Interfaces:**
- Consumes: `WriteEndpointRule` in its one-argument form, `RetrySafe`, and
  `BffFactory`, whose `Services` builds the host with the identity provider
  and Catalog stood in for and no container.
- Produces: the BFF's two facts.

The BFF has no service fixture, so nothing reaches `Common.TestSupport` for
it and the reference is drawn here. It has no Application layer either, so
it calls the form that scans no assembly.

- [ ] **Step 1: Draw the reference**

In `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, replace

```xml
    <ProjectReference Include="..\Web.Bff.TestSupport\Web.Bff.TestSupport.csproj" />
```

with

```xml
    <ProjectReference Include="..\Web.Bff.TestSupport\Web.Bff.TestSupport.csproj" />
    <!-- WriteEndpointRule alone (ADR-058); this host has no service fixture to reach it through. -->
    <ProjectReference Include="..\Common.TestSupport\Common.TestSupport.csproj" />
```

- [ ] **Step 2: Write the failing test**

`tests/Web.Bff.Tests/WriteEndpointRuleTests.cs`:

```csharp
using Common.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>§8.5's rule over this host's endpoint table: a write is keyed or declared retry-safe (ADR-058).</summary>
public class WriteEndpointRuleTests(BffFactory factory) : IClassFixture<BffFactory>
{
    private IEnumerable<Endpoint> Endpoints =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

    [Fact]
    public void Every_write_endpoint_is_keyed_or_declares_why_a_repeat_is_harmless()
    {
        // No assembly to scan: this host has no Application layer, so it declares no command (§4.1).
        WriteEndpointRule.Offenders(Endpoints).ShouldBeEmpty();
    }

    [Fact]
    public void The_rule_above_is_looking_at_the_writes_this_host_maps()
    {
        // The floor: an offender list is as green over an empty selection.
        Names(WriteEndpointRule.Writes(Endpoints)).ShouldBe(["Quote"]);

        // What the selection leaves out by shape, named, so a route this host maps that way is not left out too.
        Names(WriteEndpointRule.Unrestricted(Endpoints)).ShouldBe(["Health checks", "Health checks", "Health checks"]);
    }

    private static string[] Names(IEnumerable<Endpoint> endpoints) =>
        [.. endpoints.Select(Name).Order(StringComparer.Ordinal)];

    private static string Name(Endpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? endpoint.DisplayName ?? "unnamed";
}
```

Then `unix2dos tests/Web.Bff.Tests/WriteEndpointRuleTests.cs`.

- [ ] **Step 3: Run to see it fail**

Run:

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected: `Failed:     1, Passed:     1`, the rule failing with
`Quote accepts a write and is neither keyed nor declared retry-safe`.

- [ ] **Step 4: Declare the endpoint**

In `src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs`, replace

```csharp
using Catalog.Pricing.V1;
using FluentValidation;
```

with

```csharp
using Catalog.Pricing.V1;
using Common.Web;
using FluentValidation;
```

and replace

```csharp
            .WithName("Quote");
```

with

```csharp
            // Prices a basket and writes nothing (§9.7).
            .RetrySafe(RetrySafety.ReadOnly)
            .WithName("Quote");
```

- [ ] **Step 5: Run to see it pass**

Run:

```bash
dotnet test tests/Web.Bff.Tests --filter "FullyQualifiedName~WriteEndpointRuleTests"
```

Expected:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2
```

- [ ] **Step 6: Commit**

```bash
git ls-files --eol -m -o --exclude-standard
git add tests/Web.Bff.Tests/WriteEndpointRuleTests.cs tests/Web.Bff.Tests/Web.Bff.Tests.csproj src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs
git commit -m "feat(bff): Quote declares ReadOnly" -m "The quote is a POST because it carries a basket (ADR-045), and it writes nothing: it prices the basket over §9.7's hop and answers. It says so with RetrySafe(ReadOnly), and WriteEndpointRuleTests holds the rule over this host and names its one write.

The suite reads the table off BffFactory with no container. Web.Bff.Tests gains a reference to Common.TestSupport for the gate alone, because this host has no service fixture to reach it through."
```

---

### Task 8: ADR-058, its row and §8.5's rule

**Files:**
- Create: `docs/backend-architecture/adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md`
- Modify: `docs/backend-architecture/appendix-a-adrs.md` — one row at the
  table's end
- Modify: `docs/backend-architecture/08-caching-redis.md:347-349`

**Interfaces:**
- Consumes: the rule as Tasks 1–7 built it; ADR-057 on `main`.
- Produces: the record every `ADR-058` in a comment points at.

- [ ] **Step 1: See the record missing, and its neighbour present**

```bash
ls docs/backend-architecture/adr | tail -1
tail -5 docs/backend-architecture/appendix-a-adrs.md
git grep --untracked -c "ADR-058" -- docs/backend-architecture
git grep -c "ADR-058" -- src tests tools
```

Expected: `ls` prints PR-A's `ADR-057-…` file, and the table's last row is
ADR-057's; the first `git grep` prints nothing and exits 1; and the second
lists the files whose comments already cite a record that does not exist.
If ADR-057 is not there, PR-A has not reached this branch: rebase as
*Global Constraints* says, or take the next free number.

- [ ] **Step 2: Write the ADR**

`docs/backend-architecture/adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md`,
in `/new-adr`'s form — no blank line between the three bold-led paragraphs,
and that footer and nothing else:

```markdown
# ADR-058 — A write endpoint is keyed or declares why a repeat is harmless

**Decision.** An endpoint a POST, PUT, PATCH or DELETE reaches is exactly one
of two things. It is **keyed**: it dispatches an `IIdempotentCommand`
([§8.5](../08-caching-redis.md)), which is read off the handler's parameters
or, where the endpoint builds the command from a route value and a request
record, off `Idempotent<TCommand>()`. Or it is **declared retry-safe** with
`RetrySafe(kind)`: `Convergent`, when a repeat of the same request leaves the
state the first one left and is answered as the first one was, or `ReadOnly`,
when the endpoint writes nothing. An endpoint that is neither, or both, or
that declares both kinds, fails the build. So does a keyed endpoint that
allows anonymous callers or names no authorisation, which is §8.5's subject
rule reaching the declared form, and so does an idempotent command no
endpoint reaches. The declarations are `Common.Web`'s. The gate is
`WriteEndpointRule` in `tests/Common.TestSupport`, and each host that maps
handlers — Catalog, Ordering, Inventory, Payments and the BFF — holds it in a
`WriteEndpointRuleTests` beside a floor naming every write the host maps and
every endpoint the selection leaves out. A gRPC method is a POST
([§9.7](../09-messaging.md)) and is inside the rule, declared on the builder
`MapGrpcService` returns.
**Why.** A client whose answer is lost sends the request again, and what the
repeat does is a property of the endpoint that somebody has to decide. §8.5
opened by saying every non-idempotent write command carries a `CommandId`,
and nothing held an endpoint to it: the only gate read commands that already
carried the field, so a write that carried none was unprotected and no test
noticed. A declaration is metadata on the route, where the gate reads it and
a reviewer reads the reason beside it; a keyed endpoint needs none, because
its handler's signature already says so.
**Consequences.** Every new write endpoint is two edits its author cannot
skip: the binding or the declaration, and its name in the host's floor.
`Convergent` has a limit. A repeat that arrives after a different write is
not a repeat of the present state: a stale `SetOnHand` re-applies an old
count, and a stale release undoes a reinstatement. `Convergent` is the right
declaration where that interleaving cannot occur or is an operator's
deliberate act; where a stale repeat would undo a later write that matters,
the endpoint is keyed instead. `CancelOrder` has no such interleaving,
because cancelled is terminal ([§5.4](../05-tactical-ddd.md)).
`ReleaseReservation` and `SetOnHand` are operator acts behind
`InventoryPermissions.Admin`, and the residual is accepted for them. A
declaration is a claim and nothing checks it: the gate reads that an endpoint
said `Convergent`, not that it converges. A declaration on a route group or
on a gRPC service reaches every endpoint under it, the one added later
included, and the host's floor is what turns that addition into a failed
test. Two hosts are outside the rule and hold no suite for it: the gateway
maps a reverse proxy and no handler of its own
([§10.1](../10-api-gateway.md)), and Shipping's worker exposes no API
([§3.2](../03-bounded-contexts.md)) and serves
[§13.5](../13-observability.md)'s probes alone. A handler mapped in either
brings the suite with it, and a worker
[§4.5](../04-solution-structure.md)'s scaffold renders is born with it. A
command reached only through a consumer is outside the rule as well:
[§9.5](../09-messaging.md)'s inbox deduplicates it, so it declares no
`IIdempotentCommand`, and one that does fails the gate above. An endpoint
that names no method accepts every verb, and the selection reads it by how it
was mapped. With a handler it is a write. As a bare `RequestDelegate` it is
not, because that is how the framework maps §13.5's probes and gRPC's
unimplemented-method fallbacks and nothing else marks those; the host's floor
names each endpoint left out this way, so a route a host maps in that shape
fails the floor rather than passing unread. A command bound inside a
parameter object is not seen as keyed until its endpoint says
`Idempotent<TCommand>()`, which fails closed. `Common.TestSupport` gains a
reference to `Common.Web` for the two metadata types, and two suites gain one
to `Common.TestSupport` for the gate: the BFF's, and `Common.Web.Tests`,
which holds the gate's own.

---

[Appendix A](../appendix-a-adrs.md) · [Index](../README.md)
```

- [ ] **Step 3: Add the index row**

In `docs/backend-architecture/appendix-a-adrs.md`, add this line directly
under the table's last row, which is ADR-057's. Nothing else in the file
changes.

```markdown
| **ADR-058** | [A write endpoint is keyed or declares why a repeat is harmless](adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md) |
```

- [ ] **Step 4: Amend §8.5's opening rule**

In `docs/backend-architecture/08-caching-redis.md`, under `## 8.5
Idempotency keys`, replace the section's first paragraph

```markdown
Every non-idempotent write command carries a client-generated `CommandId`, and
the key is claimed atomically before any work happens. What that buys is **at
most one commit per key while the marker survives**.
```

with

```markdown
Every endpoint a write verb reaches is one of two things, and the build fails
on one that is neither or both
([ADR-058](adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md)).
It is **keyed** — it dispatches a command carrying a client-generated
`CommandId`, and the key is claimed atomically before any work happens — or it
**declares why a repeat is harmless**: `RetrySafety.Convergent`, where the same
request repeated leaves the state the first one left, or
`RetrySafety.ReadOnly`, where the endpoint writes nothing. What a key buys is
**at most one commit per key while the marker survives**.
```

The paragraph after it opens "**That sentence had an exception in it**" and
still points at the last sentence, which is kept. No other paragraph of the
chapter is edited: the ADR is the correction, and the chapter points at it.

- [ ] **Step 5: See the record in place**

```bash
git grep --untracked -c "ADR-058" -- docs/backend-architecture
(cd .github/licence-gate && py -3.12 licence_gate.py)
```

Expected: three files, the ADR, Appendix A and `08-caching-redis.md`; and
the licence gate's one-line pass, since no version is printed. `--untracked`
because the ADR is not yet added, and `git grep` reads tracked files alone
without it.

- [ ] **Step 6: Commit**

```bash
git add docs/backend-architecture/adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md docs/backend-architecture/appendix-a-adrs.md docs/backend-architecture/08-caching-redis.md
git commit -m "docs: ADR-058 — a write endpoint is keyed or declares why a repeat is harmless (§8.5)" -m "§8.5 opened by saying every non-idempotent write command carries a CommandId, and nothing held an endpoint to it. The rule it states now is the one the build enforces: an endpoint a write verb reaches dispatches an IIdempotentCommand, or declares that a repeat is Convergent or that the endpoint is ReadOnly.

The ADR carries what the declaration does not buy. Convergent is a claim nothing checks, and it does not survive a different write arriving between the request and its repeat, which is accepted for the two operator acts that take it. The gateway and Shipping's worker are outside the rule and said to be. A command reached only through a consumer is the inbox's. And an endpoint naming no method is read by how it was mapped, with the host's floor naming each one left out."
```

---

### Task 9: Verify, and the PR body

**Files:** none.

**Interfaces:**
- Consumes: every task above.
- Produces: a branch ready for `/pr`.

- [ ] **Step 1: One number in every citation**

```bash
git grep -n "ADR-058" -- src tests tools docs/backend-architecture
```

Expected: every line names the same number, the one Task 0 fixed. If it
moved, each of these is a line to move.

- [ ] **Step 2: Build, and the half of the suite that needs no daemon**

```bash
dotnet build Platform.slnx
dotnet test Platform.slnx --filter "Category!=Integration"
```

Expected: `0 Warning(s)`, `0 Error(s)`; then a `Passed!` line for every test
project and no `Failed!` line.

- [ ] **Step 3: The container half**

```bash
docker info --format '{{.ServerVersion}}'
dotnet test Platform.slnx --filter "Category=Integration"
```

Expected: a version number; then a `Passed!` line for every project that
has container tests and no `Failed!` line. The declarations add metadata
and change no request's path, so a failure here is not expected and is not
to be waved through.

- [ ] **Step 4: The gates outside the solution**

```bash
(cd tools/new-service && py -3.12 -m unittest)
py -3.12 .github/comment-gate/comment_gate.py --base origin/main
git ls-files --eol -m -o --exclude-standard
```

Expected: `OK`; `0 finding(s)`; and no output from the last, the tree being
committed. If anything under `tests/Catalog.*` or `tools/new-service`
changed after Task 3, run Task 3's Step 9 again.

- [ ] **Step 5: `/check-links`, then `/validate-blueprint`**

Run `/check-links`. Expected: no finding against the ADR, its row or
§8.5's link.

Then, from a subagent and last, because it narrows `Edit` to `docs/` for
the session that runs it:
`/validate-blueprint docs/backend-architecture/08-caching-redis.md`.

Expected: no finding in §8.5's opening paragraph or ADR-058. Two findings
it may raise are outside this PR's touch set and are answered by citing
`docs/change-locality.md` §2, not by editing: §11.4's `CancelOrder` sample
without the declaration, and any restatement of §8.5's old opening
sentence elsewhere. Record each in the PR body. Anything it found in the
corpus that this branch did not cause is parked on its own branch.

- [ ] **Step 6: Open the pull request**

Run `/pr`. The body's two rows are exactly:

```markdown
| Class | C |
| Touch set | `src/BuildingBlocks/Common.Web/RetrySafetyExtensions.cs`, `tests/Common.Web.Tests/RetrySafetyExtensionsTests.cs`, `tests/Common.Web.Tests/Common.Web.Tests.csproj`, `tests/Common.TestSupport/WriteEndpointRule.cs`, `tests/Common.TestSupport/Common.TestSupport.csproj`, `tests/*/WriteEndpointRuleTests.cs`, `tests/Web.Bff.Tests/Web.Bff.Tests.csproj`, `tests/Inventory.Api.Tests/AuthorizationPolicyTests.cs`, `src/Services/Catalog/Catalog.Api/Program.cs`, `src/Services/Ordering/Ordering.Api/Program.cs`, `src/Services/Ordering/Ordering.Api/Endpoints/OrderEndpoints.cs`, `src/Services/Inventory/Inventory.Api/Endpoints/ReservationEndpoints.cs`, `src/Services/Inventory/Inventory.Api/Endpoints/StockEndpoints.cs`, `src/BFF/Web.Bff/Endpoints/CheckoutEndpoints.cs`, `tools/new-service/scaffold/render.py`, `tools/new-service/scaffold/patch.py`, `tools/new-service/test_new_service.py`, `docs/backend-architecture/adr/ADR-058-a-write-endpoint-is-keyed-or-declares-why-a-repeat-is-harmless.md`, `docs/backend-architecture/appendix-a-adrs.md`, `docs/backend-architecture/08-caching-redis.md` |
```

Then `.claude/scripts/pr-locality.sh <number>`. Expected: `class C`, and
`inside` for every changed path. Update `TODO.md` in the main checkout's
root once the session is back there, as `CLAUDE.md` asks.

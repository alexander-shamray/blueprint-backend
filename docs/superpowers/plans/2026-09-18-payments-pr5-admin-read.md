# Payments PR-5 — operators read a payment — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `GET /v1/payments/{orderId}` answers what Payments holds for an
order — the record, the intent, the refund — behind `payments:admin` in the
service and at the gateway, so `order-review.md`'s first step has a
first-party answer.

**Architecture:** One Dapper query over the three tables (§6.5's read side),
one endpoint under a `RequirePermission` policy (§11.3's re-validation), and
the gateway, realm and runbook edits that make the route reachable by a
person: a `payments-admin` route and `payments` cluster in the gateway's route
file, `GatewayPermissions.PaymentsAdmin` and its policy, the realm role, and
`demo`'s grant.

**Tech Stack:** Dapper, ASP.NET Core minimal APIs, YARP configuration,
Keycloak realm export, xUnit with Shouldly and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-18-payments-service-design.md`,
section 10.

## Global Constraints

- The blueprint wins over the spec; the spec wins over this plan.
- **Class A+D+E.** Touch set: `src/Services/Payments/**`, `tests/Payments.*`,
  `Payments.Application.csproj` (the `Dapper` reference the first query
  brings back, no `Version=`), `src/Gateway/Gateway.Api/**`,
  `tests/Gateway.Api.Tests/**`, `deploy/compose/services/gateway.yml`,
  `deploy/compose/keycloak/realm-export.json`,
  `tests/Common.Web.Tests/RealmImportTests.cs`,
  `docs/backend-architecture/10-api-gateway.md` (§10.2's printed route file
  and its policy sentence), and `docs/runbooks/order-review.md` (step 1).
- **Three classes, which the locality gate does not yet admit.** A service's
  arrival spans its code (A), its projects (E) and its deployment or harness
  tree (D); `docs/change-locality.md` names at most two and
  `.github/locality-gate` refuses a third letter. This PR cannot merge until
  the contract and the gate admit that case — a Class D change of its own,
  owed before Payments' PR-1, and met first by Inventory's plans, which
  declare the same shape.
- Depends on PR-4 having merged, so the read has a refund to report, and on
  Inventory's PR-1, whose `inventory:admin` grant to `demo` this sits beside.
- The response carries no `CustomerId`: an operator needs the money's state,
  not its subject (spec, section 10).
- `orders:admin` stays ungranted; nothing here touches it.
- Every step that adds behaviour writes its test first.

---

### Task 1: `GetPaymentQuery`

**Files:**
- Modify: `Payments.Application.csproj` — `<PackageReference Include="Dapper" />`
  beside the others, the scaffold's comment in its place cut (its "What you
  do next", step 3: the first query brings Dapper)
- Create: `Payments.Application/Admin/GetPayment/GetPaymentQuery.cs`
- Create: `.../GetPayment/PaymentView.cs`
- Create: `.../GetPayment/GetPaymentHandler.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record GetPaymentQuery(Guid OrderId) : IQuery<PaymentView?>;

public sealed record PaymentView(Guid OrderId, OrderView Order, IntentView? Intent, RefundView? Refund);
public sealed record OrderView(DateTimeOffset? PlacedAt, DateTimeOffset? CancelledAt);
public sealed record IntentView(string Status, string? Reference, decimal Amount, string Currency, string? DeclineReason, DateTimeOffset CreatedAt);
public sealed record RefundView(string Reference, DateTimeOffset VoidedAt);
```

- [ ] **Step 1: Write the handler**

The query is tested over the endpoint in Task 2, which is where its SQL
meets the migrated schema; there is no in-memory stand-in for Dapper worth
writing.

```csharp
using System.Data;
using Common.Application;
using Dapper;

namespace Payments.Application.Admin.GetPayment;

/// <summary>
/// §6.5's read side over the three write tables: what Payments holds for one
/// order, for the runbook's first question (spec, section 10). No payer: the
/// subject is not what an operator needs to see.
/// </summary>
public sealed class GetPaymentHandler(IDbConnectionFactory connections) : IQueryHandler<GetPaymentQuery, PaymentView?>
{
    private const string Sql =
        """
        SELECT o.OrderId, o.PlacedAt, o.CancelledAt,
               i.Status, i.Reference, i.Amount, i.Currency, i.DeclineReason, i.CreatedAt,
               r.Reference AS RefundReference, r.VoidedAt
        FROM payments.PaymentOrders o
        LEFT JOIN payments.PaymentIntents i ON i.OrderId = o.OrderId
        LEFT JOIN payments.Refunds r ON r.OrderId = o.OrderId
        WHERE o.OrderId = @OrderId;
        """;

    private sealed record Row(
        Guid OrderId,
        DateTimeOffset? PlacedAt,
        DateTimeOffset? CancelledAt,
        string? Status,
        string? Reference,
        decimal? Amount,
        string? Currency,
        string? DeclineReason,
        DateTimeOffset? CreatedAt,
        string? RefundReference,
        DateTimeOffset? VoidedAt);

    public async Task<PaymentView?> HandleAsync(GetPaymentQuery query, CancellationToken ct)
    {
        using IDbConnection connection = connections.Create();

        Row? row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: ct));

        if (row is null)
            return null;

        IntentView? intent = row.Status is null
            ? null
            : new IntentView(row.Status, row.Reference, row.Amount!.Value, row.Currency!.Trim(), row.DeclineReason, row.CreatedAt!.Value);
        RefundView? refund = row.RefundReference is null ? null : new RefundView(row.RefundReference, row.VoidedAt!.Value);

        return new PaymentView(row.OrderId, new OrderView(row.PlacedAt, row.CancelledAt), intent, refund);
    }
}
```

An intent or refund row without its record cannot exist: every writer locks
or stamps the record first. So `PaymentOrders` leads the join and a missing
record is the query's only `null`.

- [ ] **Step 2: Build; commit with Task 2**

`dotnet build Platform.slnx`. Expected: 0 warnings. The scaffold's
registration test for the query scan now has a subject; restore it as the
scaffold's README says, asserting `GetPaymentHandler` resolves.

---

### Task 2: The endpoint and `payments:admin`

**Files:**
- Modify: `src/Services/Payments/Payments.Api/PaymentsPermissions.cs` —
  `public const string Admin = "payments:admin";`
- Create: `src/Services/Payments/Payments.Api/Endpoints/PaymentEndpoints.cs`
- Modify: `src/Services/Payments/Payments.Api/Program.cs` — the policy and
  `app.MapPaymentEndpoints();`
- Test: `tests/Payments.Api.Tests/PaymentEndpointsTests.cs`
- Test: `tests/Payments.Api.Tests/EndpointSecurityTests.cs`,
  `AuthorizationPolicyTests.cs` — the scaffold's, renamed to the constant and
  path

- [ ] **Step 1: Write the failing endpoint tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Payments.Application.Admin.GetPayment;
using Payments.TestSupport;
using Shouldly;
using Xunit;

namespace Payments.Api.Tests;

[Collection(nameof(IntegrationCollection))]
public sealed class PaymentEndpointsTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpClient Admin()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        client.DefaultRequestHeaders.Add(TestAuthHandler.PermissionsHeader, PaymentsPermissions.Admin);
        return client;
    }

    private Task SeedAsync(Guid order, bool authorised, bool refunded) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO payments.PaymentOrders (OrderId, CustomerId, TotalAmount, Currency, PlacedAt, CancelledAt)
            VALUES ({0}, NEWID(), 42.10, 'EUR', SYSDATETIMEOFFSET(), CASE WHEN {2} = 1 THEN SYSDATETIMEOFFSET() END);
            IF {1} = 1
                INSERT INTO payments.PaymentIntents (OrderId, Status, Amount, Currency, Reference, CreatedAt)
                VALUES ({0}, 'Authorised', 42.10, 'EUR', 'psp_x', SYSDATETIMEOFFSET());
            IF {2} = 1
                INSERT INTO payments.Refunds (OrderId, Reference, Amount, Currency, VoidedAt)
                VALUES ({0}, 'psp_x', 42.10, 'EUR', SYSDATETIMEOFFSET());
            """,
            order, authorised ? 1 : 0, refunded ? 1 : 0);

    [Fact]
    public async Task A_refunded_payment_reads_its_record_intent_and_refund()
    {
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: true, refunded: true);

        PaymentView? view = await Admin().GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}", TestContext.Current.CancellationToken);

        view.ShouldNotBeNull();
        view.Order.CancelledAt.ShouldNotBeNull();
        view.Intent!.Status.ShouldBe("Authorised");
        view.Intent.Reference.ShouldBe("psp_x");
        view.Refund!.Reference.ShouldBe("psp_x");
    }

    [Fact]
    public async Task A_placed_order_with_no_command_yet_reads_no_intent()
    {
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: false, refunded: false);

        PaymentView? view = await Admin().GetFromJsonAsync<PaymentView>(
            $"/v1/payments/{order}", TestContext.Current.CancellationToken);

        view!.Intent.ShouldBeNull();
        view.Refund.ShouldBeNull();
    }

    [Fact]
    public async Task The_answer_names_no_payer()
    {
        Guid order = Guid.CreateVersion7();
        await SeedAsync(order, authorised: true, refunded: false);

        string body = await Admin().GetStringAsync($"/v1/payments/{order}", TestContext.Current.CancellationToken);

        body.ShouldNotContain("customer", Case.Insensitive);
        body.ShouldNotContain("payer", Case.Insensitive);
    }

    [Fact]
    public async Task An_order_Payments_never_heard_of_reads_404()
    {
        HttpResponseMessage response = await Admin().GetAsync(
            $"/v1/payments/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_permission_it_answers_403()
    {
        HttpClient client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());

        (await client.GetAsync($"/v1/payments/{Guid.CreateVersion7()}", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Step 2: Run to see them fail**

Expected: compile failure on `PaymentsPermissions.Admin`, then 404s.

- [ ] **Step 3: Write the permission, endpoint and policy**

```csharp
namespace Payments.Api;

/// <summary>
/// Payments' permission vocabulary (§11.4): one name, the gateway's own
/// <c>payments:admin</c> (§10.2), re-validated here because §11.3 makes every
/// service check its own token.
/// </summary>
public static class PaymentsPermissions
{
    public const string Admin = "payments:admin";
}
```

```csharp
using Common.Application;
using Payments.Application.Admin.GetPayment;

namespace Payments.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app
            .MapGroup("/v1/payments")
            .WithTags("Payments")
            .RequireAuthorization(PaymentsPermissions.Admin)
            .MapGet(
                "/{orderId:guid}",
                async (Guid orderId, IDispatcher dispatcher, CancellationToken ct) =>
                {
                    PaymentView? payment = await dispatcher.QueryAsync(new GetPaymentQuery(orderId), ct);

                    return payment is null ? Results.NotFound() : Results.Ok(payment);
                })
            .WithName("GetPayment");
    }
}
```

In `Program.cs`, replace the scaffold's commented policy line with
`.AddPolicy(PaymentsPermissions.Admin, p => p.RequirePermission(PaymentsPermissions.Admin))`
and map the endpoints where the scaffold maps its own.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Payments.Application.Tests tests/Payments.Api.Tests
git add src/Services/Payments tests/Payments.*
git commit -m "feat(payments): GET /v1/payments/{orderId} under payments:admin"
```

---

### Task 3: The realm role, `demo`'s grant, and the gateway

**Files:**
- Modify: `deploy/compose/keycloak/realm-export.json` — a `payments:admin`
  client role on `commerce-api`, and `demo`'s `commerce-api` roles gain it
- Modify: `tests/Common.Web.Tests/RealmImportTests.cs` — the realm's role
  list and `demo`'s permissions both gain `"payments:admin"`, and the comment
  above `demo`'s assertion gains that it is held for the same reason as
  `inventory:admin`
- Modify: `src/Gateway/Gateway.Api/GatewayPermissions.cs` —
  `public const string PaymentsAdmin = "payments:admin";`
- Modify: `src/Gateway/Gateway.Api/Program.cs` — the policy beside
  `InventoryAdmin`'s
- Modify: `src/Gateway/Gateway.Api/appsettings.json` — the `payments-admin`
  route and `payments` cluster
- Modify: `tests/Gateway.Api.Tests/GatewayPipelineTests.cs` — the 403 test's
  sibling for `/api/v1/payments/…`
- Modify: `deploy/compose/services/gateway.yml` — `depends_on` gains
  `payments-api: { condition: service_started }`
- Test: `tests/Payments.Api.Tests/GrantablePermissionTests.cs` — Ordering's
  file with `OrderingPermissions` replaced by `PaymentsPermissions` and the
  admin-claim test removed, since no Payments handler reads a claim. Here
  rather than in Task 2 because it asserts the realm can grant what the
  endpoint requires, which is this task's edit

- [ ] **Step 1: Write the failing tests**

`RealmImportTests`: add `"payments:admin"` to both asserted lists. Gateway:

```csharp
[Fact]
public async Task The_payments_admin_route_refuses_an_authenticated_caller_without_the_permission()
{
    using HttpClient client = factory.CreateClient();

    using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/payments/{Guid.CreateVersion7()}");
    request.Headers.Add(TestAuthHandler.UserHeader, "018f4c2e");

    HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    await ShouldBeProblemJson(response);
}
```

Run `dotnet test tests/Common.Web.Tests tests/Gateway.Api.Tests` and see
both fail — the second on a 404, since no route matches yet.

- [ ] **Step 2: The realm**

A new object in `roles.client.commerce-api`, in the existing shape, with a
fresh `id` GUID, `containerId` copied from `inventory:admin`'s, and one
single-line `description`: "Read what Payments holds for an order through the
gateway's payments-admin route (§10.2) — the order-review runbook's first
step. Granted to demo locally, beside inventory:admin, because it guards a
route with no ownership check to override (§14.1)." `demo`'s
`clientRoles.commerce-api` gains `"payments:admin"`.

```bash
py -3.12 -m unittest discover -s deploy/keycloak
py -3.12 deploy/keycloak/realm_check.py --kind local deploy/compose/keycloak/realm-export.json
```

Suite first, then the gate. Expected: both exit 0.

- [ ] **Step 3: The gateway**

`GatewayPermissions.PaymentsAdmin`, and in `Program.cs` beside the existing
line:

```csharp
    .AddPolicy(GatewayPermissions.PaymentsAdmin, p => p.RequirePermission(GatewayPermissions.PaymentsAdmin))
```

In `appsettings.json`, after `inventory-admin`:

```jsonc
      // payments:admin, for the reason inventory:admin's comment gives: a
      // permission, and a rate limiter because an authorised loop still
      // reaches a database. Read-only; a refund stays at the provider.
      "payments-admin": {
        "ClusterId": "payments",
        "Match": { "Path": "/api/v1/payments/{**catch-all}" },
        "AuthorizationPolicy": "payments:admin",
        "RateLimiterPolicy": "authenticated",
        "Transforms": [ { "PathRemovePrefix": "/api" } ]
      },
```

and after the `inventory` cluster:

```jsonc
      "payments": {
        "Destinations": { "d1": { "Address": "http://payments-api:8080/" } }
      },
```

The gateway's `GrantablePermissionTests` reflects over `GatewayPermissions`,
so the new constant is checked against the realm with no edit to it.
`deploy/compose/services/gateway.yml`'s `depends_on` gains `payments-api`
with the condition its siblings use.

- [ ] **Step 4: Run; commit**

```bash
dotnet test tests/Common.Web.Tests tests/Gateway.Api.Tests tests/Payments.Api.Tests
git add deploy/compose src/Gateway tests/Gateway.Api.Tests tests/Common.Web.Tests tests/Payments.Api.Tests
git commit -m "feat(gateway): the payments-admin route, and demo may read a payment"
```

---

### Task 4: §10.2 and the runbook

**Files:**
- Modify: `docs/backend-architecture/10-api-gateway.md` — the printed route
  file gains the `payments-admin` route and `payments` cluster exactly as
  `appsettings.json` now has them; the sentence "`authenticated` comes from
  `AddCommonWebDefaults` (§13.2) and `inventory:admin` from the gateway's own
  `Program.cs` (§4.2)" becomes "…and the two permission policies,
  `inventory:admin` and `payments:admin`, from the gateway's own
  `Program.cs` (§4.2)"
- Modify: `docs/runbooks/order-review.md` — step 1 of the money procedure

- [ ] **Step 1: §10.2**

Copy the two blocks into the chapter's fence at the same positions they
hold in `appsettings.json`, comments included, since the chapter prints the
file. Any `GatewayConfigurationTests` that compares the fence to the file
will say whether they match; run `dotnet test tests/Gateway.Api.Tests`.

- [ ] **Step 2: The runbook**

In step 1, "Look for a `PaymentRefunded` for this order, or read the
provider's own console." becomes:

"Read `GET /api/v1/payments/{orderId}` through the gateway with a
`payments:admin` token: a `refund` in the answer means Payments voided it,
and `intent.status` says whether there was anything to void. The provider's
own console is the second source, for when Payments is itself the question."

Nothing else in the runbook moves.

- [ ] **Step 3: Audit; commit**

Run `/check-links` and `/validate-blueprint`.

```bash
git add docs/backend-architecture/10-api-gateway.md docs/runbooks/order-review.md
git commit -m "docs: §10.2 prints the payments-admin route, and the runbook's first step reads it"
```

---

### Task 5: Verification and the PR

- [ ] `dotnet build Platform.slnx` — 0 warnings; `dotnet test Platform.slnx` — green.
- [ ] Under Compose, with a `demo` token per `deploy/compose/README.md`:
  place an order, cancel it, then
  `curl -H "Authorization: Bearer $TOKEN" http://localhost:5000/api/v1/payments/<orderId>`.
  Expected: 200 with a `refund`. Record the response in the PR body.
- [ ] PR body: `| Class | A+D+E |`, touch set from the Global Constraints.
  Then `/ship`.

## Self-review

- Spec coverage: section 10's endpoint, response, 404, missing payer,
  in-service permission, gateway route and cluster, realm grant, §10.2 and
  runbook edits → Tasks 1–4.
- `GrantablePermissionTests` lands with the realm role it checks, so every
  commit is green.
- Types: `GetPaymentQuery`, `PaymentView`, `PaymentsPermissions.Admin`,
  `GatewayPermissions.PaymentsAdmin` are named once each and used as named.

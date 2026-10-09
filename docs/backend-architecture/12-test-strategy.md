# 12. Test strategy and TDD

## 12.1 The pyramid

```
      ╱╲            Cross-service — a few
     ╱  ╲           A whole saga, all services in containers. Seconds.
    ╱────╲
   ╱      ╲         Integration — tens per service
  ╱        ╲        Real SQL Server, Redis, RabbitMQ via Testcontainers.
 ╱──────────╲
╱            ╲      Unit — hundreds per service
──────────────      Domain logic. No I/O. Milliseconds.
```

| Level | Scope | Dependencies | Target time | Count | Lives in |
|---|---|---|---|---|---|
| Domain unit | One aggregate or value object | None — no mocks either | < 1 ms | Hundreds | `*.Domain.Tests` |
| Application | One handler end to end | Real DB and Redis (containers), fakes for other services | < 500 ms | Tens | `*.Application.Tests` |
| API contract | HTTP in, HTTP out | `WebApplicationFactory` + containers | < 1 s | Tens | `*.Api.Tests` |
| Host building block | One middleware or host extension | `TestServer` — no containers, no entry point | < 50 ms | Tens | `Common.Web.Tests` |
| Infrastructure building block | One §8, §9 or §11.5 mechanism — a consumer wrapper, the outbox's table and type map, the idempotency store, the lock, the retention policy, the client-credentials token source | Recording fakes for most; a stub OpenID provider on loopback for the token source; a real Redis (container) for the classes in its `Integration` collection, where the mechanism under test is Redis's | < 10 ms, and < 500 ms against the stub provider or Redis | One suite | `Common.Infrastructure.Tests` |
| Edge configuration | The route file of §10.2 against the host that loaded it, and §10.1's edge behaviours — compression and the body ceiling | `WebApplicationFactory` + a stub destination on loopback — no containers, and `UseKestrel` where the property under test is the server's own | < 1 s | One suite | `Gateway.Api.Tests` |
| Outbound hop | One of §9.7's synchronous calls, from its caller's host: the timeout hierarchy, the credential handler's position inside the resilience pipeline, and §11.5's realm for a caller whose grant is proved there | `WebApplicationFactory` + the callee on loopback — a real gRPC server or a stub HTTP one; a class in a suite proving a grant also runs a real Keycloak | < 1 s, and seconds for a Keycloak class | One suite per caller | `Web.Bff.Tests`, `Shipping.Worker.Tests`, `Notifications.Worker.Tests` |
| Host projection | The BFF's own schema and the consumers that write it (ADR-051): the migrator's run, each handler against every row shape another leaves, the broker binding under its account | Real SQL Server and RabbitMQ (containers), `WebApplicationFactory` | < 1 s | One suite | `Web.Bff.Tests` |
| Pipeline behaviour | One §6.3 behaviour against recording fakes — the branches its handler-level tests cannot reach | None | < 10 ms | One suite per behaviour | `Common.Application.Tests` |
| Saga | One whole saga, coordination only | MassTransit in-memory harness — no infrastructure | < 100 ms per positive assertion (§12.5) | A few | `*.Application.Tests` |
| Contract shape | Every published contract against the rules it must obey and the shape it was recorded with | Both assemblies by reflection, and the recorded shape | < 1 s | One suite | `Platform.IntegrationTests` |

**Neither is there an "all services in containers" level, nor an E2E one.** Both
are rows that get written into a strategy and never built — the second needs a
client the backend does not own and data that survives between runs; the first
needs every service's image, database and broker started together, which is a
local Compose environment wearing a test-runner costume and fails in ways nobody
can attribute.

What they would actually catch splits cleanly in two, and both halves are
cheaper elsewhere. **Saga coordination** — did the right command go out, in the
right order, after the right event — is exercised by the in-memory harness in
§12.5, in milliseconds. The exception is an assertion that something did *not*
happen, which cannot resolve until the harness gives up waiting and so costs
the whole inactivity timeout — once per test, however many negatives it
asserts. §12.5's traps price that and name the correctness hazard that comes
with it, and between them they are the reason these tests are "a few" rather
than hundreds. **Contract compatibility** —
does the message one service publishes still have the shape its consumers were
built against — is a reflection test over the contract assembly and a record of
its shape, and it is why
`Platform.IntegrationTests` exists; what else that suite holds is §12.6's.
Contract compatibility is **not the only thing genuinely between services** —
§9.7's synchronous calls are another, and §12.6 tests the two in almost
opposite ways. The calls are deliberately not in this suite: a contract over
one needs the provider running, so it lives in the provider's own suite rather
than buying a sixth project a container set.

What no level above covers is whether the *deployed* system responds under load
and against real infrastructure. That is the **k6 run against staging**
([§13.7](13-observability.md)) — `deploy/observability/slo/slo.js` — asserting
the SLO rows it can evaluate; not a test suite, and
[§15.1](15-cicd-deployment.md) stages it as what it is. **One tool, named**,
because a stage naming two tools names none.

Naming it accurately is the point: a load run that is honestly a load run gets
maintained; an "E2E suite" that is actually three fragile scripts gets disabled
after the second flake and stays green forever.

Every row above names a project **and has an example in this section**. Both
halves are the rule: a level with no home is a level nobody writes, and a level
whose home is empty is one nobody notices is missing.

**How to run them is `docs/testing.md`**, deliberately not this chapter: the
commands, the `Category=Integration` filter of §12.4, how to find the projects
that need a Docker daemon and what the coverage figure of §12.9 is measured
over. This
chapter decides what to test and that file decides how to run it, and the
second goes stale on a different clock — a new runner flag changes nothing
about the pyramid. Where they disagree, this chapter wins.

> **A suite that never ran looks exactly like a suite that passed.** `dotnet
> test` discovers through a VSTest adapter, and `xunit.v3` does not carry one:
> `xunit.runner.visualstudio` is a separate package ([Appendix B](appendix-b-licences.md)).
> Leave it off a test project and the build succeeds, the run reports no tests,
> and the process exits **zero** — green CI over a suite nothing executed. Every
> test project under `tests/` references all three of `xunit.v3`, the adapter
> and `Microsoft.NET.Test.Sdk`, and the one that goes missing is the one
> nothing turns red about. The `*.TestSupport` libraries reference none of the
> three: they are not test projects (§4.1), hold no `[Fact]`, and take the
> fixture contract from `xunit.v3.extensibility.core` instead, which is what
> lets a library share a fixture without becoming a suite.

## 12.2 The TDD cycle applied

Red, green, refactor — with a worked example, because the discipline is easier
to describe than to follow. The example and §12.3's samples are illustrative:
`OrderBuilder` is not in the tree, and
`tests/Ordering.Domain.Tests/OrderTests.cs` builds its orders with its own
`AnOrder` helper under other test names.

**Requirement:** an order cannot be cancelled once it has shipped.

**Red** — write the test first. It must fail for the right reason.

```csharp
public class OrderCancellationTests
{
    [Fact]
    public void Cannot_cancel_an_order_that_has_shipped()
    {
        Order order = OrderBuilder.Shipped();

        Action act = () =>
            order.Cancel(CancellationReason.CustomerRequest, CancellationOrigin.User, DateTimeOffset.UtcNow);

        act
            .ShouldThrow<DomainException>()
            .Message.ShouldContain("cannot be cancelled");
    }
}
```

Run it. It fails because `Cancel` does not check status yet — not because
`OrderBuilder.Shipped()` does not compile. A test failing to compile is not a
red test; make it compile first, then watch it fail.

> **Test names are sentences, and that costs exactly one analyser rule.** CA1707
> forbids underscores in member names and [ADR-019](adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md)
> makes every warning an error, so the name above fails the build until the rule
> is turned off. `Directory.Build.props` turns it off for projects whose name
> ends `Tests` and nowhere else. The convention wins because a test name is read
> in a failure report by somebody who is not looking at the code, and
> `CannotCancelAnOrderThatHasShipped` is worse at that job than the underscores
> are at anything.

**Green** — the minimum change that passes:

```csharp
public void Cancel(CancellationReason reason, CancellationOrigin origin, DateTimeOffset now)
{
    if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
        throw new DomainException($"A {Status} order cannot be cancelled.");

    Status = OrderStatus.Cancelled;
    Raise(new OrderCancelledDomainEvent(Id, CustomerId, reason, origin, now));
}
```

**Refactor** — with the test green, improve. Add the guidance about returns, and
add the idempotency case as its own test first:

```csharp
[Fact]
public void Cancelling_twice_is_idempotent()
{
    Order order = OrderBuilder.AwaitingPayment();
    order.Cancel(CancellationReason.CustomerRequest, CancellationOrigin.User, Now);

    order.Cancel(CancellationReason.CustomerRequest, CancellationOrigin.User, Now);

    order.Status.ShouldBe(OrderStatus.Cancelled);
    order.DomainEvents.OfType<OrderCancelledDomainEvent>().Count().ShouldBe(1);
}
```

Why this order matters: writing the test first forces you to design the API from
the caller's perspective before the implementation biases you, and it proves the
test can fail. A test written after the code has never been observed failing,
and a test that cannot fail is not a test.

## 12.3 Domain tests — no mocks

The domain has no dependencies, so its tests need no test doubles. This is the
payoff for the dependency rule in section 4.2.

```csharp
public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Placing_an_order_totals_all_lines()
    {
        var order = Order.Place(
            new CustomerId(Guid.CreateVersion7()),
            AddressBuilder.Valid(),
            [
                (ProductId.New(), 2, Money.Of(10.00m, "EUR")),
                (ProductId.New(), 1, Money.Of(5.50m, "EUR"))
            ],
            "EUR",
            Now);

        order.Total.ShouldBe(Money.Of(25.50m, "EUR"));
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
    }

    [Fact]
    public void Placing_an_order_raises_OrderPlacedDomainEvent()
    {
        Order order = OrderBuilder.Placed();

        OrderPlacedDomainEvent placed = order.DomainEvents
            .OfType<OrderPlacedDomainEvent>()
            .ShouldHaveSingleItem();
        placed.OrderId.ShouldBe(order.Id);
        placed.Total.ShouldBe(order.Total);
    }

    [Fact]
    public void An_order_must_have_at_least_one_line()
    {
        Action act = () => Order.Place(new CustomerId(Guid.CreateVersion7()), AddressBuilder.Valid(), [], "EUR", Now);

        act.ShouldThrow<DomainException>();
    }

    [Fact]
    public void Adding_the_same_product_twice_merges_the_lines()
    {
        var product = ProductId.New();

        var order = Order.Place(
            new CustomerId(Guid.CreateVersion7()),
            AddressBuilder.Valid(),
            [
                (product, 2, Money.Of(10m, "EUR")),
                (product, 3, Money.Of(10m, "EUR"))
            ],
            "EUR",
            Now);

        OrderLine line = order.Lines.ShouldHaveSingleItem();
        line.Quantity.ShouldBe(5);
    }

    [Fact]
    public void All_lines_must_share_the_order_currency()
    {
        Action act = () => Order.Place(
            new CustomerId(Guid.CreateVersion7()),
            AddressBuilder.Valid(),
            [
                (ProductId.New(), 1, Money.Of(10m, "USD"))
            ],
            "EUR",
            Now);

        act
            .ShouldThrow<DomainException>()
            .Message.ShouldContain("currency");
    }
}
```

Test data uses builders with sensible defaults, so each test states only what it
cares about:

```csharp
internal static class OrderBuilder
{
    private static readonly DateTimeOffset DefaultNow =
        new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    // The customer is a parameter because ownership tests have to name it —
    // §12.4's 404-not-403 case turns entirely on who owns the order.
    public static Order Placed(int lines = 1, string currency = "EUR", CustomerId? customer = null) =>
        Order.Place(
            customer ?? new CustomerId(Guid.CreateVersion7()),
            AddressBuilder.Valid(),
            Enumerable
                .Range(0, lines)
                .Select(_ => (ProductId.New(), 1, Money.Of(10m, currency))),
            currency,
            DefaultNow);

    public static Order AwaitingPayment()
    {
        Order order = Placed();
        order.ConfirmStock(DefaultNow);
        return order;
    }

    public static Order Shipped()
    {
        Order order = AwaitingPayment();
        order.ConfirmPayment(PaymentReference.Of("test-ref"), DefaultNow);
        order.MarkShipped(TrackingNumber.Of("TRK1"), DefaultNow);
        return order;
    }
}
```

## 12.4 Application tests — real infrastructure

> **Decision — integration tests use real SQL Server and Redis, not in-memory
> substitutes.** See [ADR-010](adr/ADR-010-testcontainers-not-in-memory-providers.md).

The EF Core in-memory provider does not enforce foreign keys, does not
implement `rowversion` concurrency, and translates LINQ differently from the SQL
Server provider. A test suite green against it will still fail in production.
Testcontainers starts a real SQL Server in a few seconds; the fidelity is worth
it.

`ServiceFixture` is one shared body,
`tests/Common.TestSupport/ServiceFixture.cs`, from which each service's fixture
derives with its own names, migrator and stubs
([ADR-056](adr/ADR-056-a-services-fixture-derives-from-one-shared-body-under-tests.md));
Ordering's is `tests/Ordering.TestSupport/ServiceFixture.cs`, and the host it
starts is `OrderingApiFactory` beside it. The excerpts below are the parts that
carry a rule.

**The containers run the engines the Compose baseline runs.** Each tag is read
from §14.1's files through `ComposeImage` rather than copied, so a suite and a
developer's stack cannot disagree about the engine. A service that claims keys
on Redis (§8.5) gets two servers, matching §8.1's split, because otherwise the
suite cannot catch a coordination key written to the evicting instance:

```csharp
private readonly MsSqlContainer _sql = new MsSqlBuilder()
    .WithImage(ComposeImage.Of("sql"))
    .Build();

_redisCache = new RedisBuilder()
    .WithImage(ComposeImage.Of("redis-cache"))
    .WithCommand("--maxmemory-policy", "allkeys-lru")
    .Build();

_redisCoordination = new RedisBuilder()
    .WithImage(ComposeImage.Of("redis-coordination"))
    .WithCommand("--maxmemory-policy", "noeviction")
    .Build();
```

**A service that schedules cannot use a tag at all.** §14.1 builds the broker
rather than pulling it, and the delayed exchange lives in a plugin no official
image carries, so the fixture of a service that schedules builds the same
Dockerfile through `ImageFromDockerfileBuilder` and runs the result. The
failure that forces it is a quiet one: a stock broker connects and reports
healthy, because the exchange is not declared until something schedules, and
the schedule that does then hangs on a declare the broker refuses.
[ADR-021](adr/ADR-021-saga-timeouts-are-scheduled-by-the-broker.md) has the
measurement.

**The host is the real one, with three substitutions, and each is narrow.** The
first is configuration. `AddJwtAuthentication` reads the authority eagerly and
throws naming it (§11.3), so the factory supplies one, deliberately fake and
deliberately unreachable: `.invalid` never resolves, so a test that dials the
authority fails loudly rather than reaching a real identity provider. It is not
`ValidateOnStart`, because the authority is read rather than bound and there is
no options class to validate. The factory supplies only what its host reads:
Ordering calls no peer and never binds `ServiceIdentityOptions` (§9.7), so it
is given no `Identity:Client`, since config the host ignores is how a fixture
ends up disagreeing with the deployment about what a service requires, in the
direction that hides a missing secret.

The second is the scheme. The endpoints under test are behind
`RequireAuthorization` (§11.4), and the alternatives are a 401 on every call or
a fixture that fetches OIDC metadata over the network, so the JWT scheme is
replaced rather than configured:

```csharp
services.Configure<AuthenticationOptions>(o =>
{
    o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
    o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
});

services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
```

Only authenticate and challenge are set, and forbid follows the challenge:
`DefaultForbidScheme` is unset, and the scheme provider falls back to
`DefaultChallengeScheme` before `DefaultScheme`. So the 403 comes from
`TestAuthHandler`'s inherited forbid, a bare status code touching no metadata,
and the wrong-permission test needs no identity provider either.

The third is the background services, removed one by one rather than all
together: MassTransit registers its bus as a hosted service, so
`RemoveAll<IHostedService>()` would stop the broker from starting and silently
disable every consumption test.

```csharp
ServiceDescriptor hosted = services.Single(d =>
    d.ServiceType == typeof(IHostedService) &&
    d.ImplementationType == typeof(OutboxDispatcher));
services.Remove(hosted);

// Still resolvable directly, so tests can drive one pass.
services.AddSingleton<OutboxDispatcher>();
```

The dispatcher polls every `OutboxDispatcher.PollInterval`, and left running it
drains outbox rows underneath assertions about them; tests that want it call
`fixture.ProcessOutboxBatchAsync()`. `RetentionPurgeService` is removed and
re-added the same way: its timer would not race an assertion, but a test
asserting that an abandoned row *survives* retention cannot tell "the pass
spared the row" from "the pass never ran" unless it drives the pass itself.
Every match is on `ImplementationType`, which is why §4.2 registers each with
the generic `AddHostedService<T>` rather than a factory: a factory registration
leaves that property null and the removal would match nothing.

**The rest of the fixture arranges and resets.** Tests collapse §7.1's two
database identities onto the container's `sa` login while keeping separate
keys; production keeps them separate, and the fixture migrates by running the
service's real §7.4 job, never from a host (ADR-007). Between tests the schema
is truncated with Respawn, which is far faster than recreating it or wrapping
every test in a rolled-back transaction, and the second would hide
transaction-related bugs. `ExecuteAsync` takes `{0}`-style placeholders that EF
turns into real SQL parameters, because a formatted string would be both an
injection shape and a CA1305. `SeedOrderAsync` persists a real aggregate
through the `DbContext`, so the row satisfies every invariant §5 enforces: a
raw INSERT drifts from the aggregate the first time it gains a column, and
drifts silently. The outbox helpers, `SetOutboxAttemptsAsync` and
`ExpireOutboxLeasesAsync`, write state explicitly through the columns the
dispatcher writes, so no state carries between tests (§12.8) and a test can
tell "backed off" from "abandoned" without sleeping.

> **`IAsyncLifetime` returns `ValueTask` in xUnit v3.** In v2 both members
> returned `Task`; v3 changed `InitializeAsync` to `ValueTask` and derives the
> interface from `IAsyncDisposable`, so `DisposeAsync` returns `ValueTask` too.
> The v2 shape does not implement the v3 interface, and the compiler's message
> points at the class rather than at the version.
>
> The wider point belongs in [Appendix B](appendix-b-licences.md), not here. That register pins exact
> versions because four dependencies changed licence in two years — but a pin is
> a claim about an API as well as a licence. Pinning a major you have not
> compiled against buys the licence guarantee and none of the correctness.

The test scheme itself is `tests/Ordering.TestSupport/TestAuthHandler.cs`. Tests
state who they are in headers, so authorization runs against a real principal
rather than being switched off, and no header means anonymous rather than
authenticated as nobody, because otherwise every 401 test silently passes:

```csharp
// SchemeName, not Scheme: AuthenticationHandler<T> declares a Scheme, and CS0108 fails the build.
public const string SchemeName = "Test";
public const string UserHeader = "X-Test-User";
public const string PermissionsHeader = "X-Test-Permissions";

protected override Task<AuthenticateResult> HandleAuthenticateAsync()
{
    // No header means anonymous, not authenticated as nobody.
    if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
        return Task.FromResult(AuthenticateResult.NoResult());

    List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

    // PermissionClaim.Type, not a literal, so the claim is the one §11.4's policies require.
    if (Request.Headers.TryGetValue(PermissionsHeader, out StringValues granted))
    {
        claims.AddRange(
            granted
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => new Claim(PermissionClaim.Type, p)));
    }

    ClaimsPrincipal principal = new(new ClaimsIdentity(claims, SchemeName));

    return Task.FromResult(
        AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
}
```

> **Do not give the test principal every permission.** A fixture that hands out
> a blanket claim set makes the [§11.4](11-identity-authorization.md) policies untestable and, worse, makes them
> *look* tested — the endpoints are reached, the assertions pass, and the one
> behaviour nobody ever exercises is the refusal. Grant per test exactly what
> that test's user should have, and keep at least one test per policy that
> grants nothing and expects the rejection.

The handler suite is `tests/Ordering.Api.Tests/PlaceOrderTests.cs`, over HTTP
because the handler binds a subject. Prices are seeded straight into
`ordering.ProductPrices`, which has no aggregate a raw INSERT could drift from,
and a test that places an order seeds one first: the write path reads prices
locally (§6.4), so an unseeded projection refuses the order as unavailable,
which reads as a domain assertion failing rather than as missing fixture data.

Two rules hold for every suite built this way. A helper one suite needs stays
private to that suite, and the fixture carries only what more than one suite
needs. And a test that reads a scoped service, `OrderingDbContext` among them,
resolves it from a scope, `Factory.Services.CreateAsyncScope()`, never from
`Factory.Services` itself, which throws under `ValidateScopes` and, where that
is off, hands back an instance that lives as long as the host.

Two assertions belong to the slice beyond its own cases; the split between the
lanes, the contract type on the Broker lane (§9.3's allow-list) and the domain
type on the Local lane (§7.5), is asserted below it, in
`tests/Common.Application.Tests/DomainEventDispatcherTests.cs`. A repeated
`CommandId` is served entirely by §8.5's replay branch, so the handler runs
once, and both the row count and the replayed value are asserted, in
`One_command_id_carrying_a_second_basket_is_refused_and_one_order_exists`,
because they detect different things: a second persisted order, and a replay
that returned something other than what was stored. And a command id reused by
another customer:

```csharp
HttpResponseMessage mine = await PlaceAsync(product, commandId: commandId);
HttpResponseMessage theirs = await PlaceAsync(product, commandId: commandId, caller: other);
HttpResponseMessage mineAgain = await PlaceAsync(product, commandId: commandId);

mine.StatusCode.ShouldBe(HttpStatusCode.OK);
theirs.StatusCode.ShouldBe(HttpStatusCode.OK);
Guid first = await IdOfAsync(mine);

// Different orders, not two successes: an unscoped key answers the second caller with the first's id.
(await IdOfAsync(theirs)).ShouldNotBe(first, "another customer's command id must not replay this order");

// The replay is what says the varying segment is the subject, not anything else that differs per request.
(await IdOfAsync(mineAgain)).ShouldBe(first);
(await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM ordering.Orders")).ShouldBe(2);
```

`A_command_id_claimed_by_one_customer_does_not_replay_to_another` is the one
test here whose failure is a **disclosure** rather than a defect: an unscoped
key does not throw, log or answer slowly — it answers 200 with another
customer's order id, to a caller authentication and the endpoint policy have
already admitted. Those two still run on a replay; what does not is the handler,
and with it §11.4's binding of the subject from the principal. That is why the
assertion is `ShouldNotBe` against the first caller's value and not
`theirs.StatusCode.ShouldBe(HttpStatusCode.OK)`.

**Three requests rather than two, and the third is the one that carries the
claim.** `ShouldNotBe` establishes only that the key varies with *something*;
a key scoped by a correlation id would satisfy it, and so would a pipeline with
no `IdempotencyBehavior` in it at all — which §6.3's registration-order test
is what catches, since §8.5 says plainly that neither of its reflection tests
reaches the behaviour. Returning to the first subject and getting the
first order back is what identifies the varying segment as the subject. The row
count sits beside all three: two callers, two orders, and the replay adds
none.

The dispatcher gets its own tests, driven explicitly rather than by waiting on a
timer. These are the ones that cover the behaviour §13.6 alerts on — per-row
isolation and attempt accounting — and neither is observable from a test that
lets the background service run:

```csharp
[Fact]
public async Task A_row_stops_being_claimed_at_the_attempt_cap()
{
    OutboxMessage poison = OutboxRows.Poison(fixture);
    await fixture.StageOutboxAsync(poison);
    await fixture.SetOutboxAttemptsAsync(poison.MessageId, 9);

    (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);   // 9 → 10

    // Clears the backoff lease, so the second pass is blocked by the attempt cap alone.
    await fixture.ExpireOutboxLeasesAsync();

    (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

    OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
    row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
}

[Fact]
public async Task A_local_row_with_no_registered_handler_fails_loudly()
{
    // A projection that never runs would otherwise leave every dashboard green.
    await fixture.StageOutboxAsync(OutboxRows.Unhandled(fixture));

    await fixture.ProcessOutboxBatchAsync();

    OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
    row.ProcessedAt.ShouldBeNull();           // not silently completed
    row.LastError.ShouldNotBeNull().ShouldContain("IProjectionHandler");
}
```

The suite is `tests/Ordering.Api.Tests/OutboxDispatcherTests.cs`, and it holds
per-row isolation beside these: a poison row staged ahead of healthy ones is
backed off with its attempt counted and its error recorded, and the healthy
rows are delivered in the same pass. The second test above is the one worth
keeping forever. It asserts the failure mode that would otherwise be invisible:
a projection that never runs while every dashboard stays green.

Contract messages come from a builder rather than inline object initialisers.
`required` members make partial construction a compile error, so every test
would otherwise repeat eight assignments to vary one. The saga suite's is
`tests/Ordering.Application.Tests/SagaContracts.cs`, and its instant is fixed,
as every builder's here is: `OccurredAt` is what §13.3's lag is measured from
and what the saga copies onto its own state, and a builder reaching for the
system clock would have every test assert against a value it had just made.

```csharp
internal static OrderPlaced OrderPlaced(Guid orderId, Guid customerId) => new()
{
    MessageId = Guid.CreateVersion7(),
    CorrelationId = orderId,
    OccurredAt = Occurred,
    OrderId = orderId,
    CustomerId = customerId,
    TotalAmount = Total,
    Currency = Currency,
    Lines = [new PlacedLine(Product, 2, 64.99m)]
};
```

The builders the dispatcher tests use are ordinary factories over
`OutboxMessage`, in `tests/Ordering.TestSupport/Outbox/OutboxRows.cs`: one
class rather than one per case, since they differ only in which event they
stage. The map and the payload format are the real ones, resolved from the
fixture's provider (§9.4), because a double for either would let a test stage a
row the running host cannot read back, which is the one thing these builders
exist to prove does not happen. They take the fixture rather than the map alone
because a staged row needs both halves of the host's agreement about the
format, the persisted name and the converters: a row written without the
`Money` converter round-trips to a zero amount and a null currency.

```csharp
public static OutboxMessage Poison(ServiceFixture fixture) =>
    Local(new AlwaysThrows { OccurredAt = Raised }, fixture);

public static OutboxMessage Healthy(ServiceFixture fixture) =>
    Local(new NoOpEvent { OccurredAt = Raised }, fixture);

public static OutboxMessage Unhandled(ServiceFixture fixture) =>
    Local(new UnhandledEvent { OccurredAt = Raised }, fixture);

private static OutboxMessage Local(object message, ServiceFixture fixture) =>
    OutboxMessage.Stage(
        message,
        OutboxLane.Local,
        Guid.CreateVersion7(),
        fixture.MessageTypes,
        fixture.OutboxJson);
```

`AlwaysThrows` has a registered `IProjectionHandler<AlwaysThrows>` that throws;
`NoOpEvent` has one that does nothing; `UnhandledEvent` has none, which is
precisely what the unhandled-row test exercises. All three are `IDomainEvent`
implementations in the test-support assembly, so `OrderingApiFactory` registers
that assembly before the map is built, beside the `TestAuthHandler`
replacement:

```csharp
// §9.4: added to rather than replaced, so a test cannot stage a type the real host would refuse.
services
    .Single(d => d.ServiceType == typeof(MessageTypeSource))
    .ImplementationInstance
    .ShouldBeSource()
    .Add(typeof(AlwaysThrows).Assembly);
```

Without it, `NameOf` throws on the first builder call and every outbox test
fails before its assertion. The registered instance is mutated, not
re-registered: constructing a second source would compile, pass, and quietly
restate the production list, so the day §4.2 gains an assembly it would be a
copy that does not match and that nothing points at. `MessageTypeSource` is
mutable for exactly this, and the map is built from it on first resolve.

Two assertions belong beside these. The first is the cheapest guard on the
single-identity rule of [§9.1](09-messaging.md), and it takes no fixture at
all, because the thing that can regress is a pure function.

`tests/Common.Infrastructure.Tests/OutboxMessageTests.cs` holds it, with no
`[Collection]` and no fixture, because `Stage` touches nothing. It is not a
§12.3 test either: that level is `*.Domain.Tests`, and `OutboxMessage` is
`Common.Infrastructure`, which is where §12.1's table homes the outbox's table
and type map. A fast test does not have to be a domain test, and moving it to
reach a container it does not use is how a suite acquires a minute of startup
for one assertion. The envelope is that assembly's own `SampleIntegrationEvent`
rather than a service contract, because `Common.Infrastructure.Tests`
references `Common.Infrastructure` and nothing downstream of it — the same fact
that makes the test cheap, read from the other side.

```csharp
[Fact]
public void Stage_takes_both_identities_from_the_envelope()
{
    SampleIntegrationEvent message = new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = Guid.CreateVersion7(),
        OccurredAt = Now,
        Note = "published"
    };

    OutboxMessage row = OutboxMessage.Stage(
        message,
        OutboxLane.Broker,
        correlationId: Guid.CreateVersion7(),
        types: Types,
        json: Json);

    // Both from the envelope, since the mapper decides the correlation (§9.3).
    row.MessageId.ShouldBe(message.MessageId);
    row.CorrelationId.ShouldBe(message.CorrelationId);
}
```

Both come from the envelope rather than being minted, and `CorrelationId` in
particular, because a caller-supplied one is passed in and ignored for an
`IIntegrationEvent`. That argument being silently dropped is the regression the
test exists for.

> **There is deliberately no test asserting the transport headers here.**
> Observing what reached the broker needs an `ITestHarness`, and this fixture
> does not have one: `AddMassTransitTestHarness` (§12.5) builds a standalone
> in-memory bus, whereas `ServiceFixture` runs the real host against the real
> RabbitMQ container on purpose. Bolting a harness onto it would replace the
> bus configuration these tests exist to exercise.
>
> The remaining hop — `DeliverAsync` copying the row's ids onto
> `c.MessageId`/`c.CorrelationId` — is two lines with no branching, and it is
> covered end-to-end by §9.5's inbox tests: those dedupe on `context.MessageId`,
> which only matches a second delivery if the value on the transport is the one
> the row carried. **A test that would need the fixture to become something
> else is a test that belongs elsewhere or nowhere**, and inventing a
> `fixture.Harness` to host it is how a suite acquires infrastructure nobody
> can explain later.

The second is the `Local` lane's payload contract (§9.4), in
`tests/Ordering.Application.Tests/OutboxSerialisationTests.cs`. It needs no
containers, and it lives here rather than in §12.6 because the set it iterates
comes from the service's registered `MessageTypeMap`, and §12.6 selects on the
contracts namespace, which no domain event is in. The options are the
registered ones, converters included: a hand-built `OutboxJson` listing the
service's converters would assert that they work — which nobody doubts — and
stay green if a registration were deleted, while the running host wrote a
zero-valued `Money` into every row. Registration is what can silently go
missing, so registration is what the test resolves:

```csharp
// Not "every IDomainEvent": the map is the set the outbox can actually carry.
using ServiceProvider provider = Registered();
JsonSerializerOptions options = provider.GetRequiredService<OutboxJson>().Options;

foreach (Type type in provider.GetRequiredService<MessageTypeMap>().StageableDomainEvents)
{
    object sample = DomainEventSamples.Create(type);
    string json = JsonSerializer.Serialize(sample, type, options);
    object? read = JsonSerializer.Deserialize(json, type, options);

    // Through the payload, because a record compares its list members by reference.
    JsonSerializer
        .Serialize(read, type, options)
        .ShouldBe(json, $"{type.Name} cannot survive the Local lane");
}
```

`DomainEventSamples.Create` is the same deliberate obstacle `ContractSamples` is
(§12.6): a new domain event with no sample fails here instead of being skipped,
which is the failure mode of every loop over types that falls back to
`Activator.CreateInstance`.

Containers start once per test collection, not per test. Truncating with
Respawn between tests keeps them isolated at a fraction of the cost.

> **A collection is per assembly, and the fixture has to live somewhere both
> can see.** `ServiceFixture` and `TestAuthHandler` are used by a service's
> `*.Application.Tests` (handler tests) and its `*.Api.Tests` (the contract
> tests below), and those cannot reference each other — so both live in
> `*.TestSupport` ([§4.1](04-solution-structure.md)), a library rather than a test project.
> Catalog is where that shape is realised: both its suites take the fixture,
> which is exactly why the library exists.
>
> **Ordering is the exception, and it is the code's rather than this
> section's.** Its handler tests live in `Ordering.Api.Tests`, because
> `ICurrentUser` is `HttpContextCurrentUser` and a handler resolved in a bare
> scope has no principal to bind a subject from — so
> `Ordering.Application.Tests` references no `Ordering.TestSupport`, declares no
> fixture collection, and holds §12.5's saga suite instead, which needs no
> infrastructure at all; its one collection, `OrderFulfilmentSagaCollection`,
> carries no fixture and keeps that suite's buses from running side by side.
> The library is still right for the reason above; it simply has one consumer
> there rather than two. `docs/testing.md` says the same thing from the other
> side.
>
> The `[CollectionDefinition]` does **not** move there. xUnit resolves
> collections within an assembly, so each test project declares its own, naming
> the shared fixture type:
>
> ```csharp
> [CollectionDefinition(nameof(IntegrationCollection))]
> [Trait("Category", "Integration")]
> public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
> ```
>
> **The category goes on the definition rather than on each test class, and
> that placement is the whole of why it cannot drift.** xUnit v3 applies a
> collection's traits to every test in it, so *joining the container collection
> is carrying the category* — there is no per-class attribute for a new test
> class to forget, and therefore no reflection gate needed to check that nobody
> did. The thing that decides the category is the same thing that decides
> whether the test gets a container.
>
> Measured rather than assumed, because that propagation is load-bearing: on
> `Common.Infrastructure.Tests`, `Category=Integration` selects the
> thirty-four tests of the three classes in the collection and
> `Category!=Integration` selects the other seventy-two — 106 as the runner
> counts them, with no third state and nothing counted twice. The figures move
> with the suite, in both directions, so what a reader can check is whether
> this pair matches a run.
>
> **The fast half starts no container, and that is proved rather than
> inferred**: `docker events --filter event=create` over a solution-wide
> `Category!=Integration` run reported nothing, against a probe that captured a
> control container started beside it. The mechanism is that xUnit constructs a
> collection fixture only when a test in that collection runs, so filtering the
> collection out means the container is never asked for.
>
> **A category is not a skip, and the distinction is the one this chapter
> keeps.** A skip on a missing daemon fails open: CI goes green on a runner
> whose Docker broke. The trait decides which *stage* runs a test and never
> whether it may be absent — selected in it needs the daemon exactly as before,
> and selected out it is not reported as passing. A class that needs a
> container and forgets the collection fails loudly in the fast half, which is
> the direction this has to fail in.
>
> **CI runs the halves separately**, as §15.1's two test nodes, with §4.2's
> architecture gates ahead of every instrumented run of the build they read,
> for the instrumentation reason `docs/testing.md` gives.
>
> **Three stages are three new ways to select nothing**, and that is what the
> pipeline gate's `stages` check (`.github/pipeline-gate/`) is for: `dotnet
> test` exits **zero** on a filter that matches no test, so each stage can be
> green and empty. The gate asserts a floor on each stage's count, that every
> test project in `Platform.slnx` ran in one of them, and that no test ran in
> two — the last of which is what makes "exhaustive and disjoint" a check rather
> than a claim.
>
> The consequence is worth stating rather than discovering from a slow
> pipeline: two assemblies mean two collections and therefore **two sets of
> containers** — SQL Server, both Redis instances and RabbitMQ start twice per
> run. That is the price of the pyramid's levels mapping onto separate
> projects, and it is the right trade only while the levels stay separate for a
> reason. Collapsing them into one project halves the container cost and gives
> up the ability to run the fast half alone, which is what §15.1's pipeline
> ordering depends on.

### The order summary's product names

§6.6's projection is the worked case for a set of tests whose subjects are
*different* counterfactuals rather than different inputs, and it is here
because a design can look correct and deliver its payload only by accident: the
one [ADR-027](adr/ADR-027-the-order-summary-stores-product-ids-and-resolves-the-name-locally.md)
rejected delivered nothing in the ordinary flow, which is the failure the grid
below locates precisely.

| | |
|---|---|
| **The ordinary flow** | Publish **three** products with distinct non-null thumbnails, place one order naming all three, read the history. Each resolved `SummaryProduct` carries Catalog's name *and* its thumbnail, and the three arrive **in the order the lines were placed** |
| **The late arrival** | Let a product with a **null** thumbnail reach `ordering.ProductPrices` through `PriceChanged` alone, place an order, then deliver `ProductPublished`. Both facts appear on a summary written before them — retroactively, with no rebuild — and the null survives as null |

**Three products rather than one, and both columns rather than the name.** A
single-product case cannot see line order at all, and the reader reconstructs
it from the stored id array rather than from the second statement's result —
`ordering.Products` is keyed by id and SQL Server promises no order without an
`ORDER BY`, so an implementation that projected the dictionary directly would
return the page in whatever order the join produced and satisfy any
one-product assertion. `ThumbnailUrl` is the same gap one column over:
ADR-027 moves *both* display facts, and a suite that pins one of them accepts
an implementation that drops the other. The null case belongs to the late
arrival because `ProductPublished.ThumbnailUrl` is `string?` and nullable is
the shape Catalog actually promises.

**Neither case subsumes the other, and working out which design each one
refutes is what shows why both are owed.** Three designs are in play: the
rejected one (empty strings, patched by a later `ProductPublished`), the
tempting repair (read the names at insert time), and this one (store ids,
resolve on read).

| | Ordinary flow | Late arrival |
|---|---|---|
| Empty strings, patched later | **fails** — the patch ran when the product was published, before this order existed, and it only touches summaries that already contain the product | *passes* — an order that already exists is precisely what that patch was built for |
| Names read at insert time | passes | **fails** — the price row arrived without a name, and nothing revisits the summary once one exists |
| Ids resolved on read | passes | passes |

A third case is owed for a different reason, and it is the one a suite reaches
last: **a rename, and then a replay of the older event.** Both cases above
insert a row that was absent, so both exercise only the `MERGE`'s
`WHEN NOT MATCHED` branch — an implementation whose `WHEN MATCHED` arm dropped
the `target.UpdatedAt < @OccurredAt` guard passes them both while silently
letting a stale `ProductPublished` overwrite a newer name. The per-product
watermark is the property the whole table shape was chosen for (ADR-027), and
nothing above tests it. Publish, change **both** the name and the thumbnail,
then redeliver the first event: both newer values stand.

**Both, because this is the only specified case that enters `WHEN MATCHED`.**
If that arm stopped assigning `ThumbnailUrl` — the easiest column to lose in a
`MERGE`, since the insert branch above it would still carry one — every other
case here still passes, because none of them ever updates a row.

Two more cases exist because a *branch* of the reader has no case above, which
is the same argument as the replay and worth keeping separate from the design
grid:

| | |
|---|---|
| **The unresolved id** | Read the page **before** delivering `ProductPublished`. The unnamed product is absent from `Products` and `LineCount` still counts it. Without this, deleting the reader's `Where(named.ContainsKey)` filter — and throwing on the dictionary lookup instead — satisfies every other case here |
| **The wide page** | Seed enough orders to put more than 2100 distinct ids on one page: twenty-two orders of a hundred items reaches 2200, which is well inside §6.5's clamp of a hundred orders. The page returns. Without this, an implementation that expands the ids into an `IN` list passes every other case, because they all use a handful of products, and fails in production on SQL Server's parameter ceiling |

**The second is the one that would otherwise be documentation rather than a
rule.** ADR-027 argues the parameter limit at length and the sample uses
`OPENJSON` because of it; nothing above would notice the `IN` list coming back.
A limit stated in prose and enforced by no case is the shape this repository
already names — a claim that reads as settled and is not.

So the ordinary flow is what catches the design ADR-027 rejected, the late
arrival is what catches the cheaper repair that would otherwise look
equivalent, the replay is what catches an update path neither of the others
enters, and the last two catch reader branches that no case about the *design*
reaches. A suite carrying only one of them leaves a whole design
indistinguishable from this one — which is §12.4's rule about negatives applied
to a design rather than to an assertion: a case that two candidates both
satisfy is not evidence about which is present.

**Every case above is a §12.4-level test and all of them belong in
`Ordering.Api.Tests`, which is not a contradiction.** The level is about what
they touch — the projection writes through Dapper against a real schema, so an
in-memory double would assert on the double — and the project is about where
the fixture lives. `Ordering.Application.Tests` deliberately carries no
`Ordering.TestSupport` reference and says so in its csproj, so Ordering's
real-schema tests are homed one project over; `ProductPriceProjectionTests` is
already there for exactly this reason. Read the pyramid's levels as a statement
about what a test exercises, never as a mapping onto assembly names.


### API contract tests

The pyramid's third level (§12.1) goes through HTTP, and it exists to cover
what the levels below it structurally cannot: the endpoint's authorization, its
status codes, and its serialisation. A handler test proves the decision; only
this proves the decision reaches the wire intact.

The cancel endpoint's suite is
`tests/Ordering.Api.Tests/OrderOwnershipTests.cs`, and each case asserts a
status and, where the request was refused, that the order is unchanged:

```csharp
[Fact]
public async Task User_A_cancelling_user_B_s_order_gets_404_and_not_403()
{
    // 404 rather than 403, which would confirm the order exists; and the order must still be there after.
    OrderId order = await SeedOrderAsync(Bob);

    HttpResponseMessage response = await CancelAsync(order, asUser: Alice);

    response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    (await StatusOfAsync(order)).ShouldBe(
        nameof(OrderStatus.AwaitingStock),
        "the 404 must be a refusal, not a cancellation reported as a miss");
}
```

Four of its cases are four distinct failures, none reachable from below the
HTTP boundary: an endpoint that lost its `RequireAuthorization` (401 becomes
200), a policy name that resolves to nothing (403 becomes 200), a resource
check returning the wrong status (404 becomes 403, leaking existence), and a
reason parsed by `Enum.TryParse` instead of the wire vocabulary (400 becomes
200, and the enum's member names quietly become API surface). Each is a defect
this document argues about in prose, and these are where it is asserted. The
401 case sends no `X-Test-User` header, so `TestAuthHandler` returns
`NoResult` and the challenge stands; what it catches is the *policy* being
dropped from the endpoint, not `UseAuthentication` being dropped from the
pipeline. The 403 case authenticates with a permission the endpoint's policy
does not accept — the case a fixture that grants everything hides — and writes
it as a literal, because the vocabulary holds what endpoints require, and
naming a refused permission from it would read as though one existed for it.
The 404 case is §11.4's: a 403 would confirm the order exists, and it is
invisible to a handler test that asserts on `Result.Failure` alone.

> **None of them catches a missing `UseAuthentication`, and nothing in a
> `WebApplication` host can.** Delete the line from a host's `Program.cs` and
> every test in the repository stays green. `WebApplication` adds the
> authentication and authorization middleware **itself** whenever the matching
> services are registered; an explicit call moves them earlier in the pipeline,
> which is what a composition root wants — they have to sit above anything that
> logs the caller — but it is not what puts them there.
>
> Keep the explicit calls: they are §4.2's specified shape, they are required
> by any host that is not a `WebApplication`, and a pipeline whose order is
> implicit is one nobody can review. `Common.Web.Tests` carries the four
> assertions that are actually true — that the middleware is what populates
> `HttpContext.User`, that the authorization middleware does not authenticate
> on its own, that a `WebApplication` auto-adds it, and that auto-insertion
> does **not** repair the two calls being written in the wrong order. The third
> is a regression guard on the framework: were a release to stop doing it,
> every service would hand anonymous callers to its handlers while its
> authorization kept passing. The fourth is what
> [§4.2](04-solution-structure.md)'s ordering row rests on, and why that row
> does not call reversal harmless.

### The subject rule, enforced

[§11.4](11-identity-authorization.md)'s subject rule is the kind of rule that
holds by omission — a command with no `CustomerId` field cannot be pointed at
another customer — and a rule that holds by omission is one a later refactor
reinstates without noticing. The tests below are what make it fail loudly
instead.

They are split by what can produce the state each one needs. Two are about a
caller a request carries, and run over HTTP, where the principal comes from
`TestAuthHandler`'s headers through `HttpContextCurrentUser` exactly as
production resolves it; a third has no Ordering test yet:

- **An order is attributed to the caller** —
  `The_order_is_attributed_to_the_caller_and_not_to_anything_in_the_request`
  in `PlaceOrderTests`. The row's owner is read back from the table and must be
  the principal the request's headers named.
- **An owner cancels their own order** — `The_owner_can_cancel_their_own_order`
  in `OrderOwnershipTests`. The control for the 404-not-403 refusal beside it,
  which would otherwise pass on a handler that answers 404 to everybody.
- **A customer reads only their own orders** has no Ordering test, because
  Ordering's own history query, §6.5's as §6.6 escalates it, is not built. The
  buyer's history the BFF serves is under the same rule
  ([§10.7](10-api-gateway.md)), and `Web.Bff.Tests`' `OrderEndpointTests`
  asserts a stranger's page is empty.

Two more are a pair, and run below HTTP in `SagaCommandHandlerTests`, which
dispatches in a bare scope — no request, so no principal:

```csharp
[Fact]
public async Task A_system_initiated_cancellation_publishes_the_workflow_origin()
{
    // The System case alone: a User-origin command has no principal in a bare scope, so §11.4's guard
    // refuses it first. Read off the outbox row, the payload a consumer sees.
    Guid orderId = await fixture.SeedOrderAsync(Customer);

    Result cancelled = await DispatchAsync(
        new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.System));

    cancelled.IsSuccess.ShouldBeTrue();

    // The Broker row; §6.6's projection stages the domain event on the Local lane beside it.
    OutboxMessage row = (await fixture.OutboxAsync())
        .Where(r => r.Lane == OutboxLane.Broker)
        .ShouldHaveSingleItem();

    row.Payload.ShouldContain(
        $"\"Origin\":\"{CancelOrigins.Workflow}\"",
        Case.Sensitive,
        "the saga's own CancelOrder must echo back as this workflow's doing");
}

[Fact]
public async Task A_user_command_with_no_caller_is_refused()
{
    // The pair of the test above, and the one state HTTP cannot produce: RequireAuthorization answers a
    // caller-less request 401 before the handler, so only a bare scope reaches §11.4's guard without one.
    Guid orderId = await fixture.SeedOrderAsync(Customer);

    Result cancelled = await DispatchAsync(
        new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.User));

    cancelled.Error.ShouldBe(OrderErrors.NotFound);
    (await StatusAsync(orderId)).ShouldBe("AwaitingStock", "a refusal mutates nothing");
}
```

These two run at the dispatcher rather than over HTTP, and that is not a
shortcut. They describe states HTTP cannot produce against §11.4's endpoint
group: `RequireAuthorization` turns a caller-less request into a 401 before any
handler runs, so a fail-open in the handler's own check is invisible from
outside, and the compensation path has no HTTP surface at all. The
API-contract tests above cover the boundary; these cover the check.

**The pair only means something together.** One asserts the check refuses a
caller-less user command; the other asserts it still lets the saga through.
Either alone is satisfied by a handler that is simply wrong in the other
direction, and the direction that fails silently — refusing compensations —
surfaces as orders stuck in `AwaitingStock` long after the deployment that
caused it.

**The system case states its origin rather than earning it.** It constructs
`CommandOrigin.System` directly, which is the right way to test the *check* —
and it leaves the only production code that assigns it, `CancelOrderMapper`
(§9.4), unasserted. A mapper stamping `User` would pass the pair and reject
every real compensation — the failure the pair was written to catch, arriving
by the one route the pair cannot see. Two short tests in
`tests/Ordering.Application.Tests/CommandMapperTests.cs` close it — the stamp,
and the parse that stands in front of it:

```csharp
[Fact]
public void The_mapper_is_what_makes_a_message_system_initiated()
{
    CancelOrderCommand command = new CancelOrderMapper().Map(new CancelOrder(Order, CancelReasons.OutOfStock));

    // Both halves of what the mapper does: every recognised code could map to the wrong domain reason and this
    // test would still pass on the origin alone.
    command.InitiatedBy.ShouldBe(CommandOrigin.System);
    command.Reason.ShouldBe(CancellationReason.OutOfStock);
}

[Fact]
public void An_unknown_reason_code_never_becomes_a_command()
{
    // §9.4's retry policy ignores ContractMappingException, so this sends a malformed message to the error queue
    // on the first attempt rather than after a minute of backoff.
    CancelOrder message = new(Order, "invented_last_release");

    Should.Throw<ContractMappingException>(() => new CancelOrderMapper().Map(message));
}
```

No `[Collection]` and no fixture: the mapper is a pure function, so these sit
in a container-free project, as `Stage_takes_both_identities_from_the_envelope`
above does in another. They are the second half of a boundary whose first half
is the endpoint's literal, and that half is already covered: the API-contract
tests reach the handler through HTTP, so they fail if `User` stops being
stamped.

### Gateway configuration tests

The gateway's suite sits here rather than beside `Common.Web.Tests`, and the
distinction is the entry point: `Common.Web` is a library with none, so its
tests build a pipeline by hand, while the gateway is a host and the thing under
test is the configuration *that host* loaded. It is the one suite in this
section that starts no container — the edge owns no database
([§10.1](10-api-gateway.md)) — and the one that reads a shipped configuration
file as a subject rather than as setup.

Two of its assertions have no other home, and they are in
`tests/Gateway.Api.Tests/RouteConfigurationTests.cs` and `ProxiedRouteTests.cs`
beside it. The first is that the host accepted every route in the file: policy
names are resolved when [§10.2](10-api-gateway.md)'s configuration loads, and a
route whose id went in and did not come out is a path that stopped existing.

```csharp
[Fact]
public void Every_route_in_the_file_is_a_route_the_proxy_accepted()
{
    IReadOnlyList<RouteConfiguration> configured = ReadRoutes();
    IProxyStateLookup lookup = factory.Services.GetRequiredService<IProxyStateLookup>();

    string[] accepted = [.. lookup.GetRoutes().Select(r => r.Config.RouteId).Order(StringComparer.Ordinal)];

    accepted.ShouldBe([.. configured.Select(r => r.Id).Order(StringComparer.Ordinal)]);
}
```

The second is the prefix strip, and it is asserted against a request rather
than against the file that asks for it — a stub server on an ephemeral
loopback port, standing in for the service and recording the path it was
given:

```csharp
[Fact]
public async Task The_service_receives_the_path_with_the_namespace_prefix_removed()
{
    using StubbedGatewayFactory factory = new(stub.Address);
    using HttpClient client = factory.CreateClient();

    HttpResponseMessage response =
        await client.GetAsync("/api/v1/catalog/products", TestContext.Current.CancellationToken);

    response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    // The LAST path, not any path: the stub is a class fixture and a
    // neighbouring test sends this same path, so ShouldContain could pass on
    // that test's entry while this request was forwarded wrongly.
    stub.ReceivedPaths.Last().ShouldBe("/v1/catalog/products");
}
```

> **A listener, not an address that refuses.** A refused connection looks free
> and is not: measured, pointing the clusters at `127.0.0.1:1` costs about two
> seconds a request, so exhausting §10.3's hundred-request window takes three
> and a half minutes, the window replenishes before the last request arrives,
> and the rate-limit test fails while the limiter is working perfectly. A stub
> that answers is faster *and* is the only thing that can observe the forwarded
> path.

> **One property in this suite cannot be asserted over `TestServer` at all**,
> and it is the case that says where the seam is. §10.1's body ceiling is a
> Kestrel option, and `TestServer` is not Kestrel — it implements none of the
> body-size features, so `ConfigureKestrel` is a no-op under it and the ceiling
> does not exist. `WebApplicationFactory.UseKestrel(0)` takes an ephemeral
> loopback port for the stub's reason, and the order is load-bearing: it throws
> once the host is initialised, and `CreateClient` is what initialises one, so
> a factory whose client is taken first is silently a `TestServer` again.
>
> **The failure is loud or silent depending on what the suite asserts, and only
> one of those is a trap.** Run over `TestServer`, the size-limit suite goes
> red: the oversized bodies reach the destination and answer 204 where 413 was
> expected. What passes is the one test asserting that a body *at* the ceiling
> is forwarded — so a suite written from the acceptance side alone would be
> green against a gateway with no limit whatsoever. That is the shape to guard
> against, and asserting the boundary from both sides is the guard.
>
> The rule this leaves is worth carrying past the gateway: drive `TestServer`
> for anything the *application* decides, and a real server for anything the
> *server* decides. The configuration is the part that fails silently — it
> binds, it reports nothing, and it governs nothing.

### The outbound hop

The pyramid's outbound-hop row is each caller's own suite. `Web.Bff.Tests`
holds §9.7's pricing hop, whose three properties no suite outside this row
can reach: a timeout hierarchy, a credential handler's *position*, and a
realm that nothing compiles against.
`Shipping.Worker.Tests` holds the first two for the address read
[ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
adds, against a stub Ordering on loopback. `Notifications.Worker.Tests` holds
all three for the contact read the same record adds: the first two against a
stub Keycloak on loopback, and the realm against a real one, which is also the
owner the read asks.

The hierarchy is read off the **built host** rather than recomputed from the
numbers a helper returns — which is what makes it a test of the registration:

```csharp
HttpStandardResilienceOptions options = factory.Services
    .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
    .Get(PricingHop.ResilienceOptionsName);

(attempts + backoff).ShouldBeLessThanOrEqualTo(options.TotalRequestTimeout.Timeout);
```

It is also self-checking about the options name, which is why §9.7 registers a
**named** client: ask for the wrong key and `IOptionsMonitor` hands back a
default instance whose 30 s total timeout fails this assertion at once, rather
than a configuration that silently was never read.

The credential handler's position is the harder one, because both orderings
compile, start and serve. It is observable only through a stub that records
what arrived:

```csharp
_catalog.AbortNextCalls = 1;                  // a transport fault — see below

await client.Quote("GBP", ct, (Chair, 1));    // one product, one of it

presented.ShouldBe(["Bearer token-1", "Bearer token-2"]);
```

> **Two attempts, two *different* tokens, and a constant token would make the
> correct pipeline and the reversed one produce identical bytes.** That is the
> whole assertion: registered outside the resilience handler, the credential
> handler runs once per request rather than once per attempt, and both lines
> read `token-1`.
>
> **The fault has to be a transport one**, and that is not a detail of the
> stub. A gRPC outcome travels as `grpc-status` on an HTTP **200**, so the
> resilience pipeline reads it as success and retries nothing — a stubbed
> `Unavailable` produces one attempt and measures neither ordering. Only an
> aborted connection reaches the retry. §9.7's callout carries the same point
> from the other side.

The realm is the third, and only a suite that holds it starts a real Keycloak
(§11.5). Everything else points at an unreachable authority on
purpose, so no other test here validates a token Keycloak issued — nothing
compiles differently when the realm is wrong.

## 12.5 Testing the saga

Saga logic is where cross-service bugs live, and MassTransit's in-memory test
harness makes it testable without any infrastructure at all. The suite is the
`OrderFulfilmentSaga*Tests` classes in `tests/Ordering.Application.Tests/`,
mostly one per state, over the registration they share in
`OrderFulfilmentSagaHarness.cs`:

```csharp
new ServiceCollection()
    .AddMassTransitTestHarness(x =>
    {
        x.SetTestTimeouts(TestTimeout, InactivityTimeout);
        x.AddDelayedMessageScheduler();
        x
            .AddSagaStateMachine<OrderFulfilmentSaga, OrderFulfilmentState>()
            .InMemoryRepository();
        x.UsingInMemory((context, cfg) =>
        {
            cfg.UseDelayedMessageScheduler();
            cfg.ConfigureEndpoints(context);
        });
    })
    .BuildServiceProvider(true);
```

`TestTimeout` is sixty seconds and `InactivityTimeout` ten, the ceiling
deliberately six times the bound meant to fire. Thirty is MassTransit's own
default for the test timeout, so a registration setting it there states nothing
and inherits the number the trap below is about. The two scheduler lines are
the ones production registers (ADR-021), and they are not optional here: §9.6's
`Initially` arms `StockTimeout`, so the first `OrderPlaced` reaches for a
scheduler. The in-memory transport implements the delay itself where RabbitMQ
needs a plugin — the transports differ and the registration under test does
not.

```csharp
[Fact]
public async Task Payment_declined_releases_stock_before_cancelling()
{
    (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
    await using (provider)
    {
        var orderId = Guid.CreateVersion7();

        await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));
        (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

        await Publish(harness, SagaContracts.StockReserved(orderId));
        (await Sent<AuthorisePayment>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

        await Publish(harness, SagaContracts.PaymentDeclined(orderId, "insufficient_funds"));

        (await Sent<ReleaseStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();

        // CancelOrder must not go until the release is confirmed.
        (await NotYetSent<CancelOrder>(harness, m => m.OrderId == orderId)).ShouldBeFalse();

        await Publish(harness, SagaContracts.StockReleased(orderId));

        // The reason, not just the send, since a transition that never set it sends null.
        (await Sent<CancelOrder>(harness, m => m.OrderId == orderId && m.Reason == CancelReasons.PaymentDeclined))
            .ShouldBeTrue();
    }
}
```

The contracts come from `SagaContracts` because every member of every V1
contract is `required` unless §12.6's additive-member list names it — the §9.1
envelope included — so there is no partial construction to elide, and
`new StockReserved { OrderId = orderId }` does not compile: the three envelope
members are as required as the payload, which is the point of §9.1 declaring
them on an interface rather than leaving them to convention. `Publish` is the
suite's helper, not `harness.Bus.Publish`: it returns only once the saga has
consumed the message, so the ordering is its job and not the caller's, and
three bare `harness.Bus.Publish` calls in its place would be the race the trap
below prices. The `Sent` lines are assertions, each naming the command a
transition owes, and they are `Sent`, not `Published`, because the saga issues
these as commands to a single owner (§9.6): the harness tracks the two
separately, so asserting on `Published` would fail while looking like a saga
defect.

`CancelOrder` must not be sent until stock is confirmed released, and "not yet"
needs a point in time to be false *at*. The `Publish` before it is that point,
since it returned only once the saga had consumed `PaymentDeclined`, which is
why no `Consumed` assertion stands there. `NotYetSent` then reads the record as
of that point on an already-cancelled token: no wait, no deadline for a late
saga to hide inside, and the harness's one shared inactivity token left unspent
for the assertion after `StockReleased`. The traps below explain why each of
those matters. The last assertion reads the reason, not just the send, because
both exits from `Compensating` read `ctx.Saga.CancelReason` (§9.6), so a
transition that forgets to set it on entry produces a `CancelOrder` carrying
null, which `Any<CancelOrder>` alone would not catch.

```csharp
[Fact]
public async Task Commands_are_sent_and_events_are_published()
{
    // §9.6's distinction: a command published rather than sent reaches every subscriber of its type.
    (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
    await using (provider)
    {
        var orderId = Guid.CreateVersion7();

        await Publish(harness, SagaContracts.OrderPlaced(orderId, Customer));

        // The positive first gives the negative a point in time to be false at.
        (await Sent<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeTrue();
        (await NotYetPublished<ReserveStock>(harness, m => m.OrderId == orderId)).ShouldBeFalse();
    }
}
```

Its negative is the one in a saga suite that can never match, since the whole
claim is that a command is not published; left on the ordinary token it would
bill the inactivity timeout on every green run, so it reads the record as of
the positive above it instead. `StartHarnessAsync` returns the provider as well
as the harness, and the caller owns it: dropping the `await using` leaks a
running bus into whatever runs next.

> **Trap — two consecutive publishes are a race, and losing it fails a
> different assertion.** `harness.Bus.Publish` returns when the message
> reaches the transport, not when the saga has consumed it, and nothing orders
> two publishes against each other. **The backticked name matters here**: the
> samples above call a suite helper also called `Publish`, and that one waits.
> The trap is about the transport call underneath it. So `StockReserved` behind
> `OrderPlaced` can
> reach the endpoint before anything has created the instance — discarded in
> silence, since a non-initial event with no instance is consumed cleanly — or
> before the instance has reached the state that handles it, which faults. The
> failure surfaces nowhere near the publish: the test runs on, and the next
> waiting assertion bills the inactivity bound and reports a command the saga
> did not send. Measured by forcing the losing order — a scheduled
> `PaymentAuthorisationExpired` published before `StockReserved` — the
> compensating `ReleaseStock` is never sent and the assertion returns after
> 10.1 s, which is the shape CI reported on a merge commit.

> **Put the wait inside the publish helper, not at the call sites.** Per-site
> waits are the obvious fix and they fail open: the test that forgets one is
> the test that flakes, and it flakes on a loaded runner and nowhere else. A
> helper that publishes and then waits for **that message** to be consumed
> leaves nothing to forget. **On its own id, not
> its type**: a suite that delivers one type twice — a redelivery, a duplicate
> — would otherwise match the first delivery and fence nothing, silently. It
> costs nothing on a green run, because the consume it waits for is the one
> already happening; what it spends the inactivity bound on is a message no
> consumer takes, which is a real defect reported where it occurs rather than
> four assertions later.
>
> ```csharp
> internal static async Task Publish<T>(ITestHarness harness, T message)
>     where T : class
> {
>     Guid? messageId = null;
>     await harness.Bus.Publish(
>         message,
>         context =>
>         {
>             // §9.1: body, row, header and inbox key are one GUID.
>             if (message is IIntegrationEvent integrationEvent)
>             {
>                 context.MessageId = integrationEvent.MessageId;
>                 context.CorrelationId = integrationEvent.CorrelationId;
>             }
>
>             // The send context, because a scheduled expiry has no envelope to read an id from.
>             messageId = context.MessageId;
>         },
>         TestContext.Current.CancellationToken);
>
>     // Unset, null == null would match the first consume of T and fence nothing.
>     messageId.ShouldNotBeNull();
>
>     (await ConsumedWithId<T>(harness, messageId)).ShouldBeTrue(
>         $"a published {typeof(T).Name} was never consumed, so this barrier cannot say the " +
>         "next publish is ordered after it — an unfenced publish is a race the runner loses " +
>         "under load, and it fails a later assertion wearing the wrong component's name.");
> }
> ```
>
> `ConsumedWithId`, beside it in `OrderFulfilmentSagaHarness`, is
> `harness.Consumed.Any<T>` matched on `Context.MessageId`. **The failure text
> names no consumer**, and that is not fastidiousness: the helper fences
> whatever is bound to the message, which in a saga suite is the saga and in
> the barrier's own guard is a consumer the test holds open. A message naming
> the saga sends a reader of a routing failure to a component that was never
> registered.

> **The wait reads the send context because that is the one handle both
> kinds of message carry** — not because a contract has a second identity. It
> has not: §9.1's body, row, header and inbox key are one GUID, and
> `IIntegrationEvent` says the envelope's value is *the* message id "not a
> second one" — **and that `CorrelationId` follows the same rule for the same
> reason**. So the helper writes **both**, as `OutboxDispatcher` does; what it
> reads back for a contract is the envelope's own value. Copying only the
> message id is a half-measure that leaves the correlation as the second
> identity the rule is about. A message with no envelope — a scheduled timeout,
> or any bare record — needs no such write, which is why the rule is about
> contracts rather than about publishes. A saga's scheduled timeouts are not
> contracts — `StockReservationExpired`'s own doc comment argues why — and have
> no envelope, which is the case the send context covers and the payload
> cannot.
>
> **Letting MassTransit mint the header instead is the trap
> `IIntegrationEvent` names**: every event gets two identities, one the payload
> carries and one the broker uses, and **nothing fails** — the suite stays
> green. Leave `messageId` unset and the comparison becomes `null == null`,
> which matches the first consume of the type and quietly restores the defect,
> so assert it before waiting on it.
>
> **What the barrier therefore cannot do is separate two deliveries of one
> message**, since they share the id by design — that is §9.5's inbox's job, not
> a barrier's. A suite wanting two arrivals it can tell apart publishes two
> messages, not one object twice.
>
> **A fault releases the barrier too, and that is correct rather than a hole.**
> The harness records a delivery whether the pipeline returned or threw, so an
> event the machine has no branch for satisfies this wait as readily as one it
> handles. The barrier is about **ordering** and never about outcome; a test
> whose subject is the outcome reads `Consumed.Select<T>(spent).Exception`, as
> the callout further down insists. What the wait cannot be satisfied by is a
> message **no consumer takes at all**, which is the case worth the ten seconds.

> **A barrier is only ever observed working, so give it a test whose subject is
> the barrier.** Every test a saga suite already had stays green with the wait
> removed — on an unloaded machine, which is every machine a developer has.
> The one that does not is a test that publishes and then reads the record **as
> of now**, on a cancelled token, asserting the transition's command is already
> there. **The two ways the helper can break do not fail the same assertion,
> and collapsing them is the rounding-off this technique exists to refuse.**
> With no wait at all, that first command count fails deterministically. With a
> wait on the message *type* it does not: a type delivered once is fenced
> correctly, so what fails is a later assertion over a **duplicate** — and only
> as a race, since the early-returning publish leaves the second consume in
> flight and the spent-token read may legitimately see it.

> **A guard that usually fails is the fail-open shape wearing a test's
> clothes**, so the barrier gets a second test that does not ask the saga, in
> `OrderFulfilmentSagaBarrierTests`. A
> state machine's transitions return at once, so every question put through one
> is answered by whichever of two fast operations finished first. Register a
> consumer the test **holds open** — one that signals arrival and then awaits a
> `TaskCompletionSource` the test owns — and both halves stop depending on
> timing: publish, wait for arrival, and assert the publish task is *not*
> complete. It is false at once if the helper does not wait, and false again
> for a second message of an already-consumed type, which is precisely the
> type-level wait. Measured on this repository: red on three runs of three for
> each arm, where the saga-driven version was red once and honest about it.
>
> **Release the gate in a `finally`.** Without one, a failing assertion leaves
> the consumer blocked, the harness never drains and disposal never returns —
> so the run **hangs** instead of going red. Measured that way round, a
> ten-minute runner is spent and nothing named. A hang is a worse outcome than
> the race it replaces, because a red says which assertion and a hang says
> nothing at all.

> **Trap — the harness gives up after 1.2 seconds, and the timeout named
> `TestTimeout` is not the one that says so.** An `Any(…)` ends at the
> **earliest** of four things: a match, `TestInactivityTimeout` (default 1.2 s,
> measured from the last bus activity), `TestTimeout` (default 30 s, measured
> from the call), and the caller's `CancellationToken`. With the defaults the
> inactivity bound always wins, which is why raising `testTimeout` alone looks
> like a fix and changes nothing — and why the two must be read as a pair
> rather than one being dismissed. All four were measured at the 8.5.3 pin, the
> decisive case being `testTimeout: 2 s` against `testInactivityTimeout: 10 s`,
> which gave up after 2 s.

> **Inherit either and a saturated runner fails the suite wearing the
> assertion's own message** — a saga that did not send, rather than a runner
> that did not schedule. That costume is the danger: a consume asserted under
> the inherited bound fails on a slow runner and passes on a re-run of the same
> commit, which is why both bounds are stated in `OrderFulfilmentSagaHarness`
> rather than left to a default.
> State both, and keep the ceiling clear of the bound meant to fire, so which
> one reported a failure is never a detail of how long the publish took.

> **A matching assertion returns at once; an unmatched one bills the timeout —
> but only the first one does.** MassTransit shares a single inactivity token
> across every list on a harness and cancels it for good once inactivity is
> reached, so a test pays the full wait once however many negatives it
> asserts, and every later unmatched assertion returns `false` immediately.
> Measured at the pin: a second negative on a fresh message type came back in
> 0.0 s where the first took the whole 3 s. That prices the value — a test
> that lets any negative wait has a floor of one inactivity timeout — and it
> is why 10 s here, where a composition smoke asserting only positives never
> pays it and can afford 30 s.

> **The spent token is a correctness trap, not merely a timing one.** Once it
> is cancelled an assertion can only inspect what has already been recorded:
> the same probe returned `True` after 0.4 s with no prior negative, and
> `False` immediately with one. A mid-test `ShouldBeFalse` that waits therefore
> poisons every assertion after it that needs something to arrive. The
> synchronous `Select` overload is no escape: it waits on the same token.

> **So a mid-test negative should not wait at all.** Give "not yet" a point in
> time to be false at — which, where the negative follows a message the test
> published, the fencing `Publish` above has already supplied, since it returns
> only once that message has been consumed. Elsewhere it is a positive assertion
> the test makes for itself. Either way, read the record as of that point with
> an already-cancelled token. Measured at the pin, it returns `false` for what
> is absent and `true` for what is present, both immediately, and leaves the
> shared token unspent for the assertion that follows. A *deadline* is the wrong
> tool and fails open: a window is something a late-sending saga fits inside,
> and the later positive would then accept the very command the negative was
> there to forbid. A negative that is its test's last assertion *may* simply
> wait — nothing after it is poisoned — but "may" is not "should":
> `Commands_are_sent_and_events_are_published`, left to wait, would wait for a
> publish its own subject guarantees will never come, and pay the full
> inactivity bound on every run for an answer already known. **Use the
> cancelled token for every negative and the question stops arising.**

> **`Consumed` says a message arrived and never what happened to it.** The
> harness records the delivery whether the pipeline returned or threw, so
> "nothing changed" — no transition, no command sent — is exactly what a
> saga event **faulting onto the error queue** looks like from every assertion
> on this page. A state machine's default answer to an event no state handles
> is `UnhandledEventException` ([§9.6](09-messaging.md)), so a suite that proves
> a stale timeout "changes nothing" from `Consumed` alone is green against both
> outcomes. A test whose subject is absorption has to read
> `harness.Consumed.Select<T>(spent).Select(m => m.Exception)` — the harness's
> `ConsumeFaults<T>` — and assert every element is **null**, and on the
> cancelled token, for the reason above, or the read spends the shared bound
> and every assertion after it answers falsely.
>
> **Null, not empty, and the difference is the whole test.** `Consumed` records
> one entry per delivery whatever the outcome, and `Exception` is null on a
> clean one — so the sequence has an element per absorbed message, and an
> emptiness assertion fails on exactly the result the test exists to prove.
> `ShouldAllBe(e => e == null)` is what the suite runs.

> **A missing scheduler fails this suite in the costume the traps above
> describe, which is why the registration is spelled out rather than trimmed.**
> The two scheduler lines are easy to read as ceremony. They are not: with both
> deleted, every test that places an order fails, because `Initially` arms the
> stock timeout and the schedule throws, and the saga's exception faults onto
> the error queue where no assertion sees it. **The ones that wait on a command
> or a saga state fail as timeouts**, each reporting what the saga never
> reached; the barrier test's as-of-now count fails at once, as its trap above
> says. The tests that never schedule pass throughout: the structural ones that
> construct the state machine without a bus, and those whose subject schedules
> nothing, such as an event for an order with no instance. Passing tests beside
> a deleted registration leave it looking half-covered, which is worse than a
> suite that fails whole.

> **Where the numbers live is the other half.** Both samples get them from
> `StartHarnessAsync`, which builds `OrderFulfilmentSagaHarness`'s one
> registration, so they are stated once per harness: copy them per test and one
> test can quietly run on a different wait from its neighbour, leave them out
> and it is the first trap rather than a saving. **Neither sample spends the
> inactivity timeout**, which is the whole point of the technique — both read
> the record on a cancelled token instead of waiting. The bounds still have to
> be stated: they are what a *positive* assertion waits under, and a saturated
> runner is exactly when one takes longer than 1.2 seconds to arrive.

## 12.6 Contract tests

The saga tests above prove one service's coordination. **Two things are
genuinely *between* services**, and they are tested in almost opposite ways: the
contract assembly, whose rules are about *shape* and hold for every contract
there will ever be, and [§9.7](09-messaging.md)'s synchronous calls, whose
rules are about *behaviour* and are each one consumer's. The first is below; the
second is the consumer-driven contract at the end of this section, which also
says which call earns one.

The contract assembly's rules are all stated elsewhere as things reviewers
should notice: §9.1's "a contract may not name a domain type", §9.2's versioned
namespace, `required` members, and
[ADR-028](adr/ADR-028-a-money-movement-command-carries-no-subject.md)'s rule
that a command carries no subject. The first three are mechanical, so each is a
test rather than a review note.

**The fourth is not, and saying so is the honest half.** "Spelled like a
subject" is a list of six substrings, so `OwnerId` passes it — the deny-list
failure, the shape of a check that enumerates the states it refuses instead of
the one it accepts. What makes the rule hold is the allow-list beside it: every
member the judged commands may carry is enumerated, and a name absent from that
list fails the build. That does not classify the new member — it forces
somebody to, which is the most a test can do about a rule whose vocabulary
cannot be closed.

**The fourth is the one whose gate needs a gate**, and it is worth saying here
rather than only at the test. The other three fail against a type that is
present: a domain type named, a namespace misspelt, a member not `required`.
This one asserts an **absence**, so an empty result is both what success looks
like and what a broken detector looks like. It therefore ships with controls
rather than alone — pointing the detector at a contract that *does* carry a
subject and requiring it to find one, naming every command root the judged set
must contain, asserting the exempt types are excluded, and exercising **every
declared spelling** rather than the first — because a gate that has only ever
been observed green is one nobody has established is looking at anything.

**No count of them here, deliberately**: a stated total is the half that goes
stale. What the set is for is checkable; how many there are is not worth a
second place to be wrong.

**The spelling vocabulary is the case worth naming, because a control can have
the defect it exists to catch.** With six spellings declared and one exercised,
removing or misspelling any of the other five leaves every assertion green —
the coverage failure this repository keeps rediscovering, inside the control
written to prevent it. So the assertion is parameterised over the vocabulary, a
probe carries one member per spelling, and the two are paired by size. The
cases are generated from the list rather than written beside it, because a case
list is a second copy of the vocabulary: a spelling added to the list *and* to
the probe, but not to the cases, satisfies the size check exactly and generates
no case. Generating them is the only shape with nothing to forget, the argument
§12.5's publish barrier wins over per-test discipline, arriving one suite along.

> **A control is code, and the reason it exists applies to it.** A control that
> covers less than it claims is green by construction, so the suite cannot
> catch it. What settles it is the counterfactual — add a spelling, add its
> probe member, and count the cases — and no assertion in the file
> distinguishes a count that moved from one that did not.

**Deciding what it judges is the other half, and both obvious answers are
wrong in opposite directions.** §9.1 says commands do not implement
`IIntegrationEvent` and nothing about the converse. *Every non-event* therefore
also selects the line types events carry — and an event is *permitted* a
subject, so judging them refuses a shape ADR-028 allows. *Non-events minus what
events carry* fixes that and opens a worse hole: a payload carried by a command
**and** an event goes exempt because an event reaches it, letting a subject
travel on the command unjudged.

The set is built **up from the command roots** instead — the commands plus
everything they carry — so a shared payload is judged and a purely-event one is
not.

**The closure is a function of a type universe rather than a fixed field, and
that is a testability decision rather than a stylistic one.** The real
contracts have no payload shared between a command and an event, so the
false negative above cannot be reproduced with them: every assertion over the
live set stays green under the rejected implementation. Four synthetic
contracts in the test assembly supply the case, driven through the same
algorithm — which is only possible because it takes its universe as an
argument. **An algorithm that can only be run against production data can only
be tested with the cases production happens to contain.**

This is the one suite that references every service, which is why it has its own
project and why that project holds contract shape and one other kind of check:
two services' timings held to each other where the blueprint couples them,
as [§3.2](03-bounded-contexts.md)'s wait for a missing order is held to
[§9.6](09-messaging.md)'s payment timeout, because §4.2 lets neither service
read the other's assembly. The contract shape is
`tests/Platform.IntegrationTests/ContractTests.cs`, and everything in it starts
from discovery:

```csharp
private static readonly Type[] Contracts =
[
    .. typeof(OrderPlaced).Assembly.GetTypes().Where(IsContract)
];

/// <summary>A concrete, visible type under <c>Common.Contracts</c>, its root included (§9.2).</summary>
internal static bool IsContract(Type type) =>
    type.IsVisible &&
    type is { IsInterface: false, IsAbstract: false } &&
    type.Namespace is string ns &&
    (ns == "Common.Contracts" || ns.StartsWith("Common.Contracts.", StringComparison.Ordinal));
```

Concrete types only: the assembly also holds `IIntegrationEvent` (§9.1) and the
static code vocabularies (`CancelReasons`, `CancelOrigins`, `ReviewReasons`),
and a filter of everything public under `Common.Contracts` would demand a
versioned namespace of an interface that is deliberately shared across all of
them, and then ask `ContractSamples` for an instance of it. **The root
namespace is included**, because `StartsWith("Common.Contracts.")` alone reads
as "everything in the assembly" and is not: a concrete type declared straight
into `Common.Contracts`, with no version namespace at all, would fall outside
discovery and bypass the versioned-namespace check, the sample check and the
round-trip, leaving the suite green over the one mistake §9.2 exists to reject.
**`IsVisible`, not `IsPublic`**, for a hole of the same kind: `IsPublic` is
false for every nested type, including one declared `public` inside a public
class, and a contract nested in a public type is as reachable by a consumer as
any other. `IsVisible` asks the question actually meant: can something outside
this assembly name it. Each hole has a positive control —
`Discovery_sees_a_contract_that_forgot_its_version_namespace` and
`Discovery_sees_a_contract_nested_inside_a_public_type` — asking `IsContract`
about a probe the test assembly declares.

Over that set, `No_contract_names_a_domain_type` checks §9.1's rule at the
assembly level, because a contract cannot reference a domain type without the
reference, and the reference silently drags `Ordering.Domain` into every
consuming service. `Every_contract_lives_in_a_versioned_namespace` holds
§9.2's `Common.Contracts.<Service>.V<n>`, since a contract that lands one
namespace short is a v1 that can never be superseded. And the round-trip
catches the member type `System.Text.Json` cannot handle — the failure that
otherwise appears as a message in the error queue, in staging, with a
deserialisation stack trace and no obvious owner:

```csharp
foreach (Type type in Contracts)
{
    object instance = ContractSamples.Create(type);
    string json = JsonSerializer.Serialize(instance, type);
    object? returned = JsonSerializer.Deserialize(json, type);

    JsonSerializer.Serialize(returned, type).ShouldBe(json, type.FullName);
}
```

> **The comparison is between two serialised forms, not `ShouldBeEquivalentTo`
> on the objects.** The object graph carries a detail the
> contract does not specify: a collection expression assigned to an
> `IReadOnlyList<T>` member compiles to a synthesised read-only list, and
> `System.Text.Json` returns a `List<T>` — so an equivalence check fails on
> `OrderPlaced` for a difference that is nowhere in the wire format. Making the
> samples construct a `List<T>` instead would fix the symptom by coupling every
> sample to the serialiser's current choice of collection type. The wire form
> *is* the contract, so comparing it is both the cheaper fix and the one that
> says what this suite is for.

That comparison has one blind spot, and it takes a second assertion rather than
a cleverer first one: a member that fails to serialise **at all** is absent from
both forms, so the contract silently loses a field and the round-trip passes. So
a companion test asks the type for its public instance properties and requires
every one of them to appear in the JSON — which is also what fails when a member
is added to a record and not to its sample.

`ContractSamples.Create` is the reason this suite stays honest as contracts
grow. Every member of a V1 contract is `required` unless §12.6's
additive-member list names it, so there is no
reflection shortcut that constructs one — a new contract without a sample fails
here rather than being quietly skipped, which is the failure mode of every
"iterate over all the types" test that defaults to `Activator.CreateInstance`.

Two assertions guard the registry itself, in both directions. A contract with no
sample fails **by name**, in its own test, rather than as one message from the
middle of a round-trip loop; and a sample naming a type that is no longer a
public contract fails too, which is the direction throwing cannot catch — that
entry compiles until the type is deleted and is dead weight from the moment the
contract was renamed.

**The third rule this suite claims — `required` members — needs an assertion of
its own, because no serialisation test can see it.** Dropping `required` from a
contract property changes no JSON, so the round-trip and the wire-member check
both stay green; what it changes is a producer's ability to omit the member, and
every consumer's reading a default when one does. The rule is really *there is
no way to build one incompletely*, and two shapes satisfy it: a positional
record takes its values in a primary constructor and needs no `required` at all,
while a property-based record can be built by `new()` and needs every property
marked. So the assertion applies to the shape with the hole — a contract with a
public parameterless constructor must mark every settable property.

> **Every sample gives every member a distinct, non-default value.** A sample of
> zeroes and empty strings round-trips perfectly through a serialiser that
> dropped the member entirely, which turns the assertion it feeds into one that
> cannot fail. The same rule makes `OccurredAt` a fixed instant with a non-zero
> offset: `DateTimeOffset.MinValue` survives every serialiser bug there is.

> **Trap — the rule and [§9.2](09-messaging.md)'s additive change cannot both
> be obeyed.** §9.2 says a new *optional* field is additive and needs no
> version bump. The rule above says no contract may be constructible
> incompletely. What settles which side gives way is a measurement rather than
> an argument: `System.Text.Json` refuses a payload missing a `required` member
> — *JSON deserialization for type … was missing required properties* — so a
> member shipped `required` faults every message the previous build staged and
> has not yet published. On a rolling deploy that is not an edge case, it is
> the ordinary state for the length of the deploy.
>
> **So the safe shape is the one the rule forbids, and the gate admits it by
> name rather than everywhere.** A member added to a live contract is listed
> in the suite beside the assertion that reads it, and the list is subtracted
> from the *failures* rather than from the candidates — there is no narrowed
> selection to pass vacuously. What makes that safe rather than a hole is a
> second test whose subject is the list: it fails if the entry names nothing,
> and again if the member has somehow become always-supplied. **A list of
> deliberate gaps is only honest while something re-checks that they are still
> gaps** — the same shape §13.6's unloaded alerts and their gate are in.

> **The entry clears when the contract version does, never when the member
> becomes `required`.** That tightening is a breaking change and not a
> tidy-up: a payload predating the field has no bound on how long it can arrive
> — `docs/runbooks/error-queue.md` keeps a message until somebody handles it,
> outliving even its outbox row's purge, and a replay can reintroduce one at
> any time — so requiring the member would fail deserialisation on every
> retained one, before any consumer branch could read the absent value.
> [§9.2](09-messaging.md) sends a breaking change to a new version, so an
> additive member stays optional for the life of the version it was added to,
> and the listing goes when that version does. It is a permanent tolerance with
> a stated reason, not an exemption with a tightening owed: a promised
> tightening reads as rigour and is a scheduled breakage.

### The recorded shape

None of the rules above holds a member to **its earlier type**, which is what
[§9.2](09-messaging.md)'s breaking-change rule is about: a member retyped leaves
them all green once its sample is edited to match. So the shape is recorded.
`contract-shapes.json`, beside the suite, holds every public member of every
contract — its type, its nullability and whether it is always supplied — and
`ContractShapeTests` compares the assembly with it. The contracts are found by
the discovery above, so a new service's are held from its first one, with no
list to extend.

A change breaks when a consumer or a payload built against the record could fail
on it: a contract or a member gone, a rename, which reads as one gone, any
change to a recorded member's declaration, `required` in either direction
included, and a new member that is `required`, which a payload staged before it
does not carry. A new optional member or a new contract is additive, and still
fails the test that compares the record with the assembly until the record is
replaced from the live shape that test writes to its output directory, so an
added member is held from the commit that adds it. The comparer is driven
through a synthetic shape for each kind of change and the renderer through a
probe of each nullability, for the reason the subject rule ships with controls.

**A replacement that removes or changes a recorded line lands in two cases, and
each is argued in its commit**: a contract changed in place under §9.2's
no-consumer exception, beside the ADR that exception requires, and a version
retired at the end of its deprecation window.

**Three things it does not hold.** A change of meaning stays a review's, as §9.2
says. The static vocabularies beside the records — `CancelReasons` and its kin —
are static classes, which the discovery does not reach, so a changed code passes
it. And the BFF's HTTP contract is not a `Common.Contracts` type, so it is out
of this suite's reach by construction.

### Consumer-driven contracts

Everything above is about the *shape* of a contract, and none of it reaches
[§9.7](09-messaging.md)'s hop. `pricing.proto` already pins that shape — one
file, two generated halves, so a field cannot be renamed on one side only. What
it cannot pin is what the fields **mean**: that an unpriced product is absent
rather than zero, that the amount is text to be parsed rather than compared,
that a reply's currency is the amount's own label. `pricing.proto` says all
three, in comments, and a comment holds nothing across the boundary.

**A stub is a second specification nobody verifies.** `Web.Bff.Tests` drives
its endpoint against `StubCatalog`, a hand-written gRPC server modelling
Catalog, and a stub drifts from the service it models in exactly the details a
consumer leans on: filtering currency case-sensitively where Catalog does not,
echoing the request's spelling of the currency rather than its own stored one,
formatting amounts at the test's scale rather than the column's
`decimal(19,4)`, enforcing no request ceiling at all.

> **A stub that echoes the request leaves the consumer's guard against that
> drift unguarded.** `CheckoutEndpoints` compares a reply's currency to the
> request's with `OrdinalIgnoreCase`, precisely because Catalog answers `GBP`
> to a request for `gbp`. A stub echoing the request never hands that
> comparison two spellings, so tightening it to `Ordinal` leaves every
> stub-driven test green over a change that would answer 500 to every
> lower-case currency a customer typed.

So the consumer writes down what it needs, and the provider is verified against
it. The expectations are **one file** — `PricingContract.cs`, in the consumer's
own test-support project — compiled into `Web.Bff.Tests` and **linked** into
`Catalog.Api.Tests`, exactly as `pricing.proto` is linked into `Web.Bff`. The
syntactic contract and the semantic one are then shared the same way, and
neither is an assembly, so [§4.3](04-solution-structure.md) keeps
`Common.Contracts` as its one exception.

```csharp
new PricingInteraction(
    "a product priced in another currency is absent rather than zero",
    [Chair, Lamp],
    ["chair", "lamp"],
    0,
    "GBP",
    PricingOutcome.Prices("chair"))
```

An interaction names its products by **alias** rather than by id, because
neither side can be handed one: the stub mints its ids and the provider suite
publishes through `POST /v1/catalog/products` and is given them back. Each side
realises the state its own way and binds the alias.

**What the contract builds once is the *question*, not the message**, and the
difference is deliberate. The ids and the currency come from
`PricingContract.RequestedIds` on both sides, so the stub and the real service
are asked about the same products in the same currency. Only the provider suite
then sends that as a `GetPricesRequest`; the consumer suite hands it to the
screen as a basket and lets `CheckoutEndpoints` build the gRPC message
itself. Having the contract build it there would verify the contract against
itself — the consumer's half exists precisely to establish that the request the
*endpoint* constructs is the one the contract describes.

The per-field tolerance is stated once and applied to every answered
interaction, on both sides. It is the **consumer's** tolerance and not the
provider's behaviour, which is what keeps a contract from over-specifying: the
amount is parsed and compared numerically, never matched as text, because
Catalog's column scale reaches the wire as `49.9900` and a migration that
changed it would change nothing a customer can see.

> **A consumer-driven contract carries only what the consumer needs, and the
> discipline is what makes it worth verifying.** Catalog also refuses a
> malformed product id, a non-canonical GUID and an anonymous caller. None is an
> interaction: the BFF always sends canonical ids and always presents a token,
> so an expectation about any of them would be a promise nobody is relying on.
> Those stay `PricingServiceTests`' — provider-owned behaviour, tested where it
> is decided. A contract that lists everything the provider happens to do is a
> second provider suite wearing the consumer's name.

The one number the contract does state is the request ceiling, and it is stated
in **both** directions: a basket at `MaxProductIds` must be served, and one past
it must be refused with `InvalidArgument` — the status
`UpstreamExceptionHandler` turns into the caller's 400. Either interaction
alone is satisfiable by a provider that has quietly moved the limit, so the pair
is what pins it, and a change in either direction fails verification on purpose.
`CheckoutEndpoints` holds no copy of *that* number: production code with one
would refuse requests Catalog would have served, where a contract with one is
the consumer saying which number it is relying on.

> **The consumer cannot drive the refusal half, and the interaction stays
> anyway** ([ADR-045](adr/ADR-045-the-checkout-quote-takes-quantities.md)). The
> quote bounds its own line count at `OrderLimits.MaxLines` — the bound the
> *order* insists on, not a copy of Catalog's — so a basket past the ceiling
> fails validation before the hop and Catalog is never asked. The consumer
> suite asserts that instead, and the `InvalidArgument` mapping keeps its
> coverage in `QuoteEndpointTests`, which stubs the status rather than
> provoking it with a size.
>
> **An expectation the consumer cannot drive is still an expectation it
> needs**: the provider verification holds Catalog to the refusal either way,
> and the two bounds agree only by coincidence of value. The day one of them
> moves, this is the interaction that says which — deleted as unreachable, the
> first symptom would be a quote refusing baskets Catalog would have priced, or
> accepting ones it would not.

**It is not Pact, and the mechanism was a decision rather than a
convenience** — [ADR-023](adr/ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
records it. Pact's .NET binding cannot express gRPC at all, which is the only
relationship here that needed one; what Pact is *for* — one artefact the
consumer authors and the provider verifies — is taken, and the broker and wire
format that ship it across a repository boundary are declined, because this is a
monorepo and that boundary is not there.

**Two things it does not do.** It does not cover the address read
[ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)
gives Shipping's worker, the second relationship
[ADR-023](adr/ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
left to the same conditional judgement, and the judgement is that the read
earns no consumer-authored file. What made the pricing hop contentious was
behaviour the consumer depends on and a hand-written stub reproduced wrongly —
a currency's spelling compared with the request's, an amount's scale, a
basket's ceiling. The worker depends on less: it compares nothing in the reply
with anything it sent, checks each field's shape itself as §9.7's sixth rule
asks, and tells only `NotFound` and a refusal (`Unauthenticated`,
`PermissionDenied`) apart from everything else — and Ordering's API suite pins
each of those statuses against the real provider. `StubOrdering` is still a
hand-written stub, so a worker that comes to depend on more of the reply is
where the contract becomes owed. And it does not cross a repository boundary —
extract the BFF and this file becomes something that has to be published, at
which point Pact is the answer after all.

## 12.7 Test doubles

| Dependency | Approach |
|---|---|
| Domain objects | None. Use real ones. |
| Own database | Real, via Testcontainers |
| Own Redis | Real, via Testcontainers |
| Own broker | Real container, or the MassTransit test harness |
| Another service (HTTP) | WireMock.Net — a real HTTP server with stubbed responses |
| Third-party API | WireMock.Net, plus a nightly contract test against their sandbox |
| Third-party relay (SMTP) | Mailpit — a real SMTP server in a container, read back through its HTTP API |
| Clock | `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` |
| Random / GUIDs | Inject a seam; never call `Guid.NewGuid()` where the value is asserted |

**Mock only what you do not own.** Mocking your own repository tests your
understanding of the mock rather than the behaviour of the system, and it is the
single most common cause of a green suite over a broken application.

## 12.8 Conventions

- **Naming:** `Method_expected_behaviour_when_condition`. Readable in the
  test-runner output without opening the file.
- **One logical assertion per test.** Several `Should` calls verifying one
  outcome is fine; testing two unrelated behaviours is not.
- **No conditionals in tests.** An `if` in a test means it is two tests.
- **No shared mutable state between tests.** Every test constructs what it needs.
- **Deterministic.** No `DateTime.Now`, no unseeded random, no reliance on
  execution order, no `Thread.Sleep` — wait on a condition instead.
- **Every consumer and projection gets a redelivery test.** Not a convention —
  a test, because the guarantee it protects is invisible in the code that
  breaks it. The inbox commits separately from a Dapper projection's write
  (§9.5), so a crash between them redelivers, and idempotency is what makes
  that harmless. The test is one line longer than the happy path: handle the
  same event twice, assert the row and every counter read the same as after
  one. A new handler that skips it fails the first time production restarts
  at the wrong moment, which is to say months later, on data nobody can
  reconstruct.

## 12.9 What not to test

Auto-properties. Framework behaviour. Third-party libraries. EF Core's mapping
in a unit test — it is covered by any integration test that reads and writes.
Private methods — test them through the public API that uses them, or extract
them into something with its own public API.

**Coverage** is a diagnostic, not a target. Below roughly 60% you are certainly
missing things worth testing; above roughly 85% you are usually testing getters
to move a number. Watch the *trend* and watch coverage of the domain layer
specifically — that is where it should be near-total, and where it is cheapest
to achieve.

That last sentence is an instruction until something measures it, so
`coverage.runsettings` does:

```bash
dotnet test Platform.slnx --filter "FullyQualifiedName!~ArchitectureTests&Category!=Integration" \
    --collect:"Code Coverage" --settings coverage.runsettings \
    --results-directory ./TestResults/unit
dotnet test Platform.slnx --filter "Category=Integration" \
    --collect:"Code Coverage" --settings coverage.runsettings \
    --results-directory ./TestResults/integration
py -3.12 .github/coverage/domain_coverage.py ./TestResults/unit ./TestResults/integration
```

**Both flags after the settings file are load-bearing.** Without
`--results-directory` the collector writes under each test project's own
`TestResults/` rather than the repo root, so the reporter finds nothing; the
filter is what keeps §4.2's gates out of the instrumented run, for the reason
the callout below gives. Two invocations rather than one, because the figure
is a union across §15.1's stages — the paragraph on the union below says why
— and `docs/testing.md` carries the same three lines beside every other
runner.

The file filters the report to `.*\.Domain\.dll$` and emits Cobertura, and CI
prints the figure to the job summary. **Reported, never gated** — a diagnostic
wired to a build failure stops being read and starts being satisfied.

What is gated instead is whether each stage *ran* — a fact, where a coverage
percentage is a target — and that is
[Appendix C](appendix-c-delivery-plan.md)'s "quality gates". A change that
argues for a threshold argues against this paragraph.

**The figure is a union across stages, and that is forced rather than chosen.**
§15.1 runs the unit and integration halves as separate `dotnet test`
invocations, so there is no one run to read — and the second point below asks
for the domain assemblies "over the whole run". Measured on this repository:
the unit stage covers 253 of 308 method lines and the integration stage 192,
and the union is **257**. Four of those lines are reached only by a test that
needs a container, so a figure taken from either half alone under-reports the
thing it is named after.

Three things about that filter are deliberate:

- **It is a pattern, not a list.** `Catalog.Domain`, `Ordering.Domain` and
  `Common.Domain` match it today, and every later service's Domain matches it
  the day it exists. A list would have to be edited by whoever adds a service,
  which is exactly the edit that gets missed.
- **It measures the domain assemblies over the whole run**, not the domain test
  projects. Domain types are exercised by application and API tests too, and a
  figure taken from `*.Domain.Tests` alone would under-report the thing it is
  named after.
- **The collector arrives with `Microsoft.NET.Test.Sdk`**, so no package was
  added and [Appendix B](appendix-b-licences.md) gained no entry. A coverage
  figure is not worth a new dependency.

> **Measuring a layer changes it, and §4.2's gates are what noticed.** The
> filter above instruments the Domain assemblies and nothing else — which are
> exactly the assemblies the Domain gates read `GetReferencedAssemblies` on. On
> the Linux runner an instrumented Domain assembly reports a `netstandard`
> reference no source line can produce, and both gates go red on a CI run that
> collects coverage. It does not reproduce on Windows, where the same
> collector leaves the file byte-identical.
>
> So CI runs the gates **first and uninstrumented**, and collects coverage over
> the complement. Admitting `netstandard` to the allow-list was the one-line
> alternative and is the wrong one: an architecture rule relaxed everywhere and
> for ever, in every service the scaffold renders, to accommodate a test tool.
> **If a change needs one of those gates relaxed, the gate is probably right.**

`docs/testing.md` carries the commands, the categories and what needs Docker —
the operational half of this chapter, kept separate because a runner flag goes
stale on a different clock than a strategy does. Where the two disagree, this
chapter wins.

---

[← §11 Identity](11-identity-authorization.md) · [Index](README.md) · [§13 Observability →](13-observability.md)

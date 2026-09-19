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
| Infrastructure building block | One §9 or §8 mechanism — a consumer wrapper, the outbox's table and type map, the idempotency store, the lock, the retention policy | Recording fakes for most; a real Redis (container) for the classes in its `Integration` collection, where the mechanism under test is Redis's | < 10 ms, and < 500 ms against Redis | One suite | `Common.Infrastructure.Tests` |
| Edge configuration | The route file of §10.2 against the host that loaded it, and §10.1's edge behaviours — compression and the body ceiling | `WebApplicationFactory` + a stub destination on loopback — no containers, and `UseKestrel` where the property under test is the server's own | < 1 s | One suite | `Gateway.Api.Tests` |
| Outbound hop | §9.7's one synchronous call: the timeout hierarchy read off the built host, the credential handler's position inside the resilience pipeline, and §11.5's realm | `WebApplicationFactory` + a real gRPC server on loopback; one class also runs a real Keycloak | < 1 s, and seconds for the Keycloak class | One suite | `Web.Bff.Tests` |
| Pipeline behaviour | One §6.3 behaviour against recording fakes — the branches its handler-level tests cannot reach | None | < 10 ms | One suite per behaviour | `Common.Application.Tests` |
| Saga | One whole saga, coordination only | MassTransit in-memory harness — no infrastructure | < 100 ms per positive assertion (§12.5) | A few | `*.Application.Tests` |
| Contract shape | Every published contract against the rules it must obey | Both assemblies, reflection only | < 1 s | One suite | `Platform.IntegrationTests` |

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
does the message one service publishes still mean what its consumers expect —
is a reflection test over the contract assembly, and it is why
`Platform.IntegrationTests` exists; what else that suite holds is §12.6's.
Contract compatibility is **not the only thing genuinely between services** —
§9.7's one synchronous hop is another, and §12.6 tests the two in almost
opposite ways. The hop is
deliberately not in this suite: a contract over it needs the provider
running, so it lives in the provider's own suite rather than buying a sixth
project a container set.

What no level above covers is whether the *deployed* system responds under load
and against real infrastructure. That is the **k6 run against staging**
([§13.7](13-observability.md)) — `deploy/observability/slo/slo.js` — asserting
the SLO rows it can evaluate; not a test suite, and [§15.1](15-cicd-deployment.md) stages it as
what it is. **One tool, named**: this said "k6 or NBomber" until §13.7 picked
one, on the grounds that a stage naming two tools names none.

Naming it accurately is the point: a load run that is honestly a load run gets
maintained; an "E2E suite" that is actually three fragile scripts gets disabled
after the second flake and stays green forever.

Every row above names a project **and has an example in this section**. Both
halves are the rule: a level with no home is a level nobody writes, and a level
whose home is empty is one nobody notices is missing.

**How to run them is `docs/testing.md`**, deliberately not this chapter: the
commands, the `Category=Integration` filter of §12.4, which five projects need
a Docker daemon and what the coverage figure of §12.9 is measured over. This
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
to describe than to follow.

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
            .OfType<OrderPlacedDomainEvent>().ShouldHaveSingleItem();
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
            currency, DefaultNow);

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

```csharp
public sealed class ServiceFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _sql = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // Two Redis containers, matching the production split in §8.1 — otherwise
    // the tests cannot catch a coordination key written to the evicting instance.
    private readonly RedisContainer _cache = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "allkeys-lru")
        .Build();

    private readonly RedisContainer _coordination = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .WithCommand("--maxmemory-policy", "noeviction")
        .Build();

    // 4.1 rather than a floating 4, because that is the base tag §14.1's
    // broker Dockerfile builds from and this fixture's whole claim is that a
    // test and a developer machine cannot disagree about the engine.
    //
    // A service that SCHEDULES cannot use a tag at all. Since ADR-021 §14.1
    // builds the broker rather than pulling it, and the delayed exchange lives
    // in a plugin no official image carries — so Ordering's fixture builds the
    // same Dockerfile through ImageFromDockerfileBuilder and runs the result.
    // The failure that forces it is a quiet one: a stock broker connects and
    // reports healthy, because the exchange is not declared until something
    // schedules, and the schedule that does then HANGS on a declare the broker
    // refuses. ADR-021 has the measurement.
    private readonly RabbitMqContainer _rabbit = new RabbitMqBuilder()
        .WithImage("rabbitmq:4.1-management-alpine")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    private Respawner _respawner = null!;

    // ValueTask, not Task: xUnit v3 redefined IAsyncLifetime (see below).
    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_sql.StartAsync(), _cache.StartAsync(), _coordination.StartAsync(), _rabbit.StartAsync());

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("ConnectionStrings:Ordering", _sql.GetConnectionString())
                .UseSetting("ConnectionStrings:RedisCache", _cache.GetConnectionString())
                .UseSetting("ConnectionStrings:RedisCoordination", _coordination.GetConnectionString())
                .UseSetting("ConnectionStrings:RabbitMq", _rabbit.GetConnectionString())
                // AddJwtAuthentication reads this key eagerly and throws
                // naming it (§11.3), so the fixture supplies it for the same
                // reason it supplies the connection strings: without it the
                // host does not start, InitializeAsync throws, and every test
                // in the suite fails before it runs. Deliberately fake and
                // deliberately unreachable — .invalid never resolves, so a
                // test that accidentally dials the authority fails loudly
                // rather than reaching a real identity provider.
                //
                // Not ValidateOnStart and not OptionsValidationException,
                // which is what this comment said until PR-16 wrote the code:
                // §15.4 keeps ServiceIdentityOptions as the solution's only
                // options type, so there is no options class here to validate.
                //
                // No Identity:Client here. Ordering does not call a peer, so it
                // never binds ServiceIdentityOptions (§9.7) and supplying one
                // would be config the host ignores — which is how a fixture
                // ends up disagreeing with the deployment about what a service
                // requires, in the direction that hides a missing secret.
                .UseSetting("Identity:Authority", "https://identity.invalid/realms/test")
                .ConfigureServices(services =>
                {
                    // Replace the JWT scheme rather than configuring it: the
                    // endpoints under test are behind RequireAuthorization
                    // (§11.4), and the alternative is either 401 on every call
                    // or a fixture that fetches OIDC metadata over the network.
                    // TestAuthHandler issues the principal each test asks for,
                    // including its permission claims, so the authorization
                    // policies are exercised for real.
                    // Only authenticate and challenge are set, and forbid
                    // follows the challenge one — DefaultForbidScheme is unset,
                    // and the scheme provider falls back to
                    // DefaultChallengeScheme before DefaultScheme. So the 403
                    // comes from TestAuthHandler's inherited forbid, which is a
                    // bare status code touching no metadata, and the
                    // wrong-permission test needs no identity provider either.
                    services.Configure<AuthenticationOptions>(o =>
                    {
                        o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    });
                    services
                        .AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                            TestAuthHandler.SchemeName,
                            _ => { });

                    // Remove ONLY the two background services this suite
                    // drives, not every hosted service: MassTransit registers
                    // its bus as one, so RemoveAll<IHostedService>() would stop
                    // the broker from starting and silently disable every
                    // consumption test.
                    //
                    // The dispatcher polls every 500 ms; left running it drains
                    // outbox rows underneath assertions about them. Tests that
                    // want it call fixture.ProcessOutboxBatchAsync() explicitly.
                    //
                    // Both matches are on ImplementationType, which is why
                    // §4.2 registers each with the generic AddHostedService<T>
                    // rather than a factory: a factory registration leaves that
                    // property null and these removals would match nothing.
                    foreach (Type background in (Type[])[typeof(OutboxDispatcher), typeof(RetentionPurgeService)])
                    {
                        ServiceDescriptor hosted = services.Single(
                            d => d.ServiceType == typeof(IHostedService) &&
                                d.ImplementationType == background);
                        services.Remove(hosted);
                    }

                    // Still resolvable directly, so tests can drive one pass of
                    // each. The purge's timer is an hour rather than 500 ms, so
                    // it would not race an assertion in a run this short — but a
                    // test asserting that an abandoned row SURVIVES retention
                    // cannot tell "the pass spared the row" from "the pass never
                    // ran" unless it drives the pass itself.
                    services.AddSingleton<OutboxDispatcher>();
                    services.AddSingleton<RetentionPurgeService>();

                    // ICurrentUser (§11.4) has two callers with incompatible
                    // needs, so the double DELEGATES rather than replacing.
                    // Over HTTP the principal must keep coming from
                    // TestAuthHandler through HttpContext, exactly as
                    // production resolves it — a flat replacement would make
                    // Hides_another_customers_order_behind_a_404 pass because
                    // the default subject happens to differ from the owner,
                    // which is passing for the wrong reason, and would break
                    // every HTTP path that needs the header principal.
                    services.RemoveAll<ICurrentUser>();
                    services.AddScoped<TestCurrentUser>();
                    services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<TestCurrentUser>());
                }));

        // Tests deliberately collapse the two database identities of §7.1 —
        // the container's sa login holds both DML and DDL. Production keeps
        // them separate, and migrations run as a job, never from a host (ADR-007).
        using IServiceScope scope = Factory.Services.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<OrderingDbContext>()
            .Database.MigrateAsync();

        // Reset between tests by truncating, which is far faster than
        // recreating the schema or wrapping every test in a rolled-back
        // transaction (which would hide transaction-related bugs).
        _respawner = await Respawner.CreateAsync(
            _sql.GetConnectionString(),
            new RespawnerOptions { SchemasToInclude = ["ordering"] });
    }

    public Task ResetAsync() => _respawner.ResetAsync(_sql.GetConnectionString());

    /// <summary>Runs exactly one claim-and-deliver pass. No timers, no waiting.</summary>
    public Task<int> ProcessOutboxBatchAsync(CancellationToken ct = default) =>
        Factory.Services.GetRequiredService<OutboxDispatcher>().ProcessBatchAsync(ct);

    /// <summary>
    /// The host's own map and payload format (§9.4), with this assembly's
    /// events and the service's converters in them — so a row a test stages is
    /// a row the running dispatcher can read back.
    /// </summary>
    public MessageTypeMap MessageTypes => Factory.Services.GetRequiredService<MessageTypeMap>();

    public OutboxJson OutboxJson => Factory.Services.GetRequiredService<OutboxJson>();

    /// <summary>
    /// Dispatches below HTTP with a stated principal (§11.4's subject rule).
    /// The scope is what makes that safe: TestCurrentUser is scoped, so a
    /// principal set here cannot leak into another test or into a concurrent
    /// request.
    /// </summary>
    public async Task<TResult> DispatchAsync<TResult>(
        ICommand<TResult> command,
        ICurrentUser currentUser,
        CancellationToken ct = default)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestCurrentUser>().Set(currentUser);

        return await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, ct);
    }

    /// <summary>
    /// The query half, which the read-side subject test needs. Written out
    /// rather than described as "the same with IQuery": §6.2 gives IDispatcher
    /// two methods, so swapping only the constraint leaves SendAsync refusing
    /// an IQuery. The body differs too, and that is the whole of the difference.
    /// </summary>
    public async Task<TResult> DispatchAsync<TResult>(
        IQuery<TResult> query,
        ICurrentUser currentUser,
        CancellationToken ct = default)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestCurrentUser>().Set(currentUser);

        return await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .QueryAsync(query, ct);
    }

    /// <summary>
    /// Runs a statement outside any unit of work, for arranging. Placeholders
    /// are {0}-style and EF turns each into a real SQL parameter — a formatted
    /// string here would be both an injection shape and a CA1305.
    /// </summary>
    public async Task ExecuteAsync(string sql, params object[] parameters)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        await db.Database.ExecuteSqlRawAsync(sql, parameters, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Persists a real aggregate through the DbContext, so the row satisfies
    /// every invariant §5 enforces. A raw INSERT drifts from the aggregate the
    /// first time it gains a column, and drifts silently.
    /// </summary>
    public async Task<Guid> SeedOrderAsync(Guid customerId)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        Order order = OrderBuilder.Placed(customer: new CustomerId(customerId));
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id.Value;
    }

    public async Task<IReadOnlyList<OutboxMessage>> OutboxAsync()
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<OrderingDbContext>()
            .OutboxMessages.AsNoTracking().ToListAsync();
    }

    public async Task StageOutboxAsync(params OutboxMessage[] rows)
    {
        using IServiceScope scope = Factory.Services.CreateScope();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        db.OutboxMessages.AddRange(rows);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a prior attempt count through the same column the dispatcher
    /// writes. Explicit rather than hidden in a builder, so no state carries
    /// between tests (§12.8).
    /// </summary>
    public Task SetOutboxAttemptsAsync(Guid messageId, int attempts) =>
        ExecuteAsync(
            "UPDATE ordering.OutboxMessages SET Attempts = {0} WHERE MessageId = {1};",
            attempts,
            messageId);

    /// <summary>
    /// Clears retry backoff leases so the next pass is gated only by the
    /// attempt cap. Lets a test distinguish "backed off" from "abandoned"
    /// without sleeping.
    /// </summary>
    public Task ExpireOutboxLeasesAsync() =>
        ExecuteAsync("UPDATE ordering.OutboxMessages SET LockedUntil = NULL WHERE ProcessedAt IS NULL;");

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Task.WhenAll(
            _sql.DisposeAsync().AsTask(),
            _cache.DisposeAsync().AsTask(),
            _coordination.DisposeAsync().AsTask(),
            _rabbit.DisposeAsync().AsTask());
    }
}
```

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

The test scheme itself. Tests state who they are in headers, so authorization
runs against a real principal rather than being switched off:

```csharp
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    // SchemeName, not Scheme. AuthenticationHandler<T> already declares a
    // protected Scheme — the AuthenticationScheme this handler was resolved
    // for — so a constant of that name hides it, and CS0108 is an error under
    // ADR-019's TreatWarningsAsErrors. This sample said Scheme until PR-16
    // compiled it.
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string PermissionsHeader = "X-Test-Permissions";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header means anonymous, not "authenticated as nobody" — otherwise
        // every 401 test silently passes.
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues userId))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, userId.ToString())];

        // The same claim type §11.4's policies require. A test that grants
        // itself "orders:cancel" is exercising the policy, not bypassing it.
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
}
```

> **Do not give the test principal every permission.** A fixture that hands out
> a blanket claim set makes the [§11.4](11-identity-authorization.md) policies untestable and, worse, makes them
> *look* tested — the endpoints are reached, the assertions pass, and the one
> behaviour nobody ever exercises is the refusal. Grant per test exactly what
> that test's user should have, and keep at least one test per policy that
> grants nothing and expects the rejection.

```csharp
[Collection(nameof(IntegrationCollection))]
public class PlaceOrderHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        // Respawn truncates the price projection, and the write path reads
        // prices locally (§6.4). Seed here rather than per test: an unseeded
        // projection fails a PlaceOrder with ProductsUnavailable, which reads
        // as a domain assertion failing rather than missing fixture data.
        await SeedPriceAsync(SeedData.ProductId, 12.50m, "EUR");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Placing_an_order_persists_it_and_writes_an_outbox_message()
    {
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IDispatcher>();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();

        // No CustomerId on the command — the handler reads it from
        // ICurrentUser (§11.4). This scope has no HttpContext, so the
        // fixture's TestCurrentUser answers as Principals.Default: a test
        // that does not care about the subject says nothing about it. The
        // subject tests below state one per dispatch instead.
        Result<Guid> result = await dispatcher.SendAsync(
            new PlaceOrderCommand(
                CommandId: Guid.CreateVersion7(),
                Items: [ new PlaceOrderItem(SeedData.ProductId, 2) ],
                ShippingAddress: AddressBuilder.ValidDto(),
                Currency: "EUR"));

        result.IsSuccess.ShouldBeTrue();

        Order order =
            await db.Orders.SingleAsync(o => o.Id == new OrderId(result.Value));
        order.Status.ShouldBe(OrderStatus.AwaitingStock);
        order.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);

        // The outbox rows are the real assertion: they prove the reactions are
        // staged atomically with the state change, and that nothing has run yet.
        // Only meaningful because the fixture removed the dispatcher — otherwise
        // this races a background service that drains these rows twice a second.
        List<OutboxMessage> outbox = await db.OutboxMessages.ToListAsync();
        outbox.ShouldAllBe(m => m.ProcessedAt == null);

        // Broker lane carries the CONTRACT type (§9.3 allow-list)...
        outbox.ShouldContain(m => m.Lane == OutboxLane.Broker &&
            m.MessageType.Contains(nameof(V1.OrderPlaced)));

        // ...and the Local lane carries the DOMAIN type (§7.5). Distinct names
        // are what make this an assertion about which type is on which lane,
        // rather than merely that both lanes got a row.
        outbox.ShouldContain(m => m.Lane == OutboxLane.Local &&
            m.MessageType.Contains(nameof(OrderPlacedDomainEvent)));

        // The domain type must never reach the broker — that is the leak §9.3
        // exists to prevent, and it is only checkable because the names differ.
        outbox.ShouldNotContain(m => m.Lane == OutboxLane.Broker &&
            m.MessageType.Contains(nameof(OrderPlacedDomainEvent)));
    }

    [Fact]
    public async Task The_same_command_id_is_processed_once()
    {
        var commandId = Guid.CreateVersion7();
        PlaceOrderCommand command =
            CommandBuilder.PlaceOrder() with { CommandId = commandId };

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        IDispatcher dispatcher =
            scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<Guid> first = await dispatcher.SendAsync(command);
        Result<Guid> second = await dispatcher.SendAsync(command);

        // Served ENTIRELY by the replay branch (§8.5) — the handler runs once,
        // so this value is the stored one rebuilt through Result.Success<T>
        // rather than a second handler's return. Both assertions are needed
        // because they detect different things, not because either covers a
        // case the other lets through: the row count catches a SECOND
        // PERSISTED ORDER, and the equality catches a replay that returned
        // something other than what was stored.
        second.Value.ShouldBe(first.Value);

        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_command_id_claimed_by_one_customer_does_not_replay_to_another()
    {
        PlaceOrderCommand command =
            CommandBuilder.PlaceOrder() with { CommandId = Guid.CreateVersion7() };

        // The same command, the same CommandId, two principals. Nothing about
        // the request distinguishes them, which is the point: CommandId is
        // client-generated, so this is the collision a second caller can
        // arrange (§8.5).
        Result<Guid> mine = await fixture.DispatchAsync(
            command,
            Principals.Authenticated(SeedData.CustomerId));
        Result<Guid> theirs = await fixture.DispatchAsync(
            command,
            Principals.Authenticated(Guid.CreateVersion7()));

        // Not "both succeeded" — that is true of an unscoped key too, because
        // the second caller is handed the FIRST caller's order id and reads it
        // as a success. The assertion has to be that they are different orders.
        theirs.Value.ShouldNotBe(mine.Value);

        // And the third dispatch is what makes the second one mean anything.
        // Two different ids prove only that SOMETHING varies per caller — a key
        // scoped by a correlation id, or no IdempotencyBehavior registered at
        // all, produces them just as well. Replaying the FIRST subject's own
        // claim is what says the varying segment is the subject.
        Result<Guid> mineAgain = await fixture.DispatchAsync(
            command,
            Principals.Authenticated(SeedData.CustomerId));

        mineAgain.Value.ShouldBe(mine.Value);

        // CreateScope, not the root provider: OrderingDbContext is scoped, and
        // DispatchAsync opened and closed its own scopes above. Resolving it
        // from Factory.Services directly throws under ValidateScopes and, where
        // that is off, hands back a context living as long as the host.
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        OrderingDbContext db =
            scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        (await db.Orders.CountAsync()).ShouldBe(2);
    }

    /// <summary>
    /// Seeds the price projection (§6.6). Required before any PlaceOrder test:
    /// the handler reads prices locally, so an unseeded projection makes every
    /// order fail ProductsUnavailable rather than erroring visibly. Private to
    /// the suite rather than on the fixture: it is one INSERT over
    /// ExecuteAsync, and the fixture carries what more than one suite needs.
    /// </summary>
    private Task SeedPriceAsync(
        Guid product, decimal amount, string currency, bool available = true) =>
        fixture.ExecuteAsync(
            """
            INSERT INTO ordering.ProductPrices (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES ({0}, {1}, {2}, {3}, SYSDATETIMEOFFSET());
            """,
            product,
            currency,
            amount,
            available);
}
```

`A_command_id_claimed_by_one_customer_does_not_replay_to_another` is the one
test here whose failure is a **disclosure** rather than a defect: an unscoped
key does not throw, log or answer slowly — it answers 200 with another
customer's order id, to a caller authentication and the endpoint policy have
already admitted. Those two still run on a replay; what does not is the handler,
and with it §11.4's binding of the subject from the principal. That is why the
assertion is `ShouldNotBe` against the first caller's value and not
`IsSuccess.ShouldBeTrue()`.

**Three dispatches rather than two, and the third is the one that carries the
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
[Collection(nameof(IntegrationCollection))]
public class OutboxDispatcherTests(ServiceFixture fixture) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => new(fixture.ResetAsync());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_failing_row_does_not_block_healthy_rows()
    {
        await fixture.StageOutboxAsync(
            OutboxRows.Poison(fixture),          // its handler always throws
            OutboxRows.Healthy(fixture),
            OutboxRows.Healthy(fixture));

        await fixture.ProcessOutboxBatchAsync();

        IReadOnlyList<OutboxMessage> rows = await fixture.OutboxAsync();

        rows.Count(r => r.ProcessedAt is not null).ShouldBe(2);

        OutboxMessage poison = rows.Single(r => r.ProcessedAt is null);
        poison.Attempts.ShouldBe(1);
        poison.LastError.ShouldNotBeNullOrEmpty();
        poison.LockedUntil.ShouldNotBeNull();     // backed off, not abandoned
    }

    [Fact]
    public async Task A_row_stops_being_claimed_at_the_attempt_cap()
    {
        OutboxMessage poison = OutboxRows.Poison(fixture);
        await fixture.StageOutboxAsync(poison);
        await fixture.SetOutboxAttemptsAsync(poison.MessageId, 9);

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);   // 9 → 10

        // Clear the backoff lease, so the second pass is blocked by the
        // attempt cap and nothing else. Without this the test would pass
        // even if the cap were removed entirely.
        await fixture.ExpireOutboxLeasesAsync();

        (await fixture.ProcessOutboxBatchAsync()).ShouldBe(0);

        OutboxMessage row = (await fixture.OutboxAsync()).Single();
        row.Attempts.ShouldBe(10);                // not 11 — never re-claimed
        row.ProcessedAt.ShouldBeNull();           // visible to the §13.6 alert
    }

    [Fact]
    public async Task A_local_row_with_no_registered_handler_fails_loudly()
    {
        await fixture.StageOutboxAsync(OutboxRows.Unhandled(fixture));

        await fixture.ProcessOutboxBatchAsync();

        OutboxMessage row = (await fixture.OutboxAsync()).Single();
        row.ProcessedAt.ShouldBeNull();           // NOT silently completed
        row.LastError.ShouldContain("IProjectionHandler");
    }
}
```

The third test is the one worth keeping forever. It asserts the failure mode
that would otherwise be invisible: a projection that never runs while every
dashboard stays green.

Contract messages come from a builder rather than inline object initialisers.
`required` members make partial construction a compile error, so every test
would otherwise repeat eight assignments to vary one:

```csharp
internal static class Contracts
{
    // A fixed instant, as the outbox builders below use: OccurredAt is what
    // §13.7's delivery lag is measured from, and a builder reaching for the
    // system clock would have every test assert against a lag it just made.
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    public static V1.OrderPlaced OrderPlaced(Guid orderId, decimal total = 25.00m, string currency = "EUR") => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = orderId,
        OccurredAt = Raised,
        OrderId = orderId,
        CustomerId = Guid.CreateVersion7(),
        TotalAmount = total,
        Currency = currency,
        Lines = [new V1.PlacedLine(SeedData.ProductId, 1, total)]
    };
}
```

The builders those tests use are ordinary factories over `OutboxMessage`, one
class rather than one per case — they differ only in which event they stage,
and three classes with a `Row` method each said that three times:

```csharp
// The map and the payload format are the real ones, resolved from the
// fixture's provider (§9.4). A double for either would let a test stage a row
// the running host cannot read back, which is the one thing these builders
// exist to prove does not happen.
public static class OutboxRows
{
    // A fixed instant, not the system clock: OccurredAt is what §13.7's
    // projection lag is measured from, and a test that staged "now" would
    // assert against a lag it had just created.
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    public static OutboxMessage Poison(ServiceFixture fixture) =>
        Local(new AlwaysThrows { OccurredAt = Raised }, fixture);

    public static OutboxMessage Healthy(ServiceFixture fixture) =>
        Local(new NoOpEvent { OccurredAt = Raised }, fixture);

    public static OutboxMessage Unhandled(ServiceFixture fixture) =>
        Local(new UnhandledEvent { OccurredAt = Raised }, fixture);

    // The fixture rather than the map alone, because a staged row needs both
    // halves of the host's agreement about the format: the persisted name and
    // the converters. A row written without the Money converter round-trips
    // to a zero amount and a null currency.
    private static OutboxMessage Local(object message, ServiceFixture fixture) =>
        OutboxMessage.Stage(
            message,
            OutboxLane.Local,
            Guid.CreateVersion7(),
            fixture.MessageTypes,
            fixture.OutboxJson);
}
```

`AlwaysThrows` has a registered `IProjectionHandler<AlwaysThrows>` that throws;
`NoOpEvent` has one that does nothing; `UnhandledEvent` has none, which is
precisely what the third test exercises. All three are `IDomainEvent`
implementations in the test assembly, so the fixture registers that assembly
before the map is built — one line beside the `TestAuthHandler` replacement:

```csharp
// §9.4. Adding, not replacing: the production assemblies stay, so a test
// cannot stage a type the real host would refuse. Without this, NameOf throws
// on the first builder call and every outbox test fails before its assertion.
//
// The registered instance is mutated, not re-registered. Constructing a second
// source here would compile, pass, and quietly restate the production list —
// so the day §4.2 gains an assembly, this line is a copy that no longer
// matches and nothing points at it. MessageTypeSource is mutable for exactly
// this, and the map is built from it on first resolve.
services
    .Single(d => d.ServiceType == typeof(MessageTypeSource))
    .ImplementationInstance
    .ShouldBeSource()
    .Add(typeof(AlwaysThrows).Assembly);
```

Two assertions belong beside these. The first is the cheapest guard on the
single-identity rule of [§9.1](09-messaging.md), and it takes no fixture at all — the thing that
can regress is a pure function:

```csharp
// Common.Infrastructure.Tests, no [Collection] and no fixture: Stage touches
// nothing. It is not a §12.3 test either — that level is *.Domain.Tests, and
// OutboxMessage is Common.Infrastructure, which is where §12.1's table homes
// the outbox's table and type map. A fast test does not have to be a domain
// test, and moving it to reach a container it does not use is how a suite
// acquires a minute of startup for one assertion.
//
// The envelope is this assembly's own SampleIntegrationEvent rather than a
// service contract, because Common.Infrastructure.Tests references
// Common.Infrastructure and nothing downstream of it — which is the same
// fact that makes the test cheap, read from the other side.
private static readonly DateTimeOffset Now = new(2026, 8, 11, 2, 26, 0, TimeSpan.Zero);
private static readonly MessageTypeMap Types = new([typeof(SampleDomainEvent).Assembly]);
private static readonly OutboxJson Json = new([]);

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

    // Both from the envelope, not minted here — and CorrelationId in
    // particular, because a caller-supplied one is passed in and ignored for
    // an IIntegrationEvent. That argument being silently dropped is the
    // regression this test exists for.
    row.MessageId.ShouldBe(message.MessageId);
    row.CorrelationId.ShouldBe(message.CorrelationId);
}
```

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

The second is the `Local` lane's payload contract (§9.4). It needs no
containers, but it lives here rather than in §12.6 because the set it iterates
comes from the fixture's `MessageTypeMap` and §12.6 selects on the contracts
namespace, which no domain event is in:

```csharp
[Fact]
public void Every_stageable_domain_event_round_trips_through_the_outbox_options()
{
    // Not "every IDomainEvent": the map is the set the outbox can actually
    // carry, and a type it does not know cannot reach a payload column.
    // The REGISTERED options, converters included. A hand-built OutboxJson
    // listing the service's converters would assert that they work — which
    // nobody doubts — and stay green if a registration were deleted, while the
    // running host wrote a zero-valued Money into every row. Registration is
    // what can silently go missing, so registration is what this resolves.
    JsonSerializerOptions options = fixture.OutboxJson.Options;

    foreach (Type type in fixture.MessageTypes.StageableDomainEvents)
    {
        object sample = DomainEventSamples.Create(type);
        string json = JsonSerializer.Serialize(sample, type, options);

        JsonSerializer
            .Deserialize(json, type, options)
            .ShouldBeEquivalentTo(sample, $"{type.Name} cannot survive the Local lane");
    }
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
> section's.** Its handler tests moved to `Ordering.Api.Tests`, because
> `ICurrentUser` is `HttpContextCurrentUser` and a handler resolved in a bare
> scope has no principal to bind a subject from — so
> `Ordering.Application.Tests` references no `TestSupport`, declares no
> collection, and holds §12.5's saga suite instead, which needs no
> infrastructure at all. The library is still right for the reason above; it
> simply has one consumer there rather than two. `docs/testing.md` says the
> same thing from the other side, and this paragraph exists because that file
> would otherwise contradict this one.
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
> counts them, with no third state and nothing counted twice. Those figures
> read ten/81, then twenty/91, then twenty-three/94, then twenty-four/95, then
> twenty-six/97, then twenty-six/98, then twenty-seven/99, and every retake up
> to the last three was the suite growing while the callout did not. **The
> count of retakes is not written here either**, for the reason the figures
> keep demonstrating: it has been wrong at each of them. What a reader can
> check is whether this pair matches `docs/testing.md` and a run.
>
> **One retake was the first to go *down*, and the direction is worth
> naming.** ADR-033 withdrew the token-denylist claim, so
> `RedisKeys.Denylist` and the case pinning its shape went with it and the
> fast half was seventy where it had been seventy-one. It went back up on the
> next branch: [ADR-037](adr/ADR-037-the-idempotency-marker-is-a-row-in-the-commands-own-transaction.md)'s
> floor on `RetentionPolicy.IdempotencyWindow` brought cases of its own; the
> branch that retired that floor's allowance
> ([ADR-038](adr/ADR-038-the-marker-and-its-claim-are-ordered-by-construction-not-a-margin.md))
> then added one integration case and rewrote the floor's own rather than
> adding to them, so only the integration half moved. The branch after it
> ([ADR-039](adr/ADR-039-the-markers-purge-asks-the-claim-rather-than-out-counting-it.md))
> moved the same half again and by more — seven cases for the port's fifth
> member, every one of them needing the server it asks — and left the fast half
> at seventy-two for the third retake running. The pair above is what a
> run reports now. A figure that has only ever
> grown teaches the next reader to check whether it is *behind*; one that
> moves both ways has to be re-measured instead.
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
> **CI runs the halves separately since PR-25**, as §15.1's `UT → IT`, with
> §4.2's architecture gates ahead of both for the instrumentation reason
> `docs/testing.md` gives. The category shipped a release ahead of the stage
> that depends on it, which is what let the stage be written against a filter
> already known to select what it claims.
>
> **Three stages are three new ways to select nothing**, and that is what
> PR-25's quality gate is for: `dotnet test` exits **zero** on a filter that
> matches no test, so each stage can be green and empty. The gate asserts a
> floor on each stage's count, that every test project in `Platform.slnx` ran
> in one of them, and that none ran in two — the last of which is what makes
> "exhaustive and disjoint" a check rather than a claim.
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
because the design it replaced looked correct and delivered its payload only
by accident — in the ordinary flow it delivered nothing, which is the failure
the grid below locates precisely
([ADR-027](adr/ADR-027-the-order-summary-stores-product-ids-and-resolves-the-name-locally.md)).

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
shipped one (empty strings, patched by a later `ProductPublished`), the
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
| **The wide page** | Seed enough orders to put more than 2,100 distinct ids on one page: twenty-two orders of a hundred items reaches 2,200, which is well inside §6.5's clamp of a hundred orders. The page returns. Without this, an implementation that expands the ids into an `IN` list passes every other case, because they all use a handful of products, and fails in production on SQL Server's parameter ceiling |

**The second is the one that would otherwise be documentation rather than a
rule.** ADR-027 argues the parameter limit at length and the sample uses
`OPENJSON` because of it; nothing above would notice the `IN` list coming back.
A limit stated in prose and enforced by no case is the shape this repository
already names — a claim that reads as settled and is not.

So the ordinary flow is what catches the design that shipped, the late arrival
is what catches the cheaper repair that would otherwise look equivalent, the
replay is what catches an update path neither of the others enters, and the
last two catch reader branches that no case about the *design* reaches. A
suite carrying only one of them leaves a whole design indistinguishable from
this one — which is §12.4's rule about negatives applied to a design rather
than to an assertion: a case that two candidates both satisfy is not evidence
about which is present.

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

```csharp
[Collection(nameof(IntegrationCollection))]
public class CancelOrderEndpointTests(ServiceFixture fixture) : IAsyncLifetime
{
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _client = fixture.Factory.CreateClient();
        return new(fixture.ResetAsync());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Rejects_a_request_with_no_token()
    {
        // No X-Test-User header, so TestAuthHandler returns NoResult and the
        // challenge stands. What this catches is the POLICY being dropped from
        // the endpoint — not UseAuthentication being dropped from the pipeline,
        // which is what this comment claimed until PR-16 tried it. See the
        // callout below the four tests.
        HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"/v1/orders/{Guid.CreateVersion7()}/cancel",
            new CancelOrderRequest(CancelReasons.CustomerRequest));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rejects_a_caller_holding_the_wrong_permission()
    {
        // Authenticated, but with orders:read where the endpoint wants
        // orders:cancel — the case a fixture that grants everything hides.
        // A literal, and there is no constant to reach for anyway: the
        // vocabulary holds what endpoints require, and nothing reads. The
        // point is a permission
        // this endpoint's policy does not accept, and naming it from the
        // vocabulary would read as though one existed for it.
        HttpResponseMessage response = await SendAsAsync(Guid.CreateVersion7(), "orders:read", Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Hides_another_customers_order_behind_a_404()
    {
        var owner = Guid.CreateVersion7();
        Guid orderId = await fixture.SeedOrderAsync(customerId: owner);

        HttpResponseMessage response = await SendAsAsync(
            Guid.CreateVersion7(),   // a different customer
            OrderingPermissions.Cancel,
            orderId);

        // 404, not 403 — §11.4. A 403 here would confirm the order exists,
        // which is the whole point of the check, and it is invisible to a
        // handler test that asserts on Result.Failure alone.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rejects_a_reason_outside_the_wire_vocabulary()
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/orders/{Guid.CreateVersion7()}/cancel")
        {
            // The enum's member name, not the wire code — accepted by
            // Enum.TryParse and rejected here, which is the difference.
            Content = JsonContent.Create(new CancelOrderRequest("CustomerRequest"))
        };
        request.Headers.Add(TestAuthHandler.UserHeader, Guid.CreateVersion7().ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, OrderingPermissions.Cancel);

        HttpResponseMessage response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<HttpResponseMessage> SendAsAsync(Guid userId, string permissions, Guid orderId)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"/v1/orders/{orderId}/cancel")
        {
            Content = JsonContent.Create(new CancelOrderRequest(CancelReasons.CustomerRequest))
        };
        request.Headers.Add(TestAuthHandler.UserHeader, userId.ToString());
        request.Headers.Add(TestAuthHandler.PermissionsHeader, permissions);

        return _client.SendAsync(request);
    }
}
```

Four tests, four distinct failures, none reachable from below the HTTP
boundary: an endpoint that lost its `RequireAuthorization` (401 becomes 200), a
policy name that resolves to nothing (403 becomes 200), a resource check
returning the wrong status (404 becomes 403, leaking existence), and a reason
parsed by `Enum.TryParse` instead of the wire vocabulary (400 becomes 200, and
the enum's member names quietly become API surface). Each is a defect this
document has argued about in prose and, until now, asserted nowhere.

> **None of them catches a missing `UseAuthentication`, and nothing in a
> `WebApplication` host can.** The first row read that way until PR-16 deleted
> the line from `Catalog.Api/Program.cs` and every test in the repository
> stayed green. `WebApplication` adds the authentication and authorization
> middleware **itself** whenever the matching services are registered; an
> explicit call moves them earlier in the pipeline, which is what a composition
> root wants — they have to sit above anything that logs the caller — but it is
> not what puts them there.
>
> Keep the explicit calls: they are §4.2's specified shape, they are required
> by any host that is not a `WebApplication`, and a pipeline whose order is
> implicit is one nobody can review. What changed is the claim about what would
> notice their absence. `Common.Web.Tests` carries the four assertions that
> are actually true — that the middleware is what populates `HttpContext.User`,
> that the authorization middleware does not authenticate on its own, that a
> `WebApplication` auto-adds it, and that auto-insertion does **not** repair
> the two calls being written in the wrong order. The third is a regression
> guard on the framework: were a release to stop doing it, every service would
> hand anonymous callers to its handlers while its authorization kept passing.
> The fourth is what [§4.2](04-solution-structure.md)'s ordering row rests on,
> and it is the reason that row no longer says reversal is harmless.

### The subject rule, enforced

[§11.4](11-identity-authorization.md)'s subject rule is the kind of rule that
holds by omission — a command with no `CustomerId` field cannot be pointed at
another customer — and a rule that holds by omission is one a later refactor
reinstates without noticing. These five are what make it fail loudly instead.

```csharp
using static Ordering.TestSupport.Principals;   // Authenticated, Anonymous

[Collection(nameof(IntegrationCollection))]
public class SubjectBindingTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        // Same reason the PlaceOrder suite seeds here: the write path reads
        // prices locally (§6.4), and an unseeded projection fails every
        // PlaceOrder with ProductsUnavailable — which would read as the
        // subject assertion failing rather than as missing fixture data.
        // Arranged inline rather than through a shared helper: it is one
        // INSERT, and a second suite needing it is not yet a fixture member.
        await fixture.ExecuteAsync(
            """
            INSERT INTO ordering.ProductPrices (ProductId, Currency, Amount, IsAvailable, UpdatedAt)
            VALUES ({0}, {1}, {2}, 1, SYSDATETIMEOFFSET());
            """,
            SeedData.ProductId,
            "EUR",
            12.50m);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task An_order_is_attributed_to_the_caller()
    {
        var caller = Guid.CreateVersion7();

        Result<Guid> result = await fixture.DispatchAsync(
            CommandBuilder.PlaceOrder(),
            currentUser: Authenticated(caller));

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        Order order = await db.Orders.SingleAsync(o => o.Id == new OrderId(result.Value));

        // The row's owner came from the principal. Before the subject rule
        // this assertion passed for the wrong reason — the command carried a
        // CustomerId and the handler copied it — so what makes it meaningful
        // now is the compile error a reinstated field would cause in
        // CommandBuilder.
        order.CustomerId.ShouldBe(new CustomerId(caller));
    }

    [Fact]
    public async Task A_customer_reads_only_their_own_orders()
    {
        var owner = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();

        // Seeded through the write path, not SeedOrderAsync. That helper
        // persists an Order and its lines through EF and nothing else, which
        // the level-1 query reads — but §6.6 rewrites this slice IN PLACE to
        // read ordering.OrderSummaries, and an EF-seeded aggregate never
        // reaches that table. A test seeded that way would pass today and
        // start failing on the PR that escalates the read side, with the
        // stranger's empty page still passing for the wrong reason.
        // Dispatching and draining the outbox fills whatever the live handler
        // reads: the aggregate at level 1, the projection at level 2.
        await fixture.DispatchAsync(
            CommandBuilder.PlaceOrder(),
            currentUser: Authenticated(owner));
        await fixture.ProcessOutboxBatchAsync();

        CursorPage<OrderSummaryDto> seen = await fixture.DispatchAsync(
            new GetOrderSummariesQuery(Cursor: null, Limit: 20),
            currentUser: Authenticated(stranger));

        // Empty, and provably not empty by accident: the same query as the
        // owner returns the seeded row, so the filter is discriminating rather
        // than broken. One assertion without the other passes against a
        // handler that returns nothing to anybody.
        seen.Items.ShouldBeEmpty();

        CursorPage<OrderSummaryDto> own = await fixture.DispatchAsync(
            new GetOrderSummariesQuery(Cursor: null, Limit: 20),
            currentUser: Authenticated(owner));

        own.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_owner_cancels_their_own_order()
    {
        // The positive user-origin case, and the suite is unsound without it:
        // every other CommandOrigin.User assertion here is a refusal, so a
        // handler that rejected the user path outright would pass everything
        // below while disabling customer cancellation completely. The status
        // assertion is the half that matters — IsSuccess alone is satisfied by
        // a handler that returns Success and writes nothing.
        var owner = Guid.CreateVersion7();
        Guid orderId = await fixture.SeedOrderAsync(customerId: owner);

        Result result = await fixture.DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.User),
            currentUser: Authenticated(owner));

        result.IsSuccess.ShouldBeTrue();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        Order order = await db.Orders.SingleAsync(o => o.Id == new OrderId(orderId));

        order.Status.ShouldBe(OrderStatus.Cancelled);
    }

    [Fact]
    public async Task A_user_command_with_no_caller_is_refused()
    {
        // The one case HTTP cannot produce: §11.4's endpoint group carries
        // RequireAuthorization, so an unauthenticated request never reaches
        // the handler and a 401 would prove nothing about the check inside it.
        // This is the fail-open the old IsAuthenticated guard admitted — a
        // command on the user path with no principal behind it.
        var owner = Guid.CreateVersion7();
        Guid orderId = await fixture.SeedOrderAsync(customerId: owner);

        Result result = await fixture.DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.User),
            currentUser: Anonymous);

        result.Error.ShouldBe(OrderErrors.NotFound);
    }

    [Fact]
    public async Task A_system_initiated_command_cancels_without_a_caller()
    {
        // The control, and the reason the origin exists at all. Without it
        // the test above passes against a handler that refuses every
        // compensation, which would break §9.6's saga in a way no ordering
        // test would catch.
        var owner = Guid.CreateVersion7();
        Guid orderId = await fixture.SeedOrderAsync(customerId: owner);

        Result result = await fixture.DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.OutOfStock, CommandOrigin.System),
            currentUser: Anonymous);

        result.IsSuccess.ShouldBeTrue();

        // The status, for the same reason the owner case asserts it: a handler
        // that short-circuits system commands with Result.Success() and touches
        // no aggregate satisfies IsSuccess while leaving every compensation
        // ineffective — which is the failure this pair exists to catch, wearing
        // a success code.
        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        OrderingDbContext db = scope.ServiceProvider.GetRequiredService<OrderingDbContext>();
        Order order = await db.Orders.SingleAsync(o => o.Id == new OrderId(orderId));

        order.Status.ShouldBe(OrderStatus.Cancelled);
    }
}
```

`DispatchAsync` is the fixture helper above: it opens a scope, points that
scope's `TestCurrentUser` at the principal named, and dispatches. The double
itself is the part worth reading, because a simpler one breaks the HTTP suite:

```csharp
/// <summary>
/// The scoped ICurrentUser every test runs on, in three states.
/// </summary>
public sealed class TestCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    // Unset inside a request: the real seam — TestAuthHandler through
    // HttpContext through HttpContextCurrentUser, resolved exactly as
    // production resolves it, so §12.4's endpoint tests keep asserting on
    // the principal their headers named.
    //
    // Unset below HTTP: an authenticated stand-in, so a handler test that
    // says nothing about the caller still has one. Without this branch every
    // pre-existing dispatcher test would throw on currentUser.Id the moment
    // §6.4's handler started reading it.
    //
    // Set: whatever the test said. The only route to a handler with no
    // caller at all, which is the state RequireAuthorization stops a request
    // from ever producing (§11.4).
    private ICurrentUser? _stated;

    public void Set(ICurrentUser principal) => _stated = principal;

    private ICurrentUser Effective =>
        _stated ?? (accessor.HttpContext is not null
            ? new HttpContextCurrentUser(accessor)
            : Principals.Default);

    public bool IsAuthenticated => Effective.IsAuthenticated;

    public Guid Id => Effective.Id;

    public bool HasPermission(string permission) => Effective.HasPermission(permission);
}
```

`Principals` supplies the values that double answers from — two a test names
explicitly, and the one it falls back to when a test names none:

```csharp
/// <summary>
/// The principals a test can state, and the one it gets by stating none.
/// </summary>
public static class Principals
{
    // Fixed rather than a fresh subject per access: two dispatches in one test
    // that both say nothing about the caller have to agree about who it was, or
    // a read-after-write assertion fails for a reason the test never mentions.
    public static ICurrentUser Default { get; } = Authenticated(SeedData.CustomerId);

    // IsAuthenticated false and an Id that throws — the shape
    // HttpContextCurrentUser takes off the consumer path (§11.4), so a handler
    // reading Id without guarding fails here the way it fails in production.
    public static ICurrentUser Anonymous { get; } = new Principal(null, []);

    // params, so the subject tests name a caller and nothing else; the
    // permissions are what §11.4's orders:admin branch reads.
    public static ICurrentUser Authenticated(Guid subject, params string[] permissions) =>
        new Principal(subject, permissions);

    private sealed class Principal(Guid? subject, string[] permissions) : ICurrentUser
    {
        public bool IsAuthenticated => subject is not null;

        public Guid Id => subject ?? throw new InvalidOperationException(
            "No authenticated caller. Guard with IsAuthenticated.");

        public bool HasPermission(string permission) => permissions.Contains(permission);
    }
}
```

**`Anonymous` is a property and `Authenticated` a method**, which is what makes
`currentUser: Anonymous` and `currentUser: Authenticated(caller)` read as they
do above. The subject is nullable in one place only — inside `Principal`,
where the absence *is* the state being modelled.

**Delegating rather than replacing is the whole design**, and the flat version
is worth naming because it looks simpler and is wrong. A double that always
answers from its own field would make
`Hides_another_customers_order_behind_a_404` pass because the fixture's default
subject happens not to be the seeded owner — the right status for the wrong
reason, on the one test whose entire point is that the status is right — and
would strand every HTTP path that needs the header principal.

**A double is the right call here and §12.7's rule says so**, though it needs
reading twice to see it: `ICurrentUser` is a port over `HttpContext`, so the
thing being stood in for is infrastructure, exactly as `FakeTimeProvider` stands
in for the clock. What "mock only what you do not own" forbids is doubling the
repository underneath these tests, and none of them does — the orders are real
rows in a real database, seeded through the aggregate.

These five run at the dispatcher rather than over HTTP, and that is not a
shortcut. Two of them describe states HTTP cannot produce against §11.4's
endpoint group: `RequireAuthorization` turns a caller-less request into a 401
before any handler runs, so the fail-open the old guard admitted is invisible
from outside, and the compensation path has no HTTP surface at all. The
API-contract tests above cover the boundary; these cover the check.

**The last two tests are a pair and only mean something together.** One asserts
the check refuses a caller-less user command; the other asserts it still lets
the saga through. Either alone is satisfied by a handler that is simply wrong in
the other direction, and the direction that fails silently — refusing
compensations — surfaces as orders stuck in `AwaitingStock` long after the
deployment that caused it.

**Three of the five carry an origin, and each states it rather than earning
it.** The system case constructs `CommandOrigin.System` directly, which is the
right way to test the *check* — and it leaves the only production code that
assigns it, `CancelOrderMapper` (§9.4), unasserted. A mapper stamping `User`
would pass every test above and reject every real compensation — the failure
the pair was written to catch, arriving by the one route the pair cannot see.
Two short tests close it — the stamp, and the parse that stands in front of it:

```csharp
[Fact]
public void The_mapper_is_what_makes_a_message_system_initiated()
{
    CancelOrderCommand command = new CancelOrderMapper().Map(
        new CancelOrder(Guid.CreateVersion7(), CancelReasons.OutOfStock));

    // Both halves of what the mapper does. The stamp is the new one, but the
    // parse is the older claim and equally unasserted: every recognised code
    // could map to the wrong domain reason and this test would still pass on
    // the origin alone.
    command.InitiatedBy.ShouldBe(CommandOrigin.System);
    command.Reason.ShouldBe(CancellationReason.OutOfStock);
}

[Fact]
public void An_unknown_reason_code_never_becomes_a_command()
{
    // §9.4's retry policy ignores ContractMappingException, so this is what
    // sends a malformed message to the error queue on the first attempt
    // rather than after a minute of backoff. A parse that quietly accepted
    // the code would keep the test above green and lose that behaviour, and
    // nothing else in the suite looks at this branch.
    CancelOrder message = new(Guid.CreateVersion7(), "invented_last_release");

    Should.Throw<ContractMappingException>(() => new CancelOrderMapper().Map(message));
}
```

No `[Collection]` and no fixture: the mapper is a pure function, so this sits
beside `Stage_takes_both_identities_from_the_envelope` above — a different
project, the same absence of infrastructure — rather than inside the
container-backed class. It is
the second half of a boundary whose first half is the endpoint's literal, and
that half is already covered: the API-contract tests reach the handler through
HTTP, so they fail if `User` stops being stamped.

### Gateway configuration tests

The gateway's suite sits here rather than beside `Common.Web.Tests`, and the
distinction is the entry point: `Common.Web` is a library with none, so its
tests build a pipeline by hand, while the gateway is a host and the thing under
test is the configuration *that host* loaded. It is the one suite in this
section that starts no container — the edge owns no database
([§10.1](10-api-gateway.md)) — and the one that reads a shipped configuration
file as a subject rather than as setup.

Two of its assertions have no other home. The first is that the host accepted
every route in the file: policy names are resolved when
[§10.2](10-api-gateway.md)'s configuration loads, and a route whose id went in
and did not come out is a path that stopped existing.

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

> **A listener, not an address that refuses.** The first version pointed the
> clusters at `127.0.0.1:1`, on the reasoning that a refused connection is free.
> Measured, it cost about two seconds a request — so exhausting §10.3's
> hundred-request window took three and a half minutes, the window replenished
> before the last request arrived, and the rate-limit test failed while the
> limiter was working perfectly. A stub that answers is faster *and* is the
> only thing that can observe the forwarded path.

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

The pyramid's outbound-hop row is `Web.Bff.Tests`, and it exists because
§9.7's one synchronous call has three properties no other suite can reach: a
timeout hierarchy, a credential handler's *position*, and a realm that nothing
compiles against.

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

The realm is the third, and it is the one suite in the solution that starts a
real Keycloak (§11.5). Everything else points at an unreachable authority on
purpose, so an audience mapper missing from the realm export is invisible to
every other test here — nothing compiles differently when it is gone.

## 12.5 Testing the saga

Saga logic is where cross-service bugs live, and MassTransit's in-memory test
harness makes it testable without any infrastructure at all.

```csharp
[Fact]
public async Task Payment_declined_releases_stock_before_cancelling()
{
    await using ServiceProvider provider = new ServiceCollection()
        .AddMassTransitTestHarness(x =>
        {
            // 60 and 10, and the ceiling is deliberately six times the bound
            // meant to fire. 30 is MassTransit's OWN default for testTimeout,
            // so a sample setting it there states nothing and inherits the
            // number the trap below is about — this line read 30 while the
            // suite it specifies ran 60.
            x.SetTestTimeouts(testTimeout: TimeSpan.FromSeconds(60), testInactivityTimeout: TimeSpan.FromSeconds(10));
            // The same two lines production registers (ADR-021), and they are
            // not optional here: §9.6's Initially arms StockTimeout, so the
            // first OrderPlaced reaches for a scheduler. The in-memory
            // transport implements the delay itself where RabbitMQ needs a
            // plugin — the transports differ and the registration under test
            // does not.
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

    ITestHarness harness = provider.GetRequiredService<ITestHarness>();
    await harness.Start();

    var orderId = Guid.CreateVersion7();

    // Every member of every V1 contract is `required` unless §12.6's
    // additive-member list names it — the §9.1 envelope included, and none of
    // these — so there is no partial construction to elide anywhere here,
    // and a builder keeps that from filling the test. `new StockReserved
    // { OrderId = orderId }` does not compile: the three envelope members are
    // as required as the payload, which is the point of §9.1 declaring them on
    // an interface rather than leaving them to convention.
    // `Publish` here is the suite's helper, not `harness.Bus.Publish`: it
    // returns only once the saga has consumed the message, so the ordering
    // below is its job and not the caller's. The `Sent` lines are assertions
    // — each names the command a transition owes — and three bare
    // `harness.Bus.Publish` calls in their place would be the race the trap
    // below prices.
    await Publish(harness, Contracts.OrderPlaced(orderId));
    (await harness.Sent.Any<ReserveStock>(m => m.Context.Message.OrderId == orderId))
        .ShouldBeTrue();

    await Publish(harness, Contracts.StockReserved(orderId));
    (await harness.Sent.Any<AuthorisePayment>(m => m.Context.Message.OrderId == orderId))
        .ShouldBeTrue();

    await Publish(harness, Contracts.PaymentDeclined(orderId, "insufficient_funds"));

    // Sent, not Published — the saga issues these as commands to a single
    // owner (§9.6). The harness tracks the two separately, so asserting on
    // Published here would fail while looking like a saga defect.
    (await harness.Sent.Any<ReleaseStock>(m => m.Context.Message.OrderId == orderId))
        .ShouldBeTrue();

    // CancelOrder must not be sent until stock is confirmed released — and
    // "not yet" needs a point in time to be false *at*. The `Publish` above
    // IS that point: it returned only once the saga had consumed
    // PaymentDeclined, which is why no `Consumed` assertion stands here. It
    // used to, and the barrier moving into the helper is what retired it.
    //
    // An already-cancelled token then reads the record as of that point: no
    // wait, no deadline for a late saga to hide inside, and the harness's one
    // shared inactivity token left unspent for the assertion after
    // StockReleased. The traps below explain why each of those matters.
    using CancellationTokenSource asRecorded = new();
    asRecorded.Cancel();

    (await harness.Sent.Any<CancelOrder>(m => m.Context.Message.OrderId == orderId, asRecorded.Token))
        .ShouldBeFalse();

    await Publish(harness, Contracts.StockReleased(orderId));

    // The reason, not just the send. Both exits from Compensating read
    // ctx.Saga.CancelReason (§9.6), so a transition that forgets to set it on
    // entry produces a CancelOrder carrying null — which this assertion fails
    // on and `Any<CancelOrder>` alone would not.
    (await harness.Sent.Any<CancelOrder>(m =>
        m.Context.Message.OrderId == orderId &&
        m.Context.Message.Reason == CancelReasons.PaymentDeclined))
        .ShouldBeTrue();
}

[Fact]
public async Task Commands_are_sent_and_events_are_published()
{
    // The distinction §9.6 rests on, asserted directly: publishing a command
    // would deliver it to every subscriber, and nothing else in the suite
    // would notice.
    //
    // The shared helper registers the saga and states both harness bounds.
    // The last assertion here is a negative — and the ONE negative in a saga
    // suite that can never match, since the whole claim is that a command is
    // not published. Left on the ordinary token it bills the inactivity
    // timeout on every green run, so it reads the record as of the positive
    // above it instead. See the traps below.
    // The tuple is the helper's shape — it returns the provider as well as the
    // harness, and the caller owns it. A bare
    // `ITestHarness harness = await StartHarnessAsync()` does not compile
    // against it, and dropping the `await using` leaks a running bus into
    // whatever runs next.
    (ServiceProvider provider, ITestHarness harness) = await StartHarnessAsync();
    await using (provider)
    {
        var orderId = Guid.CreateVersion7();

        // Every member of V1.OrderPlaced is `required`, so there is no partial
        // construction to elide — a builder keeps that from filling the test.
        await Publish(harness, Contracts.OrderPlaced(orderId));

        (await harness.Sent.Any<ReserveStock>()).ShouldBeTrue();

        using CancellationTokenSource spent = new();
        spent.Cancel();

        (await harness.Published.Any<ReserveStock>(spent.Token)).ShouldBeFalse();
    }
}
```

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
> the test that flakes, and it flakes on a loaded runner and nowhere else.
> Measured here — the first occurrence was fixed by interleaving waits into
> the single test that had failed, twenty unfenced publishes remained across
> fourteen of the twenty-seven harness tests the suite held then, and the very
> run that
> merged the fix went red on the next one. A helper that publishes and then
> waits for **that message** to be consumed leaves nothing to forget, which is
> the argument the assembly-wide parallelisation attribute won over a shared
> collection. **On its own id, not its type**: a suite that delivers one type
> twice — a redelivery, a duplicate — would otherwise match the first
> delivery and fence nothing, silently. It costs nothing on a green run,
> because the consume it waits for is the one already happening; what it
> spends the inactivity bound on is a message no consumer takes, which is a
> real defect reported where it occurs rather than four assertions later.
>
> ```csharp
> private static async Task Publish<T>(ITestHarness harness, T message)
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
>             messageId = context.MessageId;
>         },
>         TestContext.Current.CancellationToken);
>
>     messageId.ShouldNotBeNull();
>
>     (await harness.Consumed.Any<T>(
>         m => m.Context.MessageId == messageId,
>         TestContext.Current.CancellationToken))
>             .ShouldBeTrue(
>                 $"a published {typeof(T).Name} was never consumed, so this barrier " +
>                 "cannot say the next publish is ordered after it");
> }
> ```
>
> **The failure text names no consumer**, and that is not fastidiousness: the
> helper fences whatever is bound to the message, which in a saga suite is the
> saga and in the barrier's own guard is a consumer the test holds open. A
> message naming the saga sends a reader of a routing failure to a component
> that was never registered.

> **The wait reads the send context because that is the one handle both
> kinds of message carry** — not because a contract has a second identity. It
> has not: §9.1's body, row, header and inbox key are one GUID, and
> `IIntegrationEvent` says the envelope's value is *the* message id "not a
> second one" — **and that `CorrelationId` follows the same rule for the same
> reason**. So the helper writes **both**, as `OutboxDispatcher` does; what it
> reads back for a contract is the envelope's own value. Copying only the
> message id is the half-measure this sample shipped first, which leaves the
> correlation as the second identity the rule is about. A message with no
> envelope — a scheduled timeout, or any bare record — needs no such write,
> which is why the rule is about contracts rather than about publishes. A
> saga's scheduled timeouts are not contracts — `StockReservationExpired`'s
> own doc comment argues why — and have no envelope, which is the case the
> send context covers and the payload cannot.
>
> **Letting MassTransit mint the header instead is the trap `IIntegrationEvent`
> names**, and it was written here before this was noticed: every event gets two
> identities, one the payload carries and one the broker uses, and **nothing
> fails** — the suite stayed green and the chapter confidently said the two ids
> differ. Leave `messageId` unset and the comparison becomes `null == null`,
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
> clothes**, so the barrier gets a second test that does not ask the saga. A
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
> so the run **hangs** instead of going red. Measured that way round, on the
> first counterfactual this test was put through: a ten-minute runner spent and
> nothing named. A hang is a worse outcome than the race it replaces, because
> a red says which assertion and a hang says nothing at all.

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
> that did not schedule. That costume is the danger, and it is not
> hypothetical: the same mechanism failed CI on an in-memory harness test
> asserting a consume, which then passed on a re-run of the same commit with no
> changes. That was a consume smoke rather than a saga, and PR-21's suite
> inherited the same wait — which is why both bounds are stated in its
> `StartHarnessAsync` rather than left to a default. State both, and keep the
> ceiling clear of the bound meant to fire, so which one reported a failure is
> never a detail of how long the publish took.

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
> wait — nothing after it is poisoned — but "may" is not "should", and the
> second sample above is the case that showed why. It used to wait for a publish
> the test's own subject guarantees will never come, so the wait was the full
> inactivity bound, every run, for an answer already known; it now reads the
> record like every other negative here. **Use the cancelled token for every
> negative and the question stops arising.** PR-21's suite reached its second
> review still paying that ten seconds under a comment claiming it never did —
> and removing it took the suite from twelve seconds to two, which is the
> measurement that priced the habit.

> **`Consumed` says a message arrived and never what happened to it.** The
> harness records the delivery whether the pipeline returned or threw, so
> "nothing changed" — no transition, no command sent — is exactly what a
> saga event **faulting onto the error queue** looks like from every assertion
> on this page. That is not a hypothetical: a state machine's default answer
> to an event no state handles is `UnhandledEventException`
> ([§9.6](09-messaging.md)), and the suite that proved a stale timeout
> "changes nothing" was green against both outcomes for as long as it existed.
> A test whose subject is absorption has to read
> `harness.Consumed.Select<T>(spent).Select(m => m.Exception)` and assert every
> element is **null** — and on the cancelled token, for the reason above, or
> the read spends the shared bound and every assertion after it answers
> falsely.
>
> **Null, not empty, and the difference is the whole test.** `Consumed` records
> one entry per delivery whatever the outcome, and `Exception` is null on a
> clean one — so the sequence has an element per absorbed message, and an
> emptiness assertion fails on exactly the result the test exists to prove.
> `ShouldAllBe(e => e == null)` is what the suite runs.
>
> **This paragraph carried a PR attribution for that correction and no longer
> does**, because none was supportable: it credited a PR that is not the saga
> suite's, and the repair a reviewer proposed — the saga suite's own
> number — is not supportable either. The callout is new here, and no
> committed revision of that suite ever asserted emptiness. A claim about when
> a repo's prose was wrong is checkable against its history, so it is worth
> either checking or dropping; the fact above stands without it.

> **A missing scheduler fails this suite in the costume the traps above
> describe, which is why the registration is spelled out rather than trimmed.**
> The sample above carried neither scheduler line until PR-21 compiled it, and
> the two are easy to read as ceremony. They are not: with both deleted, eleven
> of that PR's thirteen saga tests fail, **every one of them as a timeout**,
> each reporting the command the saga did not send. The saga's exception faults
> onto the error queue and no assertion ever sees it. The survivors were the
> structural pair that construct the state machine without starting a bus,
> which is worse than none — they leave a deleted registration looking
> half-covered.
>
> **That measurement is pinned to the suite PR-21 shipped and does not
> re-run to the same numbers**, which is why it names that PR rather than the
> file. The suite has grown since — #126 alone added five tests, one of them a
> fourth bus-free structural one — so a reader reproducing it today gets a
> different ratio from the same deletion. The proportion is not the point and
> the **costume** is: whatever the count, the failures arrive as timeouts
> naming a command, and the structural tests pass throughout.

> **Where the numbers live is the other half.** The first sample states them in
> the registration it shows; the second gets them from `StartHarnessAsync`, the
> shared helper these excerpts call rather than define. Either way they are
> stated once per harness: copy them per test and one test can quietly run on a
> different wait from its neighbour, leave them out and it is the first trap
> rather than a saving. **Neither sample spends the inactivity timeout**, which
> is the whole point of the technique — both read the record on a cancelled
> token instead of waiting. The bounds still have to be stated: they are what a
> *positive* assertion waits under, and a saturated runner is exactly when one
> takes longer than 1.2 seconds to arrive.

## 12.6 Contract tests

The saga tests above prove one service's coordination. **Two things are
genuinely *between* services**, and they are tested in almost opposite ways: the
contract assembly, whose rules are about *shape* and hold for every contract
there will ever be, and [§9.7](09-messaging.md)'s one synchronous hop, whose
rules are about *behaviour* and are one consumer's. The first is below; the
second is the consumer-driven contract at the end of this section.

The contract assembly's rules are all stated elsewhere as things reviewers
should notice: §9.1's "a contract may not name a domain type", §9.2's versioned
namespace, `required` members, and — since
[ADR-028](adr/ADR-028-a-money-movement-command-carries-no-subject.md) —
that a command carries no subject. The first three are mechanical, so each is a
test rather than a review note.

**The fourth is not, and saying so is the honest half.** "Spelled like a
subject" is a list of six substrings, so `OwnerId` passes it — the deny-list
failure this repository has already paid for once, in a check that enumerated
the terminal states it refused instead of the one it accepted. What makes the
rule hold is the allow-list beside it: every member the judged commands may
carry is enumerated, and a name absent from that list fails the build. That
does not classify the new member — it forces somebody to, which is the most a
test can do about a rule whose vocabulary cannot be closed.

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

**No count of them here, deliberately**: review added to that number twice, and
a stated total is the half that goes stale. What the set is for is checkable;
how many there are is not worth a second place to be wrong.

**The spelling vocabulary is the case worth naming, because the control had the
defect it exists to catch — twice, and the second time inside the fix for the
first.** Six spellings were declared and one was exercised, so removing or
misspelling any of the other five left every assertion green — the coverage
failure this repository keeps rediscovering, reproduced inside the control
written to prevent it. The fix parameterised the assertion, added a probe
carrying one member per spelling, and paired the two by size; its comment
claimed that pairing failed in either direction.

**It did not, because the cases were a second copy of the vocabulary.** A
spelling added to the list *and* to the probe, but not to the case list,
satisfied the size check exactly and generated no case — the same entry
unobserved, one layer up from where it was fixed. The cases are now generated
from the list, which is the only shape with nothing to forget: the argument
§12.5's publish barrier wins over per-test discipline, arriving one suite along
and one round later.

> **A control is code, and the reason it exists applies to it.** Both rounds of
> this were caught by review rather than by the suite, and neither could have
> been caught by the suite: a control that covers less than it claims is green
> by construction. What settles it is the counterfactual — add the seventh
> spelling, add its probe member, and count the cases. Six meant the claim was
> false and seven means it holds, and no assertion in the file distinguishes
> those two.

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
read the other's assembly. The contract shape is this:

```csharp
public class ContractTests
{
    // Concrete types only. The assembly also holds IIntegrationEvent (§9.1)
    // and the static code vocabularies (CancelReasons, CancelOrigins,
    // ReviewReasons), and a filter of "everything public under
    // Common.Contracts" would demand a versioned namespace of an interface
    // that is deliberately shared across all of them — and then ask
    // ContractSamples for an instance of it.
    private static readonly Type[] Contracts =
    [
        .. typeof(OrderPlaced).Assembly.GetTypes().Where(IsContract)
    ];

    // The ROOT namespace is included, and a trailing dot is what excluded it.
    // `StartsWith("Common.Contracts.")` reads as "everything in the assembly"
    // and is not: a concrete type declared straight into `Common.Contracts`,
    // with no version namespace at all, falls outside discovery — so it
    // bypasses the versioned-namespace check, the sample check and the
    // round-trip, and leaves the suite green over the one mistake §9.2 exists
    // to reject. Exposed as a method so a positive control can ask it about a
    // type declared, in the *test* assembly, in exactly that namespace.
    //
    // IsVisible, not IsPublic, and that is a second hole of the same kind:
    // IsPublic is false for EVERY nested type, including one declared `public`
    // inside a public class — those report IsNestedPublic. A contract nested in
    // a public type is as reachable by a consumer as any other and fell out of
    // discovery entirely. IsVisible asks the question actually meant: can
    // something outside this assembly name it.
    internal static bool IsContract(Type type) =>
        type.IsVisible &&
        type is { IsInterface: false, IsAbstract: false } &&
        type.Namespace is string ns &&
        (ns == "Common.Contracts" || ns.StartsWith("Common.Contracts.", StringComparison.Ordinal));

    [Fact]
    public void No_contract_names_a_domain_type()
    {
        // §9.1's rule, and the one that silently drags Ordering.Domain into
        // every consuming service. Checked at the assembly level because a
        // contract cannot reference a domain type without the reference.
        typeof(OrderPlaced).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ShouldNotContain(name => name.EndsWith(".Domain", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_contract_lives_in_a_versioned_namespace()
    {
        // Common.Contracts.<Service>.V<n> — §9.2. A contract that lands one
        // namespace short is a v1 that can never be superseded.
        Contracts.ShouldAllBe(t =>
            Regex.IsMatch(t.Namespace!, @"^Common\.Contracts\.[A-Za-z]+\.V\d+$"));
    }

    [Fact]
    public void Every_contract_round_trips_through_the_bus_serialiser()
    {
        // Catches the member type System.Text.Json cannot handle — the failure
        // that otherwise appears as a message in the error queue, in staging,
        // with a deserialisation stack trace and no obvious owner.
        foreach (Type type in Contracts)
        {
            object instance = ContractSamples.Create(type);
            string json = JsonSerializer.Serialize(instance, type);
            object? returned = JsonSerializer.Deserialize(json, type);

            JsonSerializer.Serialize(returned, type).ShouldBe(json, type.FullName);
        }
    }
}
```

> **The comparison is between two serialised forms, and `ShouldBeEquivalentTo`
> on the objects is what it replaced.** The object graph carries a detail the
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
> be obeyed, and the first member to be added found it.** §9.2 says a new
> *optional* field is additive and needs no version bump. The rule above says
> no contract may be constructible incompletely. So the additive path §9.2
> promises had no reachable shape here until something tried to take it, and
> what settled which side gives way was a measurement rather than an argument:
> `System.Text.Json` refuses a payload missing a `required` member —
> *JSON deserialization for type … was missing required properties* — so a
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

> **The entry clears when the contract version does, and an earlier revision
> of this callout said it clears when the member becomes `required`.** That
> tightening is a breaking change and not a tidy-up: a payload predating the
> field has no bound on how long it can arrive — `docs/runbooks/error-queue.md`
> keeps a message until somebody handles it, outliving even its outbox row's
> purge, and a replay can reintroduce one at any time — so requiring the member
> would fail deserialisation on every retained one, before any consumer branch
> could read the absent value. [§9.2](09-messaging.md) sends a breaking change
> to a new version, so an additive member stays optional for the life of the
> version it was added to, and the listing goes when that version does.
>
> **The first framing was "§15.5's expand phase with a contract phase owed",
> and it was wrong in the direction that reads as rigour.** It promised a
> future tightening, which sounds like the exemption being temporary and is
> actually a scheduled breakage — the honest version is a permanent tolerance
> with a stated reason, which is the less tidy claim and the true one.

### Consumer-driven contracts

Everything above is about the *shape* of a contract, and none of it reaches
[§9.7](09-messaging.md)'s hop. `pricing.proto` already pins that shape — one
file, two generated halves, so a field cannot be renamed on one side only. What
it cannot pin is what the fields **mean**: that an unpriced product is absent
rather than zero, that the amount is text to be parsed rather than compared,
that a reply's currency is the amount's own label. `pricing.proto` says all
three, in comments, and until PR-26 nothing checked any of them across the
boundary.

**What stood in for a check was a stub, and a stub is a second specification
nobody verifies.** `Web.Bff.Tests` drives its endpoint against `StubCatalog`, a
hand-written gRPC server modelling Catalog. Four of its behaviours had drifted
from the service it models — it filtered currency case-sensitively where Catalog
does not, echoed the request's spelling of the currency rather than its own
stored one, formatted amounts at the test's scale rather than the column's
`decimal(19,4)`, and enforced no request ceiling at all.

> **The consumer's guard against the drift was itself unguarded, and that was
> measured rather than argued.** `CheckoutEndpoints` compares a reply's currency
> to the request's with `OrdinalIgnoreCase`, precisely because Catalog answers
> `GBP` to a request for `gbp`. Because the stub echoed the request, the
> comparison had never once been handed two spellings — so tightening it to
> `Ordinal` left every one of that suite's 62 pre-PR tests that need no
> container green, over a change that would answer 500 to every lower-case
> currency a customer typed. That is the fast half rather than the whole
> suite, which was 66 before this PR and is 81 after it; the four that want
> a Keycloak were not run.

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

> **The consumer stopped being able to drive the refusal half, and the
> interaction stays anyway
> ([ADR-045](adr/ADR-045-the-checkout-quote-takes-quantities.md)).** The quote
> now bounds its own line count at `OrderLimits.MaxLines` — the bound the
> *order* insists on, not a copy of Catalog's — so a basket past the ceiling
> fails validation before the hop and Catalog is never asked. The consumer
> suite asserts that instead, and the `InvalidArgument` mapping keeps its
> coverage in `QuoteEndpointTests`, which stubs the status rather than
> provoking it with a size.
>
> **An expectation the consumer cannot drive is still an expectation it
> needs**, which is the case worth having met once: the provider verification
> holds Catalog to the refusal either way, and the two bounds agree today only
> by coincidence of value. The day one of them moves, this is the interaction
> that says which — and had it been deleted as unreachable, the first symptom
> would have been a quote refusing baskets Catalog would have priced, or
> accepting ones it would not.

**It is not Pact, and the mechanism was a decision rather than a
convenience** — [ADR-023](adr/ADR-023-the-consumer-driven-contract-is-a-linked-file-not-pact.md)
records it. Pact's .NET binding cannot express gRPC at all, which is the only
relationship here that needed one; what Pact is *for* — one artefact the
consumer authors and the provider verifies — is taken, and the broker and wire
format that ship it across a repository boundary are declined, because this is a
monorepo and that boundary is not there.

**Two things it does not do.** It covers one relationship, because the platform
has one synchronous hop; a second would be the same conditional judgement again
rather than an automatic second contract. And it does not cross a repository
boundary — extract the BFF and this file becomes something that has to be
published, at which point Pact is the answer after all.

## 12.7 Test doubles

| Dependency | Approach |
|---|---|
| Domain objects | None. Use real ones. |
| Own database | Real, via Testcontainers |
| Own Redis | Real, via Testcontainers |
| Own broker | Real container, or the MassTransit test harness |
| Another service (HTTP) | WireMock.Net — a real HTTP server with stubbed responses |
| Third-party API | WireMock.Net, plus a nightly contract test against their sandbox |
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

**PR-25 took that decision rather than deferring it, and this passage used to
defer.** It read "a threshold that fails a build is PR-25's quality gates",
which named the pull request entitled to add one; that pull request declined,
on the sentence above rather than on effort. What it gated instead is whether
each stage *ran* — a fact, where a coverage percentage is a target — and
[Appendix C](appendix-c-delivery-plan.md)'s "quality gates" is that. A later
change may still argue for a threshold; it will be arguing against this
paragraph rather than filling a gap it left open.

**The figure is now a union across stages, and that is forced rather than
chosen.** §15.1 runs the unit and integration halves as separate `dotnet test`
invocations, so there is no longer one run to read — and the sentence above
asks for the domain assemblies "over the whole run". Measured on this
repository: the unit stage covers 253 of 308 method lines and the integration
stage 192, and the union is **257**. Four of those lines are reached only by a
test that needs a container, so a figure taken from either half alone
under-reports the thing it is named after.

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
> reference no source line can produce, and both gates went red on the first CI
> run that collected coverage. It does not reproduce on Windows, where the same
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

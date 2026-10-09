# 13. Observability

## 13.1 The three signals

| Signal | Question it answers | Tool |
|---|---|---|
| Metrics | Is something wrong right now? | Prometheus |
| Traces | Where in the system is it wrong? | Tempo / Jaeger |
| Logs | What exactly happened? | Loki / Seq |

They are used in that order during an incident. An alert fires on a metric, a
trace localises it to a service and a span, and logs filtered by that trace ID
explain it. Correlation between the three is what makes this work, which is why
section 10.4 exists.

## 13.2 OpenTelemetry

Configure once in `Common.Web` ([§4.1](04-solution-structure.md)), referenced by every service host.
`AddObservability` is one of the pieces `AddCommonWebDefaults` composes — the
single call every `Program.cs` makes (§4.2). Both overloads, from
`src/BuildingBlocks/Common.Web/CommonWebDefaultsExtensions.cs`:

```csharp
    public static IHostApplicationBuilder AddCommonWebDefaults(this IHostApplicationBuilder builder) =>
        builder.AddCommonWebDefaults(ServiceOptions.OperationTimeout);

    /// <summary>The same, for a host whose requests meet a deadline other than a service's (§9.7).</summary>
    public static IHostApplicationBuilder AddCommonWebDefaults(
        this IHostApplicationBuilder builder,
        TimeSpan requestTimeout)
    {
        builder.AddObservability();                            // §13.2
        builder.AddJwtAuthentication();                        // §11.3

        // The fallback policy makes authorization deny-by-default (ADR-030).
        builder.Services
            .AddAuthorizationBuilder()
            .AddPolicy("authenticated", p => p.RequireAuthenticatedUser())
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // §11.4's port, with the accessor ASP.NET Core does not register by default.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        builder.Services.AddCommonProblemDetails();            // §10.5
        builder.Services.AddCommonRequestTimeouts(requestTimeout);   // §9.7

        // Liveness only; readiness checks need connection strings a service owns (§13.5).
        builder.Services.AddHealthChecks();

        // Chosen, not inherited: an escaped exception is a defect, so the host stops and is restarted (§13.5).
        builder.Services.Configure<HostOptions>(o =>
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);

        return builder;
    }
```

**`SetFallbackPolicy` is what makes authorization deny-by-default, and the
named policy beside it is not.** Without a fallback policy `UseAuthorization`
evaluates nothing at all on an endpoint carrying no policy metadata, so an
endpoint class that omits its one `RequireAuthorization` call is reachable
anonymously with no compiler error, no `ValidateOnBuild` failure and no
failing test. With one, the omission is a 401 and a genuinely public route has
to spell `AllowAnonymous` — a line a reviewer can see. It reaches the
gateway's proxied routes too, which is why §10.2's route file names
`anonymous` on the public catalog route rather than leaving the key out: a
public path by omission and a public path by decision read identically in a
route file, and only one of them survives someone else's edit. The argument
belongs to [§11.4](11-identity-authorization.md), which owns this platform's
authorization rules, and is recorded in
[ADR-030](adr/ADR-030-authorization-is-deny-by-default-in-the-building-block.md);
it is restated here only because this is the block that registers it.

The `authenticated` policy beside it is deliberately identical to ASP.NET
Core's default, which YARP would accept as the magic string `default` (§10.2):
naming it costs one line and buys a route file that says what it means, and
that file is read by people deciding whether a path is public. `ICurrentUser`
and the `IHttpContextAccessor` it depends on are registered here rather than
in each service's `Add*Infrastructure`, because every host that authenticates
has a current user and neither type names a service. ASP.NET Core registers no
accessor by default, so omitting that line fails `ValidateOnBuild` rather than
the first ownership check.

Note what is **not** here. `AddCommonWebDefaults` covers what every host needs
identically. Anything needing a connection string — the SQL, Redis and broker
checks in §13.5 — belongs in `AddOrderingInfrastructure`, because
`Common.Web` cannot know them.

`AddObservability` itself, from
`src/BuildingBlocks/Common.Web/ObservabilityExtensions.cs`:

```csharp
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        string serviceName = builder.Environment.ApplicationName;

        // The only provider, because the redactor sees records in this pipeline and nowhere else (§13.4).
        builder.Logging.ClearProviders();

        // Wraps whatever scope provider is registered, so every provider's scopes are redacted (§13.4).
        RedactingScopeProvider.WrapScopesForRedaction(builder.Services);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;

            // §13.4's "never log a secret" rule, given a mechanism.
            logging.AddProcessor(new SensitiveDataRedactor());
        });

        KeyValuePair<string, object> environment =
            new("deployment.environment", builder.Environment.EnvironmentName);

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: BuildInfo.Version)
                .AddAttributes([environment]))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // A condition whose meter is not registered here cannot fire (§13.6).
                .AddMeter("Catalog.Outbox")                        // §13.6 per-lane
                .AddMeter("Ordering.Orders")                       // §13.3, §13.6
                .AddMeter("Ordering.Outbox")                       // §13.6 per-lane
                .AddMeter("Inventory.Reservations")                // §13.3
                .AddMeter("Inventory.Outbox")                      // §13.6 per-lane
                .AddMeter("Payments.Provider")                     // §3.2's provider
                .AddMeter("Payments.Outbox")                       // §13.6 per-lane
                .AddMeter("Shipping.Outbound")                     // §3.2's carrier, and the address read
                .AddMeter("Shipping.Outbox")                       // §13.6 per-lane
                .AddMeter("Notifications.Outbound")                // Notifications' outbound calls (§3.2)
                .AddMeter("Web.Bff.Projection")                    // ADR-051's projection

                // Shared names, not service-prefixed: the service.name resource attribute separates them.
                .AddMeter("Commerce.Requests")                     // §13.3, §13.7
                .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
                .AddMeter("MassTransit")
                // Publishes no meter at the pinned version; §13.6 records what the alert is owed.
                .AddMeter("Microsoft.Extensions.Caching.Hybrid")
                .AddMeter("StackExchange.Redis"))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                // No options: SetDbQueryParameters would put raw values on the span, past §13.4's redactor (§13.2).
                .AddEntityFrameworkCoreInstrumentation()
                // Redis instrumentation lives in AddRedisConnections, beside the keyed connections (§13.2).
                .AddSource("MassTransit")
                // OutboxDispatcher.ActivitySourceName: the span that joins a staging request to its publish (§9.4).
                .AddSource("Commerce.Outbox")
                // StagedTrace.ClaimSourceName: a worker's pass over a row it claims, joined to the row's writer (§9.4).
                .AddSource("Commerce.Claims"))
            .UseOtlpExporter();

        return builder;
    }
```

The shared meters are not service-prefixed. Every service emits the same
`Commerce.Requests` and `Commerce.Messaging` instruments and the
`service.name` resource attribute separates them, so one dashboard query works
for all of them, and a new service appears on it without anyone editing a
panel.

One piece of §13.2's telemetry, and one block beside it, are not in
`AddObservability`, and neither absence is a mistake.
The rule is that an instrumentation lands with the package it instruments —
unlike a meter name, which is a string, each costs a package reference, and a
reference to a library nothing uses is a claim about the dependency graph that
is not yet true. `AddEntityFrameworkCoreInstrumentation` is therefore here,
because a service holds a `DbContext`, and is live.
`AddRedisInstrumentation` is inside
`AddRedisConnections` ([§8.2](08-caching-redis.md)), not here, and permanently
so: §8.1's connections are keyed services, the parameterless overload
discovers only an unkeyed `IConnectionMultiplexer`, so registered in this
block it would silently instrument nothing — and it would hand
`StackExchange.Redis` transitively to every host, including the ones with no
Redis. The authorization block above is in `AddCommonWebDefaults` rather than
in `AddObservability`, beside `AddJwtAuthentication`, the scheme that makes its
policy mean anything — the two arrive together because neither works alone. A
policy requiring an authenticated user, with no scheme registered to
authenticate one, rejects every request that reaches it.

> **The EF Core call takes no options.** The instrumentation package emits the
> command text through the semantic-convention attributes by default. Its
> documentation also describes a `SetDbQueryParameters` switch that would add
> raw parameter **values** to a span — every password, token and card number
> the application has ever bound, written where
> [§13.4](13-observability.md)'s redactor never looks — but the pinned version
> does not expose it, although the XML docs list it. Nothing here can turn it
> on. **If a later bump exposes it, it stays off.**

> **A third attribute reaches the resource and no line above puts it there.**
> `deployment.track` — `stable` or `canary` — is what
> [§15.5](15-cicd-deployment.md)'s rollout compares the two versions by, and it
> arrives through `OTEL_RESOURCE_ATTRIBUTES`, the SDK's own mechanism, set by
> the chart from `canary.enabled` (§15.4,
> [ADR-022](adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)).
> The resource builder above already honours it, so nothing in `Common.Web`
> knows the word *canary* and there is no `AddAttributes` line for it.
> Asserted end to end against an exported resource rather than against the
> variable, because "the SDK reads this" is exactly the kind of claim that is
> true of a different overload.
>
> **`service.version` is the attribute a reader reaches for first and it cannot
> do this job.** `BuildInfo.Version` strips the source-revision suffix
> deliberately — a value that changes every commit turns one series into
> thousands — and [§4.4](04-solution-structure.md) pins no assembly version, so
> every host in the platform reports `1.0.0`. The attribute is registered,
> exported and constant: a registered name is not a live signal, which is the
> same trap §13.6 spends a callout on one section over.

Filtering health checks out of traces is not cosmetic — at a ten-second probe
interval across a dozen pods they would otherwise dominate both trace volume and
storage cost.

Recording SQL text on spans is invaluable for debugging and a data-exposure
risk if queries embed sensitive literals. Parameterised queries — which
everything here uses — put the parameterised form on the span and the values
nowhere, so the default is safe. Revisit it if anyone introduces string
concatenation into SQL, because that is the change that moves a literal from
the parameter list into the text.

## 13.3 Domain metrics

Infrastructure metrics tell you the servers are healthy. Business metrics tell
you the business is healthy, and they catch a category of failure that CPU
graphs never will.

Placement follows the dependency rule, not the topic. `OrderMetrics` records
domain quantities — `Placed(Money total)` — so it cannot sit in
`Common.Application`, which is shared across services and references no domain.
Its only call site is `OrderSummaryProjection`, which is Infrastructure, so
Infrastructure would also compile. It belongs in `Ordering.Application` anyway:
the type is a statement about the business vocabulary — placed, cancelled,
fulfilled — and Infrastructure is where that vocabulary is *implemented*, not
where it is defined. Application is also where it has to be the moment a
handler needs it again, and moving a type to satisfy one new call site is how
its meaning drifts. The type, from
`src/Services/Ordering/Ordering.Application/Orders/OrderMetrics.cs`:

```csharp
namespace Ordering.Application.Orders;

/// <summary>§13.3's business instruments, recorded by §6.6's projection after commit, never by a handler.</summary>
/// <remarks>Not in Common.Application, because <c>Placed</c> takes a domain <see cref="Money"/> (§13.3).</remarks>
public sealed class OrderMetrics
{
    private readonly Counter<long> _placed;
    private readonly Counter<long> _cancelled;
    private readonly Histogram<double> _value;
    private readonly Histogram<double> _fulfilmentSeconds;

    public OrderMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Ordering.Orders");

        _placed = meter.CreateCounter<long>(
            "orders.placed",
            unit: "{order}",
            description: "Orders successfully placed.");
        _cancelled = meter.CreateCounter<long>("orders.cancelled", unit: "{order}");
        _value = meter.CreateHistogram<double>(
            "orders.value",
            description: "The order total, in the currency its tag names.");
        _fulfilmentSeconds = meter.CreateHistogram<double>(
            "orders.fulfilment.duration",
            unit: "s",
            description: "Placed to confirmed.");
    }

    public void Placed(Money total)
    {
        _placed.Add(1, new KeyValuePair<string, object?>("currency", total.Currency));
        _value.Record((double)total.Amount, new KeyValuePair<string, object?>("currency", total.Currency));
    }

    /// <summary>Tagged with a <c>CancellationReasons</c> code, a bounded set, never an id (§13.3).</summary>
    public void Cancelled(string reason) =>
        _cancelled.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Fulfilled(TimeSpan placedToConfirmed) =>
        _fulfilmentSeconds.Record(placedToConfirmed.TotalSeconds);
}
```

An instrument with no call site is a metric that reads zero forever, which is
indistinguishable from a system doing no work. All three call sites are in
`OrderSummaryProjection` ([§6.6](06-cqrs.md)), and that is a rule rather than a coincidence:

> **A business metric is recorded on the committed path, never inside the
> write transaction.** A handler runs inside one, so a counter it increments
> counts orders that roll back — and counts them once per attempt when EF's
> retrying execution strategy replays the delegate (§6.3). The projection runs
> after the commit, driven by the `Local` outbox lane, which is the earliest
> point at which "an order was placed" is true.
>
> **And exactly once, which rows-affected does not give you.** The `Local` lane
> is at-least-once *and* unordered ([§9.4](09-messaging.md)), so "did this write change anything"
> answers the redelivery question and not the ordering one: a cancellation
> claimed before its placement changes a row, and counting it there records a
> cancelled order that `orders.placed` has not counted and — if the placement
> row is later abandoned — never will. `cancelled > placed` is unreachable in
> the write model, and a metric that can reach it is a metric no reconciliation
> can trust.
>
> So each counter is a **claim against the row**: a flag column, flipped and
> read in one `UPDATE`, with a predicate naming everything that must already be
> true. `RecordPendingFactsAsync` runs all three after every write, because any
> write may be the one that completes a pair.
>
> The rule generalises: **a business counter is state, not a side effect.** It
> fires once per fact, the fact is the row satisfying a predicate, and "it
> already fired" belongs in the same table as the fact — not in the control flow
> of whichever handler happened to arrive first.

The three claims are `UPDATE` constants beside the handlers in
`OrderSummaryProjection.cs`, under `Ordering.Infrastructure/Projections/`, and
`RecordPendingFactsAsync` runs them in turn, recording each one it wins. The
cancellation's predicate carries `PlacedCounted = 1`, which is what orders the
two counters, and `CancelReason` is written by the handler through
`CancellationReasons.ToCode`, the same table the parse uses, which keeps the
dimension a bounded, stable set. The placement's `Money` is reassembled from
the row rather than taken from the event, because the event that triggers the
call may be a cancellation:

```csharp
    /// <summary>Records every fact the row now supports and has not yet counted (§13.3).</summary>
    private async Task RecordPendingFactsAsync(IDbConnection connection, OrderId orderId, CancellationToken ct)
    {
        var args = new { OrderId = orderId.Value };

        PlacedFact? placed = await connection.QuerySingleOrDefaultAsync<PlacedFact>(
            new CommandDefinition(ClaimPlacedSql, args, cancellationToken: ct));

        // The column only ever holds Money's own three letters, so it returns unpadded to Money.Of (§5.3).
        if (placed is not null)
            metrics.Placed(Money.Of(placed.TotalAmount, placed.Currency));

        string? cancelled = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(ClaimCancelledSql, args, cancellationToken: ct));

        if (cancelled is not null)
            metrics.Cancelled(cancelled);

        FulfilmentFact? fulfilment = await connection.QuerySingleOrDefaultAsync<FulfilmentFact>(
            new CommandDefinition(ClaimFulfilmentSql, args, cancellationToken: ct));

        if (fulfilment is not null)
            metrics.Fulfilled(fulfilment.ConfirmedAt - fulfilment.PlacedAt);
    }
```

Fulfilment duration is recorded there for a second reason on top of that one. It
spans placement to confirmation, and the summary row is the only place that sees
both ends — the handler that confirms an order knows nothing about when it was
placed. Its claim, `ClaimFulfilmentSql` in the same file:

```csharp
    private const string ClaimFulfilmentSql =
        """
        UPDATE ordering.OrderSummaries
        SET FulfilmentCounted = 1
        OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
        WHERE OrderId = @OrderId
            AND PlacedAt IS NOT NULL
            AND ConfirmedAt IS NOT NULL
            AND FulfilmentCounted = 0;
        """;
```

> The projection is the right home for a *duration* metric because it is the
> component that has already gathered both halves. A handler measuring this
> would have to re-read the aggregate to find its own start time.
>
> **Note what the predicate is not.** It does not measure `now − PlacedAt` when
> the `Confirmed` event arrives, guarded on `PlacedAt` being set. `PlacedAt` is
> legitimately NULL for an order whose confirmation was claimed first (§6.6),
> so that guard would silently drop the measurement, permanently, for exactly
> the orders whose delivery was disordered — which correlates with load, which
> is when the number matters. Claiming on "both timestamps present and not yet
> counted" has no ordering assumption to be wrong about.

Note the cardinality discipline: tags are `currency` and `reason` — small,
bounded sets. **Never tag a metric with an order ID, customer ID or URL with an
embedded ID.** Each distinct tag combination is a separate time series, and
unbounded cardinality is the standard way to take down a Prometheus instance.

> **Only one of these four is alerted on, and that is deliberate.**
> `orders.placed` backs the business-volume alert (§13.6) because a drop in it
> is a symptom nothing else shows. `orders.cancelled`, `orders.value` and
> `orders.fulfilment.duration` are dashboard metrics: they answer *"how is the
> business doing"*, a question with no threshold that should wake anyone.
>
> The rule runs one way only. **Every alert and SLO row must name an
> instrument** (§13.6, §13.7) — a target with no signal reads as satisfied. An
> instrument with no alert is just a number somebody looks at, which is most of
> them. The asymmetry is worth stating because the tidy-looking mistake is to
> invent thresholds for the other three so every metric has a row, and a page
> for "cancellations up 20%" is one nobody can act on at 3 a.m.

### The two types this section defines, and where the rest come from

Domain metrics answer business questions. The SLO table answers *"is this
service behaving"*, and its rows need instruments too — a target with no signal
is not a target, it is an intention.

§13.7's seven rows read **four** sources, and only two of them are defined here.
Naming all four is the point of the table, because the two that are not are the
ones a reader would otherwise go looking for in this section and fail to find:

| Source | Defined in | Signals it provides to §13.7 |
|---|---|---|
| `RequestMetrics` | here, `Common.Application` | `request.duration` — the command and query p95 rows |
| `MessagingMetrics` | here, `Common.Infrastructure` | `messaging.delivery.lag`, `projection.lag` |
| `OutboxMetrics` | §13.6, each publishing service's `*.Infrastructure` | `outbox.oldest.age`, read once per lane |
| ASP.NET Core instrumentation | the framework, enabled in §13.2 | `http.server.request.duration` — the availability row |

`RequestMetrics` is Application because `LoggingBehavior` injects it and the
pipeline is Application. `MessagingMetrics` is Infrastructure because all four
of its call sites are — two consumers, §9.5's inbox filter and the outbox
dispatcher's invoker. `OutboxMetrics` is separate from both because it reads
the database, which is also why it is observable rather than pushed (§13.6).

**The table's last column is what each source contributes to §13.7, not what it
declares.** `MessagingMetrics` publishes two instruments that appear nowhere in
it: `command.domain_rejected` feeds no SLO row at all, being a business signal
that happens to share the meter (§9.8), and `messaging.inbox.suppressed` feeds
none either — it exists so that §9.5's deliberate drop stops being the one path
in this platform with no signal at all. So the class below has four instruments
and its row above lists two, and both are right — a reader counting one against
the other will find a difference that is not a defect, which is why it is said
here rather than left to be noticed.

`RequestMetrics` is registered by `AddOrderingApplication` (§4.2) and forced at
startup by each service's `MetricsInitialiser` (§13.6), because "a behaviour
injects it" is not the same as "something has constructed it". From
`src/BuildingBlocks/Common.Application/RequestMetrics.cs`:

```csharp
/// <summary>§13.3's <c>request.duration</c>, injected by <see cref="LoggingBehavior{TRequest,TResult}"/>.</summary>
public sealed class RequestMetrics
{
    private readonly Histogram<double> _duration;

    public RequestMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Requests");
        _duration = meter.CreateHistogram<double>(
            "request.duration",
            unit: "s",
            description: "Dispatcher entry to result.");
    }

    public void Recorded(string request, string outcome, TimeSpan elapsed) =>
        _duration.Record(
            elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("request", request),
            new KeyValuePair<string, object?>("outcome", outcome));
}
```

`MessagingMetrics` is registered by `AddOrderingInfrastructure`, because all
four call sites are Infrastructure types (§9.4, §9.5). Its fields and
constructor declare the four instruments, from
`src/BuildingBlocks/Common.Infrastructure/Messaging/MessagingMetrics.cs`:

```csharp
    private readonly Histogram<double> _deliveryLag;
    private readonly Histogram<double> _projectionLag;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _suppressed;

    public MessagingMetrics(IMeterFactory factory)
    {
        Meter meter = factory.Create("Commerce.Messaging");

        _deliveryLag = meter.CreateHistogram<double>(
            "messaging.delivery.lag",
            unit: "s",
            description: "OccurredAt to consumer start.",
            tags: null,
            advice: LagBuckets);
        _projectionLag = meter.CreateHistogram<double>(
            "projection.lag",
            unit: "s",
            description: "Event raised to projection applied.",
            tags: null,
            advice: LagBuckets);
        _rejected = meter.CreateCounter<long>(
            "command.domain_rejected",
            description: "Message-borne commands the domain refused (§9.8).");
        _suppressed = meter.CreateCounter<long>(
            "messaging.inbox.suppressed",
            description: "Messages the inbox dropped as already handled (§9.5).");
    }
```

An instrument lands with the call site that records it, because one nothing
writes to is an empty series on a dashboard rather than a signal; that is the
rule for when one may be added, not a claim that four is the number. The
recording methods, in the same file, tag both lags by `message`,
`command.domain_rejected` by `message` and `error`, and
`messaging.inbox.suppressed` by `message` and `endpoint`. Neither of the last
two is the `MessageId`, and that is the constraint rather than an omission: a
message id is unbounded, so it belongs on the log line the inbox filter writes
beside the counter and never on a series.

The two lags read `OccurredAt` from **different places**, because they measure
different lanes. `Delivered` reads it **off the message**: it covers the broker
lane, every integration event carries the field (§9.1), and
`IntegrationEventConsumer<T>` reaches it through the `IIntegrationEvent`
constraint — so there is no header to define and nothing to keep in sync.
`Projected` reads it **off the outbox row**, which the claim returns (§9.4)
and which `Stage` copies from the message rather than from a clock.
It has to: the local lane carries domain events, and `ProjectionInvoker<TEvent>`
is deliberately unconstrained — `IProjectionHandler<T>` is satisfied by any
type, including the read-model-shaped events a projection may prefer. Every
`IDomainEvent` does carry `OccurredAt` ([§5.5](05-tactical-ddd.md)), so a constraint would compile
today; it would also make the metric the reason the invoker cannot accept a
plain record tomorrow. The row already has the timestamp, and reading it there
costs a column the claim was going to pay for anyway.

`IntegrationEventConsumer<T>`, `CommandConsumer<,>` and `InboxFilter<T>` take
`MessagingMetrics` as a constructor parameter. `Projected` is recorded by
`ProjectionInvoker` (§9.4), which is static and cached — it resolves
`MessagingMetrics` from the `IServiceProvider` it is already handed rather than
through a constructor it does not have.

**The two lags are exported on bucket bounds the class gives as advice**,
`MessagingMetrics.LagBuckets`, because the OpenTelemetry SDK gives
seconds-scale defaults only to instruments it knows by name, and on its
millisecond defaults every lag under five seconds lands in one bucket, where a
quantile reads the same number whatever the lag was. The bounds reach below a
second not to claim precision there but so that a healthy lag lands in a
narrow bucket, and §13.7's targets are bounds themselves, so the p95 §13.6
alerts on is read at a bucket edge.

Both lags compare a timestamp made on another machine, so both carry the same
caveat: they are useful at second granularity and meaningless below it, which is
why §13.7's targets for them are in seconds and not milliseconds. The two
counters carry no such caveat, because neither reads a clock at all:
`command.domain_rejected` is recorded by `CommandConsumer` at the moment the
dispatcher returns a failure (§9.8), and `messaging.inbox.suppressed` by
`InboxFilter<T>` at the moment it drops a delivery (§9.5) — both on the
machine doing the work.

> **These get a `MetricsInitialiser` entry too (§13.6), and the tempting reason
> not to is the instrument kind.** An observable gauge is pull-based — the
> collector asks, and if nothing ever constructed the class there is nothing to
> ask — which makes forcing the outbox gauges obviously necessary. A histogram
> is pushed from a live call site, so anything recording it has already resolved
> the class, and it looks safe to leave out. It is not: the call site has to be
> *reached*, and a consumer is constructed when a message arrives, so on a quiet
> service these instruments still do not exist. §13.6 states the test that
> actually decides membership — can this service run for an hour without
> constructing it — and all four types fail it.

The behaviour that records the first of these, from
`src/BuildingBlocks/Common.Application/LoggingBehavior.cs`:

```csharp
/// <summary>Registered first, so outermost (§6.3): its span covers every behaviour and the handler.</summary>
/// <remarks>A returned failure is <c>ok</c>, because a refused command is not a broken system (§13.3).</remarks>
public sealed class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger,
    RequestMetrics metrics,
    TimeProvider clock)
    : IPipelineBehavior<TRequest, TResult>
{
    private static readonly Action<ILogger, string, double, Exception?> Completed =
        LoggerMessage.Define<string, double>(
            LogLevel.Information,
            new EventId(1, nameof(Completed)),
            "{RequestType} completed in {ElapsedMs} ms");

    private static readonly Action<ILogger, string, Exception?> Threw =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(2, nameof(Threw)),
            "{RequestType} threw");

    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        string name = typeof(TRequest).Name;
        long start = clock.GetTimestamp();

        // A scope, not a log property, so EF Core's and MassTransit's logging inside the handler inherit it.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestType"] = name
        });

        try
        {
            TResult result = await next();

            // Read once, so the log line and the histogram carry the same number.
            TimeSpan elapsed = clock.GetElapsedTime(start);

            Completed(logger, name, elapsed.TotalMilliseconds, null);
            metrics.Recorded(name, "ok", elapsed);

            return result;
        }
        catch (Exception ex)
        {
            Threw(logger, name, ex);
            metrics.Recorded(name, "error", clock.GetElapsedTime(start));
            throw;
        }
    }
}
```

> **`outcome` is `ok` or `error`, and a returned `Result.Failure` is `ok`.** The
> behaviour is generic over `TResult` and cannot see inside it without a
> constraint that would exclude queries — but the deeper reason is that a
> rejected command is a normal outcome of a working system. "Order cannot be
> cancelled once shipped" is the domain doing its job, and counting it as an
> error makes the one number that should mean *"something is broken"* track
> customer behaviour instead. Business outcomes are counted by the domain
> instruments above, where they have names.

`TimeProvider.GetTimestamp()` rather than `Stopwatch`, for the reason §5.4
gives about time: the same seam the tests replace, used everywhere including
here.

## 13.4 Structured logging

```csharp
// Good — structured, queryable, no PII.
logger.LogInformation(
    "Order {OrderId} placed by customer {CustomerId} for {Amount} {Currency}",
    order.Id,
    order.CustomerId,
    order.Total.Amount,
    order.Total.Currency);

// Bad — string interpolation destroys the structure; the fields cannot be
// queried and every message is a distinct string.
logger.LogInformation($"Order {order.Id} placed for {order.Total}");
```

Both halves call `LogInformation` directly, and that is deliberate — the pair
is about message templates, and a `LoggerMessage.Define` field either side of
it would bury the one difference the reader is meant to see. Every logging call
site the solution actually builds takes the compiled form instead, because
CA1848 is enforced (ADR-019) and the classes that log are the ones that run per
request or per message: §13.3's `LoggingBehavior`, and `OutboxDispatcher` and
`CommandConsumer` in [§9.4](09-messaging.md). Fragments here teach the
template; those three show the shape.

Levels, applied consistently:

| Level | Use | Example |
|---|---|---|
| `Trace` | Developer diagnostics. Off in production. | Method entry with arguments |
| `Debug` | Diagnosable detail. Off by default in production. | Cache miss, retry attempt |
| `Information` | Business events worth an audit trail. | Order placed, payment authorised |
| `Warning` | Recovered, but someone should know. | Retry succeeded after failures, circuit half-open |
| `Error` | An operation failed. | Handler threw, message went to error queue |
| `Critical` | The service cannot function. | Database unreachable at startup |

**Never log:** passwords, secrets, tokens, authorization headers, credentials,
connection strings, cookies, API keys, account keys, private keys, full card
numbers, national ID numbers, CVVs, one-time passcodes, session ids and
signatures — or full request bodies on endpoints that accept them. That is the
term list below in prose, and the two are reconciled together: a reader scans
this sentence and the code reads the array, so a term in one and not the other
is a rule nobody enforces. A connection string and a
JWT are recognised by the shape of their **value** as well, whatever the key
they arrived under is called, which is the half of the rule that survives a
name nobody predicted.

A rule of that shape needs a mechanism, or it is a request that every future
developer remember it. There are two mechanisms, in two layers, because a log
record carries values through two channels and one piece of code cannot reach
both: a `BaseProcessor<LogRecord>` rewrites the record's own **attributes**,
so a property named `Password` is redacted by default rather than by
discipline, and an `IExternalScopeProvider` wrapper rewrites the **scopes**
those records inherit — which the processor can read and cannot change. Both
read one vocabulary, declared once so that the copy nobody edits is not the
one that stops matching, in `src/BuildingBlocks/Common.Web/SensitiveKeys.cs`:

```csharp
/// <summary>The one never-log vocabulary, read by the redactor and the scope provider alike (§13.4).</summary>
/// <remarks>Matching is by substring, so <c>pin</c> is absent because <c>Shipping</c> contains it (§13.4).</remarks>
public static class SensitiveKeys
{
    // Both snake_case spellings are listed, because normalising a key would guess at the separator.
    private static readonly string[] Terms =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "authorization",
        "credential",
        "cookie",
        "apikey",
        "api_key",
        "connectionstring",
        "connection_string",
        "privatekey",
        "private_key",
        "cardnumber",
        "card_number",
        "ssn",
        "nationalid",
        "cvv",
        "otp",
        "sessionid",
        "session_id",
        "accountkey",
        "account_key",
        "signature"
    ];

    /// <summary>The never-log terms, wrapped so a caller cannot cast back to the array and rewrite them.</summary>
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Terms);

    // A foreach rather than Any, because a lambda capturing key would allocate on every attribute.
    public static bool Matches(string key)
    {
        foreach (string term in Terms)
        {
            if (key.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
```

Matching is by substring, ordinal and case-insensitive, because the field that
leaks is never named exactly `password` — it is `NewPassword`, `card_number`,
`id_token`. The cost is that a term which is a substring of an innocent word
redacts that word too, which is why `pin` is deliberately absent: `Shipping`
contains it. Both spellings of the snake_case entries are listed rather than
normalised, because normalising a key would have to guess at the separator
and a miss here is silent. `All` wraps the array once rather than handing it
out, because returning it would let a caller cast the view back to `string[]`
and rewrite the vocabulary at run time, where the test that pins the list
could never see it. `Matches` loops rather than calling `Any`, because a
lambda capturing the key would allocate on every attribute of every record.

`LooksLikeSecret`, in the same file, is the half that survives a key nobody
predicted: the list can only catch a name someone thought of, and that failure
is silent. It recognises two shapes, because both are unmistakable and both
are what this platform holds: a connection string, which every service builds
from configuration and which carries Password= inline, and a JWT, which §11.3
puts on every authenticated request. A JWT is anchored on the `eyJ` prefix its
base64url header always opens with, then on the compact serialisation's two
dots. A connection-string password is found as an assignment — the key, any
whitespace, then `=` — because ADO.NET tolerates spaces around the separator,
so `Password = hunter2` is as valid as `Password=hunter2` and a check for the
key and `=` side by side misses it; requiring the `=` is what keeps the check
off prose such as "the password was rejected". It is deliberately not an
entropy test: a high-entropy string is an id as often as it is a credential,
and redacting every id would empty the records an incident is triaged by —
§13.1's whole argument.

The processor, `SensitiveDataRedactor.cs` beside it, reads that vocabulary and
rewrites what a record carries of its own — the attributes first, then the two
fields the exporter would otherwise ship the same secret through. It is
public, as `MetricsInitialiser` (§13.6) and `Program` (§4.2) are, because a
test outside `Common.Web` constructs it. Its `OnEnd`:

```csharp
    private const string OriginalFormat = "{OriginalFormat}";

    /// <inheritdoc />
    public override void OnEnd(LogRecord record)
    {
        if (record.Attributes is null)
            return;

        List<KeyValuePair<string, object?>>? scrubbed = null;
        List<string>? secrets = null;
        bool hasTemplate = false;

        for (int i = 0; i < record.Attributes.Count; i++)
        {
            KeyValuePair<string, object?> attribute = record.Attributes[i];

            if (attribute.Key == OriginalFormat)
                hasTemplate = true;

            // The template is exempt from the value check, because the fallback below depends on it.
            if (!SensitiveKeys.Matches(attribute.Key) &&
                (attribute.Key == OriginalFormat || !SensitiveKeys.LooksLikeSecret(attribute.Value)))
            {
                continue;
            }

            // Copied only on a match, because this runs on every log record.
            scrubbed ??= [.. record.Attributes];

            if (attribute.Value?.ToString() is { Length: > 0 } secret)
                (secrets ??= []).Add(secret);

            scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
        }

        if (scrubbed is null)
            return;

        record.Attributes = scrubbed;

        // The exporter ships FormattedMessage as the body, and Body is only a safe template with one (§13.4).
        record.FormattedMessage = hasTemplate && record.Body is not null
            ? record.Body
            : "[redacted]";

        // OTLP serialises the exception separately, so one that repeats a redacted value is dropped (§13.4).
        if (record.Exception is not null && secrets is not null && Reveals(record.Exception, secrets))
            record.Exception = null;
    }
```

The rule its last two statements enforce is never to export a value the
processor has just decided is sensitive. `{OriginalFormat}` is exempt from the
value check because the template is written by the author rather than bound
from data, and the fallback depends on it. `Body` is only that template when
the state carried `{OriginalFormat}`; without it OpenTelemetry fills `Body`
with the formatter's own output, so the fallback there is `[redacted]`. OTLP
serialises the exception separately from both the attributes and the
formatted message, as `exception.message` and `exception.stacktrace`, so the
exception is dropped when `Reveals`, in the same file, finds a redacted value
in its `ToString()`, which covers inner exceptions and the stack trace too.
Dropped rather than rewritten, because `Exception.Message` is read-only; and
deliberately narrower than dropping it whenever anything was redacted, which
would destroy the stack trace on every record that merely has a `Password`
attribute beside an unrelated failure.

> **Scrubbing the attributes alone would protect nothing.**
> `IncludeFormattedMessage` is on (§13.2), and with it the exported body is the
> *rendered* string — so `Login for ada with hunter2` would sit in the one
> field a log backend indexes and searches, next to a `Password` attribute
> reading `[redacted]`. The fallback is `Body`, the un-substituted template:
> still readable, and the values that were not sensitive are still on the
> record as attributes.

A record with nothing sensitive on it keeps its formatted message untouched,
and that is asserted as its own test rather than left implied. Without it a
processor that rewrote unconditionally would pass every redaction test in the
suite while quietly emptying every log line on the platform.

Three limits worth stating rather than discovering.

Redaction is **by key or by value shape**. The key is matched by substring, so
`logger.LogInformation("Token is {Value}", token)` is caught that way only if
the placeholder is named sensitively — the argument for naming it `{Token}`.
The value check catches it whatever the placeholder is called, but only for
the two shapes `LooksLikeSecret` recognises, which is a deliberate floor
rather than a general test. Interpolation defeats both: `$"Token is {token}"`
produces no attribute to match *and* puts the secret in the template, so the
fallback carries it too.

It cannot help with a **whole object logged as one attribute**; that is what
the "never log full request bodies" half of the rule is for.

And an **exception can still carry a secret the attributes never named**.
Where a redacted value reappears in the exception text the exception is
dropped, under the rule above — never export a value the processor has just
decided is sensitive. Where the secret was only ever in the exception, there
is nothing to match it against and it survives. That is the interpolation
case again: text an author wrote by hand, which no key-based mechanism can
inspect. `throw new InvalidOperationException($"bad token {token}")` is the
same mistake as `$"Token is {token}"` and is caught by neither.

**Scopes are redacted as well, and by a second mechanism rather than by more
of the processor.** `IncludeScopes` is on (§13.2), so every record inherits
the scopes open around it. The platform's own two are safe only for reasons
that live elsewhere: `LoggingBehavior`'s `RequestType` (§13.3) is a type name,
and `UseCorrelationId`'s `CorrelationId` ([§10.4](10-api-gateway.md)) carries
a client-supplied `X-Correlation-Id` only within the bounds §10.4's middleware
sets on what it will adopt. A safety claim that rests on a neighbouring
component's validation is one that expires the next time that component is
edited, silently, so scopes are redacted where they are read, whatever opened
them.

> **A processor cannot fix this, only notice it.** `LogRecord` exposes
> `ForEachScope` and no settable scope provider, so a
> `BaseProcessor<LogRecord>` that walked the scopes could read a secret out of
> one and would have no way to put anything else back. Redaction has to happen
> where the scope is *read*.

That place is one layer lower than the pipeline: the `IExternalScopeProvider`
`LoggerFactory` resolves from the container and hands to every provider it
holds. §13.2 registers a wrapper there, which is why the redaction covers the
scopes EF Core and MassTransit open as well as the platform's own two. That
breadth is the point rather than a bonus: an inventory of this repository's
`BeginScope` calls could never include a library's.

The wrapper is `src/BuildingBlocks/Common.Web/RedactingScopeProvider.cs`. Its
registration takes the last non-keyed `IExternalScopeProvider` out of the
collection and registers itself around it, rather than standing aside for a
provider already there, because §13.4 is a guarantee and not a default:

```csharp
    public static IServiceCollection WrapScopesForRedaction(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? existing = services.LastOrDefault(
            d => d.ServiceType == typeof(IExternalScopeProvider) && !d.IsKeyedService);

        if (existing is not null)
            services.Remove(existing);

        // Owns the inner provider when the container would have created, and so disposed, it.
        services.AddSingleton<IExternalScopeProvider>(
            sp => new RedactingScopeProvider(
                Inner(sp, existing),
                ownsInner: existing is not null && existing.ImplementationInstance is null));

        // A later registration would win, so the guard checks the resolved provider once the host starts.
        services.AddHostedService<ScopeRedactionGuard>();

        return services;
    }
```

The prior descriptor is removed and rebuilt inside the factory rather than
resolved, because resolving `IExternalScopeProvider` from within its own
factory is unbounded recursion. Only the last registration is wrapped, because
single-service resolution returns the last, so the earlier ones were already
unreachable. `ownsInner` follows who created the inner provider, because that
is who the container would have disposed: one built from a factory or an
implementation type is the wrapper's to dispose, and an instance the caller
supplied is not.

Wrapping what came before is only half of it. `AddCommonWebDefaults` runs
ahead of a host's own registrations and the container resolves the last, so
an `IExternalScopeProvider` registered afterwards replaces the wrapper and
exports every scope raw. Nothing at registration time can prevent that, so
`ScopeRedactionGuard`, a hosted service in the same file, refuses to start a
host whose resolved provider is not the wrapper — the direction §13.5's
readiness guard takes, because a control that is silently absent is worse
than a host that will not boot. It reports rather than repairs: re-registering
would leave a host running with a provider its own author did not choose.

The wrapper redacts on the way out, not on the way in. `Push` stores the
caller's object untouched, because a scope is also a live object the
application may read back, and only the enumeration a logging provider
performs is rewritten. A keyed scope is matched as an `IEnumerable` of pairs
rather than an `IReadOnlyList`, because `BeginScope(new Dictionary<,>)` — what
§10.4 and §13.3 both open — produces a `Dictionary`, which is not a list:

```csharp
    private static object? Redact(object? scope)
    {
        // IEnumerable rather than IReadOnlyList, because BeginScope(new Dictionary<,>) is not a list (§13.4).
        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            return RedactPairs(scope, pairs);

        return SensitiveKeys.LooksLikeSecret(scope) ? Redacted : scope;
    }
```

A scope with no keys reaches the exporter as a single unkeyed value, so only
the value check can say anything about it. A scrubbed scope is returned as a
`RedactedScope` whose `ToString` is overridden, because a provider that
formats a scope rather than enumerating it would otherwise print the secret
straight back out — the same failure the `FormattedMessage` rewrite exists to
prevent, one layer over.

**The processor governs one pipeline, which is why §13.2 leaves only one.** A
`BaseProcessor<LogRecord>` sees records inside OpenTelemetry and nowhere else.
Any other `ILoggerProvider` on the host formats the original state itself and
never passes through this code — so `AddObservability` calls
`ClearProviders()` before adding OpenTelemetry, making it the sole provider.

That is not tidiness. `WebApplication.CreateBuilder` installs Console, Debug and
EventSource before a host reaches `AddCommonWebDefaults` (§4.2), and container
stdout is collected in most clusters, so a `{Password}` scrubbed on the OTLP
path would ship in clear text on the console one. The redaction would look
complete and cover a single destination — the same shape as the
`FormattedMessage` gap above, one layer further out.

Two things follow, both worth stating. The guarantee covers providers registered
**before** `AddObservability`, which is every default and the only case §4.2
produces; a service that adds a provider afterwards has opted out and owns the
consequence. And the visible cost is local: `dotnet run` prints nothing to the
terminal, because nothing is left that writes there. §13.1 routes logs to Loki
or Seq through OTLP regardless, and [§14.1](14-local-development.md) runs a
collector, so the loss is the raw terminal stream rather than the logs
themselves — add a console exporter to the OpenTelemetry pipeline if a
developer wants it back.

Assert it, because a redactor that silently stops matching is worse than none.
The test lives in `Common.Web.Tests` — a `Common.Web` behaviour tested once
rather than once per host, in the suite that already owns this project's
behaviour ([§12.1](12-test-strategy.md)). Every host calls `AddObservability`, so a copy in a
service's own suite would re-assert the same processor over the same pipeline
and only add a place to forget — and a building block asserted in Ordering's
suite is one that moves house if Ordering ever does.

**The vocabulary itself is pinned as a list, by a test whose subject is the
list.** Every other test in this area drives a record through the processor
and asserts one key at a time, so a term deleted in a refactor takes its own
test with it: the suite stays green while what reaches the collector quietly
widens. `SensitiveKeys.All` is therefore asserted against the array this
repository decided on, spelled out in the test rather than computed from the
property under test — a test that reads the value it is checking cannot notice
that value changing, which is the one thing it is there to notice.

Assert it through `ILogger`, not through OpenTelemetry's logger provider
directly. The Logs Bridge API (`Sdk.CreateLoggerProviderBuilder`) is shipped
behind an experimental diagnostic and is not how any host here produces a log
record; a test that used it would be green while the path in production drifted
away underneath it. The seam, from
`tests/Common.Web.Tests/SensitiveDataRedactorTests.cs`:

```csharp
    private static LogRecord EmitRecord(Action<ILogger> write)
    {
        List<LogRecord> exported = [];

        // Built as AddObservability builds it (§13.2), IncludeFormattedMessage included, so this is the host's seam.
        using (ILoggerFactory factory = LoggerFactory.Create(b =>
            b.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.AddProcessor(new SensitiveDataRedactor());
                o.AddInMemoryExporter(exported);
            })))
        {
            write(factory.CreateLogger("test"));
        }

        return exported.Single();
    }
```

`Login`, declared above it, is a `LoggerMessage.Define` over the template
`Login for {User} with {Password}`, because CA1848 is enforced repo-wide
(ADR-019) and does not exempt test projects; the attribute keys still come
from a message template, read through `ILogger`.
`Sensitive_attributes_are_redacted` asserts that `Password` reads `[redacted]`
and that `User` survives intact — the half that catches a deny-list grown
careless — and `A_redacted_record_does_not_export_the_rendered_secret` asserts
that the formatted message is the template.

**The seam comment in that helper is true of the attribute half only.** The
factory registers no scope provider, so nothing on this path exercises the
scope redaction argued above; that half is asserted separately, against the
`IExternalScopeProvider` a `LoggerFactory` hands its providers — the seam the
mechanism actually rests on, and the one that would go quiet if a release ever
stopped resolving the registration.

Going through `ILogger` also means the test exercises message templates, which
is where the attribute keys come from — so the `{Token}` naming advice above is
verified by this test rather than merely stated near it.

### How long a log and a trace are kept

**A log and a trace each have a lifetime, and it is a value the deployment
states.** The template above sends a customer's id rather than a name or a
mailbox, and so do the services' own lines, but an identifier is pseudonymous
and not anonymous:
[ADR-035](adr/ADR-035-an-integration-event-carries-identifiers-not-personal-data.md)
already calls it personal data, and a span's attributes carry the same ids.
[§11.7](11-identity-authorization.md)'s erasure deletes or anonymises what each
service owns and reaches no log store, so the lifetime is the only thing that
ever removes those ids from it. That is why the lifetime is bounded. It is not
shorter than the deployment's incident investigations need, because an
incident is read from these records after it is noticed, and a personal-data
incident's statutory clock
([ADR-053](adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md))
runs from when it is discovered, not from when it happened.

**It is set in the log and trace stores' own retention configuration, by
whoever runs them.** This repository deploys neither —
[§15.3](15-cicd-deployment.md)'s charts render this platform's workloads and
no stateful store, and §13.8 says the same of Prometheus and Grafana — so the
statement is a requirement on the deployment, like the recovery §15.3 demands
of every store it does not run: a deployment that cannot name its log lifetime
and its trace lifetime is not ready for customer traffic. ADR-053's rule 3
gives each jurisdiction its own log store, so each has its own lifetime.

**It is not a member of a service's jurisdiction options**, though ADR-053 is
where the jurisdiction's windows live. Those classes are bound by a host that
applies each window — its own retention purge reads it — and no host deletes a
log or a span; the store does. A member no code applies would pass validation
at start and prove nothing about the store. Notifications'
`Jurisdiction:LogRetention` is a different thing: the window its
`NotificationLog` rows are kept as evidence, which its own purge applies.

**Locally there is no lifetime to choose.** §14.1's `grafana` container holds
the logs and traces it ingests on no named volume, so they last as long as the
container does.

## 13.5 Health checks

Three distinct endpoints, because Kubernetes asks three distinct questions.

Registration and exposure live in different places, for one reason: the checks
need connection strings and the endpoints do not.

**The checks** are registered by the service's own Infrastructure — the block
shown in `AddOrderingInfrastructure` (§4.2), which has the configuration, in
`src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs`:

```csharp
        // Readiness lives here, not in Common.Web, because it needs the connection strings (§13.5). Both Redis
        // instances, since AbortOnConnectFail is false and §8.1 gives them different servers.
        services
            .AddHealthChecks()
            .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Cache)!,
                name: "redis-cache",
                tags: ["ready"])
            .AddRedis(
                configuration.GetConnectionString(RedisConnections.Coordination)!,
                name: "redis-coordination",
                tags: ["ready"]);
```

**The broker's readiness check rides in with the bus registration, not with
this block.** `AddMassTransit` contributes a check named `masstransit-bus`,
tagged `ready` and `masstransit`, that reports the actual bus — connection and
receive endpoints both — so the predicate above picks it up with no line here.
A transport-level check from the `AspNetCore.HealthChecks.Rabbitmq` package
was considered and rejected: its parameterless `AddRabbitMQ()` resolves an
`IConnection` from the container, and nothing registers one — MassTransit does
not expose its connection as that type — so the check as written would throw
on every probe and the pod would never become ready. Making it work means
holding a **second** AMQP connection whose only job is answering a weaker
question than the bus's own check already answers.

**The endpoints** are mapped once in `Common.Web`, since the tag predicates are
identical for every service and need no configuration. `Program.cs` calls this
after `builder.Build()` (§4.2), from
`src/BuildingBlocks/Common.Web/HealthCheckExtensions.cs`:

```csharp
    // Spelled once, so the predicates and the startup guard ask about the same set.
    private const string Ready = "ready";

    /// <summary>Maps the live, ready and startup probes, refusing an empty readiness set by default.</summary>
    /// <remarks>An empty predicate set passes, so a host with none must say so at the call site (§13.5).</remarks>
    public static IEndpointRouteBuilder MapCommonHealthEndpoints(
        this IEndpointRouteBuilder app,
        bool ownsNoReadinessDependencies = false)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!ownsNoReadinessDependencies && !AnyReadinessCheck(app))
        {
            throw new InvalidOperationException(
                "No health check carries the \"ready\" tag, so /health/ready would answer 200 " +
                "without having verified anything (§13.5). Register the service's readiness " +
                "checks in its own Infrastructure, or pass ownsNoReadinessDependencies: true if this host " +
                "gates readiness on nothing.");
        }

        // The kubelet sends no token, so an authenticated probe restarts the pod in a loop.
        app
            .MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) })
            .AllowAnonymous();

        app
            .MapHealthChecks("/health/startup", new HealthCheckOptions { Predicate = c => c.Tags.Contains(Ready) })
            .AllowAnonymous();

        return app;
    }

    // The options, because they are what the predicates above are evaluated against.
    private static bool AnyReadinessCheck(IEndpointRouteBuilder app)
    {
        HealthCheckServiceOptions options = app.ServiceProvider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value;

        return options.Registrations.Any(r => r.Tags.Contains(Ready));
    }
```

**An empty predicate set is a passing predicate set**, so a host that
registers no readiness checks answers `/health/ready` with 200 without having
verified anything — and [§15.1](15-cicd-deployment.md) removes the smoke stage
by name, on the grounds that this probe already gates the rollout. "Forgot to
wire it up" and "gates readiness on nothing" therefore look identical from
outside, and only one of them is a deploy that should proceed.

The rule that separates them — **a host with a connection string has a
readiness check, and a host without one does not** — is mechanised by the
guard above rather than left as prose. The **gateway** owns no database
(§4.2), so it declares its empty set at the call site and an absence becomes
a written decision. Every other host fails to start: each service owns a
schema, including the two with no public API, since Shipping and
Notifications both ship a migrator and both register a SQL check (§4.1,
[§3.2](03-bounded-contexts.md)), and so does the BFF, whose projection is a
schema of its own
([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)).
The BFF's readiness set is its own SQL and the bus, `sql` and
`masstransit-bus`, and Catalog's hop is kept out of it, because a peer's
outage must not take its caller out of rotation.

**The guard tests the set, not any member of it**, and the bound is stated
rather than left to be discovered, because a guard read as stronger than it is
buys a confidence nobody checked. `AnyReadinessCheck` asks whether *one*
registration carries the tag, so a service that loses its `AddSqlServer(...)`
while keeping its Redis and broker checks starts exactly as before. What it
catches is the readiness set going missing whole — a host wired up with none at
all, or one whose Infrastructure registration stopped being called. The
narrower case would take a per-service count, and nothing generates one for a
service [§4.5](04-solution-structure.md)'s scaffold has rendered.

**Failing to start is the right direction, and the restart-storm argument
below is why.** That rule forbids gating *liveness* on a dependency because a
running process should not be killed for something outside it, which a
database outage is; a missing readiness registration is inside it, is true at
every start, and never resolves by waiting. So the cases the two rules cover
do not overlap, and the failure they each avoid is the same one: a pod that
takes traffic it cannot serve. A host that refuses to boot is held out of the
rollout by the deployment controller before any traffic is routed to it, which
is the answer a readiness probe would have given had it been wired up — and
with §15.1's smoke stage gone, this probe is the last thing standing between a
wiring mistake and production traffic.

| Endpoint | Question | On failure |
|---|---|---|
| `/health/live` | Is the process alive? | Kubernetes restarts the pod |
| `/health/ready` | Can it serve traffic? | Removed from the load balancer, not restarted |
| `/health/startup` | Has it finished starting? | Liveness probing is deferred |

**Liveness must not check dependencies.** If liveness checks the database, a
brief database outage restarts every pod simultaneously, and the restart storm
outlasts the outage. Liveness answers only "is this process wedged?".

**A background service's escaped exception stops the host, by choice.**
`AddCommonWebDefaults` sets `BackgroundServiceExceptionBehavior.StopHost`, so
the orchestrator restarts the pod. Each worker's per-pass catch is the first
line; an exception that escapes it is a defect, and a host that ignored it
would leave a worker silently stopped behind a liveness probe still answering.

**Readiness must not check the outbox backlog either.** A growing backlog means
events are not being *delivered*; the service can still accept commands and
serve queries perfectly well. Gating readiness on it means a RabbitMQ blip pulls
every pod out of the load balancer and converts a delivery delay into a total
outage — the failure amplifying exactly when the system is already degraded.
The outbox is a set of gauges, scraped and alerted on (§13.6), and deliberately
not a health check at all — nothing registers one, so no probe can select it.

## 13.6 What to alert on

Alert on symptoms users experience, not on causes. Each alert should be
actionable — if the response is "acknowledge and ignore", delete it.

| Alert | Condition | Why | Runbook |
|---|---|---|---|
| Error rate | 5xx > 1% over 5 min | Users are seeing failures | `error-rate.md` |
| Latency | p99 > 1 s over 10 min | Users are waiting | `latency.md` |
| Error queue depth | > 0 | A business process has stopped — and one arrival is a saga's deliberate escalation rather than a fault ([ADR-025](adr/ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md)): a `PaymentAuthorised` for an order whose instance has already been finalised correlates to nothing, so §9.6 faults it here instead of letting it be consumed cleanly and gone. Money that moved on an order nothing is tracking is what this queue is for. The depth cannot tell that arrival from a broken consumer, so the faulted message's type is what triage reads first | `error-queue.md` |
| Skipped queue depth | > 0 | An endpoint was handed a message it has **no consumer for**, and MassTransit parked it in `<queue>_skipped`. Nothing threw and nothing will retry it, so the business fact is lost as quietly as an unwatched queue can lose one. Deliberately not folded into the row above with a `.+_(error\|skipped)` selector: an error-queue message is one a consumer accepted and could not finish, this is one no consumer would take, and the two are triaged from opposite ends — replay is the right first move here and the wrong one there. **It fires on a correctly ordered release exactly never**, which is what makes it worth paging on and what makes [§9.2](09-messaging.md)'s consumer-before-producer rule enforceable rather than advisory | `skipped-queue.md` |
| Queue backlog | a working queue above 1000 messages **and rising** over 10 min | A consumer is not keeping up, or there are too few of it. Both halves are required, as *Outbox growth* below requires both: a deep queue that is draining needs nobody. The dead-letter queues are excluded because the two rows above own them and are triaged from the opposite end. **It sees a receive endpoint and nothing past it**, so work a host keeps in its own tables — Shipping's fulfilment and tracking passes — is outside it, and §15.3 names the gauge that watches that work instead. | `queue-backlog.md` |
| Delivery lag | `messaging.delivery.lag` p95 above §13.7's event end-to-end target over 10 min | The same condition read from an event consumer's end: events reach it late. A ticket for the backlog's reason — a late message is still on the broker, and one that fails for good reaches `_error`, which the error-queue row pages on — and it shares that runbook, because both are answered by deciding whether arrival rose or service fell. **Late is not always slow**: each in-memory retry records the message again from its original `OccurredAt`, so a failing handler raises it too, and the runbook reads the error rate before anything is scaled. It can be read at all because `MessagingMetrics` exports the target as a bucket bound (§13.3) | `queue-backlog.md` |
| Unattributed order | `bff.orders.unattributed` above 15 min | An order the BFF's projection holds payment or shipment facts for and no buyer, so [§10.7](10-api-gateway.md) returns it to nobody: one buyer's history is missing an order, with no error, no 5xx and no lag anywhere ([ADR-051](adr/ADR-051-the-buyers-order-read-is-a-projection-in-the-bff.md)). **Fifteen minutes is past every cause that resolves itself or raises its own row first**: a payment event beating `OrderPlaced` resolves inside §13.7's event end-to-end target, and an Ordering broker-lane stall, a backlog on the BFF's queue and a message in its `_error` queue each raise their own alert first. What is left is the silent case: an Ordering event published before the queue existed, or older than a rebuild reached. A ticket: the order exists in Ordering, and checkout is untouched | `unattributed-order.md` |
| Address read refused | any `shipping.address.refused` in the last 30 min, longer than a row's longest backoff | Shipping's read of an order's delivery address was refused by the identity provider, by the worker's own grant check, or by Ordering ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)), so every shipment with no stored address backs off and none leaves. An increase, because the counter only grows; a page, because no retry fixes a credential or a grant | `address-refused.md` |
| Contact read refused | any `notifications.contact.refused` in the last 30 min, longer than a row's longest backoff | The address row's form for Notifications' own owner read: the send worker's read of a customer's contact was refused by the identity provider, by the worker's own grant check, or by Keycloak's admin API ([ADR-052](adr/ADR-052-a-contact-is-read-from-its-owner-by-a-worker-and-kept-in-the-readers-own-table.md)), so every notification whose customer has no fresh stored contact backs off and none is sent. The window is argued against `SendClaims`' backoff, which climbs the same `OutboxDispatcher` ladder as Shipping's; a page, because a refused read is never answered from a stored contact | `contact-refused.md` |
| Saga age | any saga unfinalised > 1 h **outside `Confirmed`**, or > 4 days **in** it | Orders are stuck. **The margin is argued against the longest wait rather than the set of them**, because a list of thresholds is the half that rots: the shortest in §9.6 is the five-minute reservation wait and the longest is a payment verdict at **thirty minutes** — armed at fifteen when `AuthorisePayment` is sent, and re-armed once when `Compensating` inherits it unanswered ([ADR-025](adr/ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md)) — so an hour still clears every one of them. The waits also compose along a path, and the margin survives that too. But the despatch wait is **three days** by design, three orders of magnitude further out, and an unqualified hour would page on the healthy path for most of a saga's real lifetime. A despatch that genuinely expires escalates to the row below, not to this one. **The state is named rather than described**: §9.6 has no `AwaitingDespatch`, because the saga arms `DespatchTimeout` on the transition *into* `Confirmed` — the order is confirmed and now waiting on Shipping. A selector spelled the way the description reads would match no series, exclude nothing, and page on every healthy confirmed order. **The four-day branch is not a refinement but the other half of the alert**: excluded outright, a `Confirmed` saga whose three-day timeout was never delivered is invisible here *and* to the row below, because that timeout is what creates the review row. Nothing would page at all | `stuck-saga.md` |
| Orders awaiting review | any row in `ordering.OrderReviews` older than 1 h | A saga escalated work the Ordering workflow cannot finish itself (§9.6) — a wait it could not compensate, an authorisation that landed while compensation was already under way, or a **confirmation** that did. **"Cannot finish itself" is not "has no contract to do"**: §3.2 gives Payments both a `Refund` aggregate and `OrderCancelled` to act on — what Ordering lacks is a way to *ask*. **Nor does every door follow a cancellation**: on two of the three none has happened yet, and **the second is not necessarily a customer cancelling**: compensation begins on a cancellation, a decline or the fifteen-minute payment timeout alike, so a slow PSP raises that row with nobody having cancelled anything. **Whether the saga has finalised is a property of the BRANCH that raised the row, not of the row and not reliably of the reason**: most are raised on the way out, while `payment_authorised_during_compensation` and `cancelled_after_confirmation` are raised mid-wait from `Compensating` and can sit beside a live instance. The reason alone is not enough because `cancelled_after_confirmation` is raised from `Confirmed` too, where it finalises, so one code spans both answers. **The branch is not quite enough either, and the narrowing runs in both directions** ([ADR-025](adr/ADR-025-a-saga-state-that-waits-on-two-services-finalises-on-neither-alone.md)): `Compensating` finalises on a join — the stock half settled *and* no payment verdict outstanding — so the branch raising `payment_authorised_during_compensation` finalises in the same transition when the stock half has already settled, and leaves the instance standing when it has not. "Raised mid-wait" does not imply a live instance, and a raised row does not imply a wait. What holds is the predicate: a branch whose `Finalize` is absent or conditional can outlive its row's first hour, and the instance is what says which. **Those are the cases the saga-age alert can reach, and it will not usually reach them either**: both thresholds are an hour, and every wait that can hold `Compensating` open ends well inside one — the release wait at ten minutes, and the payment verdict at thirty from `AuthorisePayment` — so the instance is normally gone before this row is old enough to page. The two coincide when a wait failed to arrive at all, which is why this row exists rather than being folded into the one above | `order-review.md` |
| Migration job failed | Helm `pre-install,pre-upgrade` hook non-zero, or a release stuck pending | The deploy stopped before any pod rolled ([§7.4](07-persistence.md)). On an **upgrade** the previous version is still serving, which is why nothing else fires — so this alert is the only signal. On a first **install** there is no previous version and no pod at all, so nothing else fires for the opposite reason: check which before promising availability | `migration-failure.md` |
| Cache hit ratio collapse | hits ÷ (hits + misses) < 50% over 10 min. **The expression lives in the rule file, not here** — see below | Redis lost its working set; every miss becomes a database read, and the databases are sized for a warm cache (ADR-006) | `redis-cold.md` |
| Business volume | `orders.placed` per hour drops > 50% vs the same hour last week | The most valuable alert here — it catches failures no technical metric detects. §6.6's worked case: `ordering.ProductPrices` has no row for a product, every order containing it is **refused by the domain**, and the result is a 422 `order.products_unavailable` the customer sees, no exception, no 5xx and no lag. **Not a 400, and the difference is where the on-call looks**: the request is well-formed and the validator passed it (§10.5 maps `Error.Rule` to 422, `ValidationException` to 400), so a 400 dashboard shows a path this request never took. Week-over-week rather than a fixed floor, because a volume alert without a seasonality model is the first pager people mute | `business-volume.md` |

**`shipping.shipments.overdue` carries no rule yet, by decision.** A threshold
on how long a due shipment has waited wants a baseline under load, and none
exists until §13.7's load run is scheduled or declined
([#434](https://github.com/alexander-shamray/blueprint-backend/issues/434)).
Until then it, `shipping.shipments.waiting` and `shipping.carrier.unavailable`
are read by `queue-backlog.md`'s procedure for a worker whose wait is outside
a consumer, and the address refusal above is the one Shipping signal that
needs no baseline, because any refusal is a fault.

### Outbox alerts are per lane

The two outbox lanes (§9.4) fail for different reasons, produce different
symptoms, and need different people. A single "outbox backlog" alert averages
them into something nobody can act on.

The runbook column is not decoration. §13.9 requires every alert to have one,
and the pairing is checkable in both directions: an alert with no runbook is a
3 a.m. page with no procedure, and a runbook with no alert is a procedure
nobody will be told to follow.

| Alert | Condition | Symptom | Likely cause | Runbook |
|---|---|---|---|---|
| **Broker lane stalled** | `outbox.oldest.age{lane="Broker"}` > 2 min | *Other services* are working from stale data; sagas stop advancing | Broker unreachable, credentials expired, queue at its length limit, network policy change | `outbox-broker.md` |
| **Local lane stalled** | `outbox.oldest.age{lane="Local"}` > 30 s | The read models this service feeds from its **own** events are stale — users see missing or outdated list data. Not the ones another service's contract feeds: those never touch this lane, and §13.7 records that their staleness has no direct signal yet | A projection handler throwing, read-model deadlock, schema drift after a migration | `projection-lag.md` |
| **Outbox growth** | `outbox.pending.count` > 1000 and rising over 10 min, replicas deduplicated | Either lane, not keeping up | Dispatcher not running, batch size too small for load, a slow deliverer. **Not a failed purge** — this gauge counts `ProcessedAt IS NULL` and the purge deletes *processed* rows, so a stopped purge grows the table without moving this number | `outbox-growth.md` |
| **Abandoned rows** | `outbox.abandoned.count` > 0, per lane | Permanent data loss, disguised as an ordinary stall | A message that will never be delivered and is no longer being retried. The `lane` tag says whose loss: `Broker`, and other services never learned something; `Local`, and this service's read model is permanently wrong | `outbox-abandoned.md` |

Thresholds differ by an order of magnitude because the lanes have different
floors. The local lane is in-process with no network hop, so 30 seconds of lag
already means something is wrong. The broker lane crosses a network and should
absorb a short RabbitMQ blip or a rolling broker restart without paging anyone.

> **Alert on abandoned rows specifically.** The dispatcher claims rows below
> `OutboxDispatcher.MaxAttempts` (§9.4), so a row that reaches the cap is
> silently skipped forever, and without this alert permanent loss of a
> business event has no signal of its own.
>
> **What that looks like depends on how the other two gauges count, and this
> platform's count it in.** The classic failure is a backlog that excludes
> abandoned rows: it drains to zero and the graph goes green *precisely
> because* the message was given up on. `OutboxStats.PendingCount` and
> `OldestAgeSeconds` filter on `ProcessedAt IS NULL` alone, so an abandoned row
> keeps both non-zero instead — which trades that failure for a quieter one:
> permanent loss then looks exactly like an ordinary stall, and the on-call
> works `outbox-broker.md` for ever. **Either way the abandoned gauge is what
> tells the two apart**, which is the point; only the disguise changes.

**These three gauges read the database, not the pod, so aggregate with `max`
and never `sum`.** Every replica of a service exports the same table-wide
number — §15.3's Ordering chart runs three — so `sum` reports three times the
backlog and trips a thousand-row threshold at about 334 real rows. Sum *across
lanes* if a total is wanted, but deduplicate replicas first:

```promql
sum by (service_name) (max by (service_name, lane) (outbox_pending_count))
```

This is the one aggregation rule in this chapter that a reader will get wrong
from habit, because summing a counter across replicas is right almost
everywhere else. A gauge whose value is a property of a shared resource is the
exception.

All three gauges carry `lane`, so one query serves both lanes and every alert
above can say which one it is talking about. The constructor and its per-lane
read, from `OutboxMetrics.cs` in
`src/Services/Ordering/Ordering.Infrastructure/Observability/`:

```csharp
    public OutboxMetrics(IMeterFactory factory, IOutboxStats stats, ILogger<OutboxMetrics> logger)
    {
        Meter meter = factory.Create(MeterName);

        meter.CreateObservableGauge(
            "outbox.oldest.age",
            () => PerLane(stats.OldestAgeSeconds, logger),
            unit: "s",
            description: "Age of the oldest unprocessed row, per lane.");

        // The growth alert needs a count, which the age gauge cannot supply (§13.6).
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => PerLane(lane => stats.PendingCount(lane), logger),
            unit: "{message}",
            description: "Unprocessed rows, per lane.");

        // Per lane matters most here: a Broker abandonment and a Local one differ in blast radius and recovery.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => PerLane(lane => stats.AbandonedCount(lane), logger),
            unit: "{message}",
            description: "Rows past the attempt cap, per lane.");
    }

    /// <summary>Lanes from the enum, so a new lane cannot be left without a gauge.</summary>
    /// <remarks>Contained, since the collector abandons its pass on an exception (§13.6).</remarks>
    private static List<Measurement<double>> PerLane(Func<OutboxLane, double> read, ILogger logger)
    {
        List<Measurement<double>> measurements = [];

        foreach (OutboxLane lane in Enum.GetValues<OutboxLane>())
        {
            double value;

            try
            {
                value = read(lane);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Every lane is dropped: one missing from a `max by (lane)` reads as a healthy zero, not as no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(value, Tag(lane)));
        }

        return measurements;
    }

    /// <summary>The enum's name, as the <c>Lane</c> column stores it, so SQL, C# and PromQL agree.</summary>
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());
```

`MeterName`, declared above it in the same file, is the contract with §13.2's
`AddMeter`: an instrument on an unregistered meter is collected by nothing and
alerted on in vain. The `lane` tag is the enum's own name, as the `Lane`
column stores it and §9.4's dispatcher compares against it, because a
hand-written lowercase tag would give one value three spellings across SQL, C#
and PromQL, and an alert querying the wrong one matches no series and never
fires, which looks exactly like health. `PerLane` reads the lanes from the
enum rather than from a list written out at each call site, so a lane added
to `OutboxLane` cannot be left without a gauge and therefore without an alert.

**The read is contained, and the loop is what contains it.** An observable
callback that throws does not fail alone: `RecordObservableInstruments`
propagates and abandons the rest of the pass, so one failing read could stop
unrelated observable instruments being collected. Every lane is dropped rather
than only the failing one, because a lane missing from a `max by (lane)` reads
as a healthy zero rather than as no data. `GaugeReadFailed`, also declared
above, is the only thing that separates a contained failure from a healthy
quiet lane, because both are an absent series.

> **Containment answers a transient outage; the log is what answers a permanent
> one.** An absent series is the right reading of a database that is briefly
> unreachable — an outbox alert firing for that would page the wrong person
> with the wrong runbook. It is the *wrong* reading of schema drift or a
> revoked grant, where every read fails for ever, all four outbox alerts go
> quiet, and the service stays ready: §13.5's check proves the connection opens
> and nothing about this table. An empty outbox dashboard is then
> indistinguishable from a healthy one, which is this section's own callout
> arriving through the instrument. **An alert on the absence itself is what
> would close it, and is owed** — a further alert, a further runbook and
> a row in §13.6's table, on the same terms as the alerts this chapter already
> ships unloaded.

`IOutboxStats` is read from a singleton on the collector's schedule, so it must
not hold a `DbContext` — a metrics singleton that captured a scoped one would
hold a connection open for the life of the process — and it satisfies that by
never asking for one. §6.5's `IDbConnectionFactory` is itself a singleton
holding a connection string, so it is injected directly and the reads are
Dapper on a connection the caller disposes. Every member takes the lane,
because each question has a different answer per lane and a different runbook
behind it. The port is `IOutboxStats.cs`, beside `OutboxMetrics.cs`:

```csharp
/// <summary>The three questions §13.6's gauges ask of the outbox table, each per lane.</summary>
/// <remarks>The lane is not optional: the two lanes fail differently and need different people (§13.6).</remarks>
public interface IOutboxStats
{
    /// <summary>Seconds since the oldest unprocessed row, or zero when the lane is empty.</summary>
    double OldestAgeSeconds(OutboxLane lane);

    /// <summary>Unprocessed rows on this lane, abandoned ones included.</summary>
    int PendingCount(OutboxLane lane);

    /// <summary>Unprocessed rows past §9.4's attempt cap, which will never be delivered.</summary>
    int AbandonedCount(OutboxLane lane);
}
```

`OutboxStats.cs`, in the same folder, answers the three with aggregate
queries composed from the registered `OutboxTable` rather than written
literally, because a second literal in code is a second place the schema has
to be right. The lane predicate is on all three, because an untagged gauge
cannot answer the first question its runbook asks, and the abandoned count
reads the dispatcher's cap rather than writing it again: §9.4 claims rows
below it, this counts the rows at or above it, and two copies of one number
stop agreeing on the day somebody tunes it. The third query, from the
constructor:

```csharp
        // The dispatcher's cap (§9.4), not a copy that could drift from the loop it describes.
        _abandonedSql =
            $"""
            SELECT COUNT(*)
            FROM {table.QualifiedName}
            WHERE ProcessedAt IS NULL
                AND Lane = @lane
                AND Attempts >= {OutboxDispatcher.MaxAttempts};
            """;
```

Every question goes through one read, cached for `CacheFor` because the
collector polls every few seconds and these are aggregate queries, not free —
a metrics type that loads the database it is measuring is a monitor causing
the symptom. **Both timeouts are bounded, and neither is optional.** These
reads run inside observable gauge callbacks on the metric reader's own thread,
so an unbounded wait stalls the reader and takes unrelated telemetry down with
these gauges. `CommandTimeoutSeconds` bounds the statement;
`ConnectTimeoutSeconds` bounds the open, through the connection string the
registration below builds, because a command timer does not start until a
connection is open and SqlClient waits fifteen seconds for that by default.
One without the other is a safeguard that is stated and absent:

```csharp
    /// <summary><c>double</c> throughout: the age is one, and a count is widened for the gauge anyway.</summary>
    private double Read(string key, string sql, OutboxLane lane) =>
        _cache.GetOrCreate(
            key,
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheFor;
                using IDbConnection connection = _connections.Create();

                // MIN over an empty lane is NULL; COUNT never is, so one coalesce serves both.
                return connection.ExecuteScalar<double?>(
                    new CommandDefinition(
                        sql,
                        new { lane = lane.ToString() },
                        commandTimeout: CommandTimeoutSeconds)) ?? 0;
            });
```

> **Two gauges, because they answer different questions and fail differently.**
> `outbox.oldest.age` catches a lane that has *stopped*; `outbox.pending.count`
> catches one that is *falling behind*. Neither substitutes for the other: a
> single stuck row pins the age gauge at hours while the count stays at 1, and
> a backlog of ten thousand rows all seconds old leaves the age gauge flat.
> The alerts in the table read one each, which is why both exist.

Registration is the step that makes any of this exist, and it is the step
`ValidateOnBuild` cannot check — nothing depends on a metrics class, so the
container is happy without it (§6.2). Both of Infrastructure's metrics types
are registered in `AddOrderingInfrastructure`, from
`src/Services/Ordering/Ordering.Infrastructure/DependencyInjection.cs`:

```csharp
        // §13.3's messaging instruments.
        services.AddSingleton<MessagingMetrics>();

        // §13.6's outbox gauges. OutboxStats reads the runtime key's data plane (§7.1), and runs in gauge callbacks,
        // so it gets its own bounded connect timeout, which no query inherits.
        string metricsConnectionString =
            new SqlConnectionStringBuilder(configuration.GetConnectionString("Ordering"))
            {
                ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
            }.ConnectionString;

        services.AddSingleton<IOutboxStats>(sp =>
            new OutboxStats(new SqlConnectionFactory(metricsConnectionString), sp.GetRequiredService<OutboxTable>()));
        services.AddSingleton<OutboxMetrics>();

        // Constructs the metrics singletons at start, before the bus, so they exist for the first message (§13.6).
        services.AddHostedService<MetricsInitialiser>();
```

`OrderMetrics` and `RequestMetrics` are not registered there: they are
Application types, and `AddOrderingApplication` registers them (§4.2), once. A
second `AddSingleton` would not fail. The container keeps both descriptors and
resolves the last, so it is dead weight that a reader takes for a second
registration, and only a resolution of `IEnumerable<OrderMetrics>` would build
a second instance.

Singleton registration alone is lazy: the instruments appear on first
resolve, which for a class nothing injects is never. `MetricsInitialiser`
forces construction at startup, for every one that exists, and it is
registered by Infrastructure, which may reference Application; the reverse
would not compile. It is `MetricsInitialiser.cs`, beside `OutboxMetrics.cs`:

```csharp
/// <summary>Builds every metrics type at startup: an instrument never constructed does not exist (§13.6).</summary>
/// <remarks>
/// A type belongs if the service can run for an hour without constructing it, as <see cref="OrderMetrics"/> can
/// while no order arrives. Public for the reason <c>Program</c> is (§4.2).
/// </remarks>
public sealed class MetricsInitialiser : IHostedService
{
    /// <summary>Resolving the parameters is the whole job; the guards make it a read (§13.6).</summary>
    public MetricsInitialiser(
        OutboxMetrics outbox,
        MessagingMetrics messaging,
        RequestMetrics requests,
        OrderMetrics orders)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(orders);
    }

    // `cancellationToken`, not `ct`: CA1725 keeps the interface's name, an error under ADR-019.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

> **The class is shaped by two rules that
> [ADR-019](adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md)
> makes errors.** **CS9113** — *parameter is unread* — fires on every
> parameter of a primary constructor that only exists to be resolved, and a
> discard-looking name does not escape it: `_` in a primary constructor is an
> ordinary parameter, not a discard. **CA1725** rejects `ct` against
> `IHostedService`'s `cancellationToken`.
>
> The guards are not ceremony bought to satisfy a compiler. A null here would
> mean the container resolved a metrics type to nothing, which is precisely the
> silent-instrument failure this class exists to prevent.

**`OrderMetrics` is in that constructor because a replica can run without
ever building it.** §13.3 puts it in `Ordering.Application` with
`OrderSummaryProjection` as its only call site, so without the initialiser
nothing builds it until an order event reaches the replica — raised there,
which makes the projection registry resolve the handler, or claimed from
the outbox — and a replica can run for an hour with none. And
nobody has to remember to add it: the test below reads the container's
registrations rather than this list, so an unforced metrics type fails a
build the day it is registered.

**The test for membership is not "is it a gauge".** It is *"can this service run
for an hour without constructing it"* — and for every metrics type in this
document the answer is yes, which is why each belongs in that constructor as it
comes to exist. **All four are there.**

That includes `RequestMetrics`, and the reasoning that would exclude it is the
worked example. `LoggingBehavior` injects it, a behaviour
runs on every dispatched request, and it is tempting to conclude that any live
service has therefore constructed it. It has not. `IPipelineBehavior` runs for
what `IDispatcher` handles; a health probe is mapped by `MapHealthChecks`
(§13.5) and never enters the pipeline. A canary before cutover, a replica behind
a rate limiter, or a service whose traffic has simply stopped all publish
nothing — and **Notifications and Shipping have no public API at all** (§3.2),
so on those two the instrument would never exist under any circumstances.

`OrderMetrics` is the second worked example, and a different failure. Its call
site is the projection (§6.6), not the handler that places the order, so no
command constructs it — and a call site that moves can move a type into this
list without anybody editing this list. **A constructor parameter is a
dependency on a call site that may move**, which is why the rule is about
reachability and not about instrument type.

Reachability is not decidable from a type, so no test can assert the rule as
stated. What a test *can* do is refuse to let a metrics type appear without
somebody deciding, and make the decision the thing under review. From
`tests/Ordering.Api.Tests/MetricsRegistrationTests.cs`:

```csharp
    /// <summary>Types deliberately not forced, each with the reason its instrument can go unbuilt.</summary>
    private static readonly Dictionary<Type, string> NotForced = [];

    [Fact]
    public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
    {
        // The collection, not a built provider, which cannot enumerate its registrations.
        Type[] registered =
        [
            .. BuildServices()
                .Select(d => d.ServiceType)
                .Where(t => t.Name.EndsWith("Metrics", StringComparison.Ordinal))
                .Distinct()
        ];

        HashSet<Type> forced =
        [
            .. typeof(MetricsInitialiser)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(p => p.ParameterType)
        ];

        // Both directions. Unforced-and-unexplained is the drift this exists
        // for; forced-but-unregistered is a host that will not start.
        registered
            .Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
            .ShouldBeEmpty("add it to MetricsInitialiser, or to NotForced with a reason");

        forced.ShouldBeSubsetOf(registered);
    }
```

`NotForced` is empty, and a name lands there only when someone argues it in a
pull request. `BuildServices`, in the same file, runs both
`AddOrderingApplication` and `AddOrderingInfrastructure`, which matters here
and nowhere else: the types are split across the two, and a helper that ran
only one half would see a subset and fail against a correct
`MetricsInitialiser` — the test reporting a defect in the thing it is
guarding.

> **The naming filter is a heuristic, and it is worth being honest about which
> way it fails.** The `Metrics` suffix is how the test finds candidates, so a
> metrics type named something else is invisible to it — a false negative, and
> the same silent gap the test was written to close. It never produces a false
> positive that forces a wrong decision, because `NotForced` is the escape
> hatch: a type that genuinely does not need forcing gets a line and a reason
> rather than a spurious constructor parameter.
>
> That asymmetry is deliberate. A convention test that can *block* a correct
> design gets disabled within a month; one that can only miss something is a
> net gain, and the missed case is caught by the same review that named the
> type.

`lane` is a two-value tag, so this respects the cardinality rule in §13.3.

> **An alert has three parts: a condition, a signal and a procedure.** §13.9
> pairs conditions with procedures in both directions. This is the third leg —
> every condition above resolves to instruments on a meter §13.2 registers.
> An alert written against a signal that does not exist looks correct: the
> dashboard is empty either way, whether the system is healthy or the metric
> was never published.
>
> Where a condition is **derived** rather than measured — the cache hit ratio
> is computed from HybridCache's hit and miss counters, not published as a
> ratio — write the expression, not an invented metric name. A name that looks
> like an instrument and is not is the hardest version of this to spot.

### Three of them have no signal yet

Three of the conditions above read an instrument nothing publishes. That is
this section's own callout coming true, and it is recorded here rather than
resolved by quietly shipping rules that cannot fire:

| Alert | What is owed |
|---|---|
| Saga age | A gauge over `ordering.OrderFulfilmentStates`. §9.6 persists every saga, so the reading is a query away — there is simply no instrument over it |
| Orders awaiting review | A gauge over `ordering.OrderReviews`, which the `IX_OrderReviews_RaisedAt` index already exists for |
| Cache hit ratio collapse | **An instrument, and only an instrument — see the callout below.** §13.2 registers the `Microsoft.Extensions.Caching.Hybrid` meter and the package publishes no meter at all |

**The third row is the one worth pausing on**, because it is the failure mode
this section warns about wearing its best disguise: the `AddMeter` line makes
the signal look wired, and a reviewer checking "is the meter registered" gets a
yes. A registered meter with no publisher is as silent as an unregistered one.

Their rules live in `deploy/observability/alerts/awaiting-signal.yaml`, which is
**not loaded**, and `deploy/observability/check.py` asserts in both directions:
every loaded rule's metric is published, and every awaiting rule's metric is
published by *nothing*. The second is what makes the list self-clearing — the
day one of these instruments lands, the gate goes red and names the rule to
move. Every runbook exists regardless, per §13.9.

> **The cache row is the one the self-clearing claim does not cover.** The
> other two are owed a `Create*` call the gate can see in `src/`. This one is
> owed an instrument, and a consumer is necessary and not sufficient: services
> call `AddRedisConnections`, and the row is still unpublished.
>
> **`Microsoft.Extensions.Caching.Hybrid` 10.0.0 publishes no `Meter`.** The
> assembly references `System.Diagnostics.Tracing` and not
> `System.Diagnostics.Metrics`: it reports through `HybridCacheEventSource`
> with `PollingCounter`, which is EventCounters. So the `AddMeter` line in
> §13.2 collects nothing — **the registered-name trap this section warns
> about, in this platform's own configuration**, and the name looks exactly
> like an instrument. The line stays, because deleting it would hide the
> obligation where naming it records one.
>
> A gate on the consumer would be worse than the gap: it would read the
> wiring of Redis as a published signal, somebody would move the rule into the
> loaded file, and it would sit there silent. What the row is owed is an
> **instrument** — an EventCounters bridge written here, which check 5 would
> see, or a package that publishes a meter, **which no gate in this repository
> can observe.** That second half is a residual, named rather than implied.

### The gap is per service as well as per metric

A fourth absence sits underneath all of this and is invisible to the checks
above, because they are about metric *names*. **The four loaded outbox alerts
group `by (service_name)`, and a service that hosts §9.4's dispatcher and
registers no `OutboxMetrics` is absent from every one of them.** Which
services those are, if any, is `OUTBOX_METRICS_EXEMPT` in
`deploy/observability/check.py`, and a stalled lane in one of them would be
precisely the silent case this section exists to prevent — arriving through a
service missing from a series rather than through a metric nobody declared.

§13.3 places `OutboxMetrics` in each publishing service's `*.Infrastructure`,
so the gap closes one service at a time, by registering the type and paying
for it with a copy. **The template closes it for every publishing service
after it**: Catalog, which §4.5's scaffold renders, registers the gauges, and
the scaffold copies them and writes the service's `AddMeter` line — so a
rendered publisher publishes them from its first boot, a pure consumer hosts
no dispatcher to owe them, and the exemption list holds a service only when
somebody writes its reason down.

What the gate adds is that an absence cannot be quiet. `check.py` requires
every service hosting the dispatcher to publish the gauges **or** to be on a
declared exemption with a reason, and it fails in both directions — a new
unexempted service, and a stale exemption for one that no longer needs it.
**A gap somebody argued is not the same as a dashboard nobody noticed was
empty**, and that distinction is the whole of what is being bought.

## 13.7 Starting SLOs

Alert thresholds without targets are arbitrary. These are **starting points** to
be replaced by measured behaviour within the first month — publishing them
matters more than their initial accuracy, because they make "is this slow?" a
question with an answer.

**Every row names the instrument it reads**, for the reason §13.2 gives: a
target whose signal is not registered cannot be measured, and reads as
satisfied. A row that cannot name one does not belong in the table.

| Metric | Target | Signal |
|---|---|---|
| Command p95 (single aggregate, excl. external calls) | < 100 ms | `request.duration`, `request` tag on a command type (§13.3) |
| Query p95 | < 80 ms | `request.duration`, `request` tag on a query type |
| Event end-to-end p95 (publish → consumer start) | < 2 s | `messaging.delivery.lag` |
| Outbox oldest unprocessed, **broker lane**, p99 | < 5 s | `outbox.oldest.age`, `lane` tag (§13.6) |
| Outbox oldest unprocessed, **local lane**, p99 | < 1 s | same gauge, other lane |
| Read-model staleness, **own events**, p99 | < 1 s | `projection.lag` |
| Availability, per service | 99.9% monthly | `http.server.request.duration`, ASP.NET Core instrumentation |

**The read-model row says *own events*, and the qualifier is what keeps it
honest.** `projection.lag` is recorded by `ProjectionInvoker` off an outbox
row, so it only ever measures a read model this service feeds from its own
domain events (§7.5). A read model fed by *another* service's contract never
touches the outbox at all — Ordering's `ordering.ProductPrices` is the worked
case (§6.6) — so `projection.lag` is empty for it.

> **Its producer is a registered `IProjectionHandler<T>`.**
> `MessagingMetrics.Projected` is called by `ProjectionInvoker` only after one
> succeeds, so a service with no projection declares the instrument and never
> writes to it — a registered name is not a live signal, as §13.6 records for
> the HybridCache meter. Ordering's is §6.6's `OrderSummaryProjection`, which
> every placed order reaches, so `deploy/observability/slo/slo.js` asserts
> this row against the orders its own run places.

**Broker-fed read-model staleness therefore has no SLO here, and the honest
move is to say so rather than to point at a row that nearly fits.** The
**event end-to-end** row above is the near miss: `IntegrationEventConsumer<T>`
records `messaging.delivery.lag` at the top of `Consume`, *before* it resolves
a handler, so the measurement stops where the projection starts. It never
waits for the SQL round trip, counts a retried message once per attempt rather
than once when it lands, and cannot tell a handler that fails terminally from
one that succeeds — which means that row can sit inside its two-second target
while `ordering.ProductPrices` is stale, or was never written at all. Adopting
it would restate this table's own defect one row over: a target that is met
while the thing it names is broken.

Closing it needs an instrument that fires *after* a broker-lane handler
commits — the `Projected` half of `MessagingMetrics` reaches only the local
lane, because `ProjectionInvoker` is its only call site. That is a
§13.3 change with a dashboard behind it, so it belongs with the observability
work rather than with a service's own change. Until then this is
a **named** gap, which is the same standing as the two rows left out below: an
SLO nobody can compute is worse than an absence somebody has written down.

Two rows are left out rather than left unmeasurable. **Gateway added latency**
would need the gateway's own duration minus the backend's, correlated per
request — no single instrument produces it, so any number published for it
could only be guessed at. **Query p95 split by cache hit and miss** needs a
tag no query handler sets. The question the split asks, whether the cache is
working, would be answered by the cache's own hit ratio, which has no signal
until the cache publishes a meter (§13.6's callout on
`Microsoft.Extensions.Caching.Hybrid`).

Cutting a row is the honest move when the alternative is a target nobody can
compute. An SLO that cannot be evaluated is not a weak SLO — it is a claim that
the service is meeting a bar nobody is checking.

Verify order-of-magnitude with the **k6 SLO run against staging**
([§15.1](15-cicd-deployment.md)) — `deploy/observability/slo/slo.js`, the load
run in CD, which asserts the five rows of this table it can evaluate — the two
request rows, the two outbox lanes and own-event staleness, with the other two
named below — and is the first real gate after the dev deploy.

**It drives with k6 and adjudicates with Prometheus**, and the split is forced
by this table rather than chosen. A load generator measures wall-clock at the
client, which includes the edge, TLS and the network; the first two rows read
`request.duration`, which is dispatcher entry to result. Asserting only the
client's number would fail a healthy service on a slow link, and asserting only
the server's would pass a broken edge with perfect handler timings — so k6's own
thresholds are a coarse guard and every row it *can* evaluate is checked by
querying that row's named instrument after the run.

**An absent series fails that run.** It is not read as "no problem observed",
for the reason §13.6 gives one section up: empty and healthy look identical.

**Two of the seven are not evaluated there, and each is named in the script
rather than quietly dropped.** Cutting a row rather than pretending is this
table's own rule, applied to the gate that reads it — and a gate that fails on
a healthy platform is a gate that gets switched off:

| Row | Why the run cannot evaluate it |
|---|---|
| Availability | A **monthly** objective; a three-minute run cannot compute one. The run bounds its own error rate instead, says so, and reports no pass for the row |
| Event end-to-end | `messaging.delivery.lag` is recorded by `IntegrationEventConsumer<T>`; the run places orders, and the consumer that records it handles Catalog's product events, which neither scenario produces |

The remaining five — the two request rows, the two outbox lanes and own-event
staleness — are what the run actually asserts. Not a "smoke test": §15.1
declines to have one and §12.1 gives the reason, which is that a stage named
for what it actually does gets maintained. This is also not a capacity test —
it catches the regression where a query loses its index and goes from 40 ms
to 4 s, which no unit test will find.

## 13.8 Ownership

| Artefact | Owner |
|---|---|
| Golden-signal dashboards (RED, saturation) | Platform |
| Business metric dashboards | The service team |
| Gateway 5xx, infrastructure alerts, **broker-lane** outbox stalls (usually a shared-broker fault) | Platform |
| *(the same 5xx condition on any other service)* | The service team — see below |
| Own p95, **local-lane** outbox stalls and projection lag, abandoned rows, consumer failures | The service team |

Dashboards are **code**, checked into `deploy/observability/` as Grafana JSON or
equivalent. A dashboard clicked together in a UI is lost with the instance and
cannot be reviewed.

**The 5xx row splits by service, and a single rule cannot carry that.** An
alert's owner travels as a label, and Alertmanager routes on labels — so one
rule with `owner: platform` sends every service's 5xx to Platform, which is this
table implemented as the opposite of what it says. `platform-alerts.yaml`
therefore ships `ErrorRateGateway` and `ErrorRateService`: one condition, two
selectors, two owners, one runbook between them. A rule *set* is the unit that
has to match this table, not a rule.

That directory holds the alert rules and the SLO run on the same argument, and
a gate over all three:

```
deploy/observability/
  alerts/       platform-alerts.yaml (loaded) and awaiting-signal.yaml (not)
  dashboards/   golden-signals.json, outbox.json
  slo/          slo.js — the k6 run of §13.7 and §15.1
  check.py      the gate; see deploy/observability/README.md
```

**Nothing here deploys Prometheus or Grafana.** §15.3's charts cover this
platform's own workloads, so how these files reach a running stack — a
`PrometheusRule`, a ConfigMap, a sidecar-discovered folder — is a decision no
chapter has taken. What the directory guarantees is that the content is
reviewed, versioned and internally consistent, which is the half a UI loses.
The local stack is the one place they are loaded: §14.1's `grafana` service
reads the loaded rule file and both dashboards at start, and
`deploy/observability/README.md` says how and what it leaves out.

The two dashboards follow the ownership split in the table above rather than
cutting across it: golden signals are Platform's, and the outbox dashboard
keeps the two lanes apart because averaging them produces a panel nobody can
act on.

## 13.9 Runbooks

Every alert links to a runbook. An alert that fires at 03:00 with no procedure
attached is a page to somebody who will have to reason from scratch.

| Runbook | Covers |
|---|---|
| `docs/runbooks/error-rate.md` | Triaging a 5xx spike: which service, which endpoint, correlating to a deploy or a dependency |
| `docs/runbooks/latency.md` | p99 regression: reading the trace waterfall, the usual suspects — a lost index, a cold cache, a slow peer |
| `docs/runbooks/business-volume.md` | Orders stopped: checking the gateway, auth, the outbox and the client before assuming it is real demand |
| `docs/runbooks/error-queue.md` | Inspecting a poison message, deciding replay vs discard, replaying safely |
| `docs/runbooks/skipped-queue.md` | A message an endpoint has no consumer for: reading the type off the headers, telling a producer that shipped ahead of its consumer from a binding that was never written, and why replay is right here and wrong one queue over |
| `docs/runbooks/queue-backlog.md` | A queue growing: telling arrival from service, the consumer that is not running at all, and what the alert cannot see — the worker whose wait is outside a consumer |
| `docs/runbooks/unattributed-order.md` | An order the BFF holds no buyer for: telling a late Ordering event from a lost one, Ordering's outbox and the BFF's error queue, repairing by the rebuild tool, and deleting a row nothing will attribute |
| `docs/runbooks/address-refused.md` | Shipping's address read refused: telling a rejected secret from a changed grant, an opaque token and a disagreement with Ordering, and the rows a long refusal gives up |
| `docs/runbooks/contact-refused.md` | Notifications' contact read refused: telling a refusal from a Keycloak outage, a rejected secret from a changed grant and an opaque token, and the notifications a long refusal gives up |
| `docs/runbooks/outbox-broker.md` | Broker lane stalled: checking RabbitMQ reachability, credentials, queue limits; what downstream services are missing while it is stopped |
| `docs/runbooks/projection-lag.md` | Local lane stalled: finding the throwing handler, deciding whether to serve from the write model meanwhile, replaying a projection from scratch |
| `docs/runbooks/outbox-growth.md` | Total backlog rising: dispatcher liveness, batch sizing, and the retention purge as a *diagnostic* rather than a cause — a stopped purge deletes nothing and this gauge counts only unprocessed rows, so it cannot have moved it |
| `docs/runbooks/outbox-abandoned.md` | Rows past the attempt cap: reading the payload and `LastError`, deciding repair vs discard, resetting `Attempts` to replay |
| `docs/runbooks/stuck-saga.md` | Finding unfinalised sagas, reading their state, manual compensation |
| `docs/runbooks/order-review.md` | Working the `OrderReviews` queue: what each reason code means, how to resolve it, and deleting the row when done |
| `docs/runbooks/migration-failure.md` | A migration job that failed mid-deploy, and how to roll forward |
| `docs/runbooks/redis-cold.md` | Cache-loss load spike on the databases, and how to shed load while it warms |

Write each one when the corresponding alert is created, not after it first
fires.

**The pairing is a gate, not a convention.** `deploy/observability/check.py`
fails the build on an alert whose `runbook_url` names a file that is not there,
on a runbook no alert points at, and on a runbook claimed by two alerts that
its `SHARED_RUNBOOKS` does not declare — each declared sharer carries its
reason beside it there, so another is argued for in that file rather than
added. This repository's rule for any gate holds here: the failure it exists
to catch is seen red, in both directions, before the gate is trusted.

`docs/runbooks/README.md` is the index and is excluded from the pairing by
name — one declared exception, so a second non-runbook file in that directory
has to be argued for rather than added.

**The one procedure with a statutory clock is outside that directory on
purpose.** A personal-data incident has no signal, so no alert can point at
its procedure and the pairing would refuse it;
[ADR-053](adr/ADR-053-a-jurisdiction-is-a-value-the-deployment-is-given.md)
places it beside [§11.7](11-identity-authorization.md)'s extension instead, as
[`docs/personal-data-incident.md`](../personal-data-incident.md), and the
runbook index points to it.

---

[← §12 Test strategy](12-test-strategy.md) · [Index](README.md) · [§14 Local development →](14-local-development.md)

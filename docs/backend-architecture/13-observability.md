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
single call every `Program.cs` makes (§4.2):

```csharp
public static IHostApplicationBuilder AddCommonWebDefaults(this IHostApplicationBuilder builder)
{
    builder.AddObservability();                           // this section

    builder.AddJwtAuthentication();                       // §11.3

    // The scheme and the policy arrive together because neither works
    // alone: a policy requiring an authenticated user, with no scheme
    // registered to authenticate one, rejects every request that reaches
    // it. This is the one policy every host shares and the only one
    // Common.Web may know — "is there a valid token". Permission policies
    // are per-service and are registered by the service (§11.4) or, for
    // the gateway, by the gateway.
    //
    // Deliberately identical to ASP.NET Core's default policy, which YARP
    // would accept as the magic string "default" (§10.2). Naming it costs
    // one line and buys a route file that says what it means — and that
    // file is read by people deciding whether a path is public.
    //
    // SetFallbackPolicy is what makes authorization deny-by-default. Without
    // it UseAuthorization evaluates NOTHING on an endpoint carrying no
    // policy metadata, so a new *Endpoints class that omits the one
    // RequireAuthorization line is reachable with no diagnostic — no
    // compiler error, no ValidateOnBuild failure, no failing test. The
    // fallback inverts that: the omission is a 401, and a public route has
    // to say AllowAnonymous, which is a line a reviewer can see.
    //
    // It reaches the gateway's proxied routes too, which is why
    // appsettings.json now names "anonymous" on catalog-public rather than
    // leaving the key out — a public path by omission and a public path by
    // decision read identically in a route file, and only one of them
    // survives someone else's edit.
    builder.Services
        .AddAuthorizationBuilder()
        .AddPolicy("authenticated", p => p.RequireAuthenticatedUser())
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

    // §11.4's port, paired with the accessor it depends on: ASP.NET Core
    // registers no IHttpContextAccessor by default, so omitting the first line
    // fails ValidateOnBuild rather than the first ownership check. Here rather
    // than in each service's Add*Infrastructure, where §4.2 had it until
    // PR-16 — every host that authenticates has a current user, and neither
    // type names a service.
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

    builder.Services.AddCommonProblemDetails();           // §10.5

    // Liveness only — it must not touch dependencies (§13.5), and Common.Web
    // has no connection strings anyway. Readiness checks are registered by
    // each service's own Infrastructure, which does.
    builder.Services.AddHealthChecks();

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

Note what is **not** here. `AddCommonWebDefaults` covers what every host needs
identically. Anything needing a connection string — the SQL, Redis, broker and
outbox checks in §13.5 — belongs in `AddOrderingInfrastructure`, because
`Common.Web` cannot know them.

```csharp
public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
{
    string serviceName = builder.Environment.ApplicationName;

    // OpenTelemetry becomes the ONLY logging provider, and that is a security
    // requirement rather than tidiness — see §13.4.
    builder.Logging.ClearProviders();

    // The scope half of §13.4, and it has to be registered rather than
    // configured: LoggerFactory takes an IExternalScopeProvider from the
    // container and hands the same instance to every provider, so wrapping
    // it here covers scopes opened by EF Core and MassTransit as well as
    // the platform's own two. IncludeScopes below is what puts them on the
    // record; without this line the redactor would be scrubbing attributes
    // beside a scope carrying whatever the caller sent.
    //
    // It WRAPS whatever is already registered rather than standing aside for
    // it. TryAdd was the first spelling and it fails open: a host that had
    // registered any provider first kept it, unwrapped, and every scope
    // exported raw while IncludeScopes stayed on and the redactor went on
    // scrubbing attributes beside it — a security control switched off by a
    // registration nobody looked at. The comment beside it was wrong in the
    // other direction too, claiming a later registration would be the one
    // ignored; the built-in container resolves the LAST, measured rather than
    // assumed.
    RedactingScopeProvider.WrapScopesForRedaction(builder.Services);

    builder.Logging.AddOpenTelemetry(logging =>
    {
        logging.IncludeFormattedMessage = true;
        logging.IncludeScopes = true;

        // §13.4's "never log a secret" rule, given a mechanism. Registered
        // here because this is the only logging pipeline the host has — a
        // redaction policy configured on a library nobody installed redacts
        // nothing, and reads in review as though it does.
        logging.AddProcessor(new SensitiveDataRedactor());
    });

    // A named local rather than an inline construction. The type has to be
    // spelled out where it is built, and spelling it inside AddAttributes' own
    // argument runs that line to 130 columns — past the 120 budget. Named here,
    // the declaration carries the type and `new` needs none.
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
            // Every meter an alert or SLO reads from. A condition whose signal
            // is not registered here cannot fire — it looks configured and is
            // silent, which is worse than having no alert at all.
            .AddMeter("Ordering.Orders")                       // §13.3, §13.6
            .AddMeter("Ordering.Outbox")                       // §13.6 per-lane
            // Shared names, not service-prefixed: every service emits the same
            // instruments and the service.name resource attribute separates
            // them. One dashboard query then works for all of them, and a new
            // service appears on it without anyone editing a panel.
            .AddMeter("Commerce.Requests")                     // §13.3, §13.7
            .AddMeter("Commerce.Messaging")                    // §13.3, §13.7
            .AddMeter("MassTransit")
            // §13.6's cache-hit-ratio alert, and it COLLECTS NOTHING at the
            // pinned version: Microsoft.Extensions.Caching.Hybrid 10.0.0
            // references System.Diagnostics.Tracing and not
            // System.Diagnostics.Metrics, reporting through
            // HybridCacheEventSource with PollingCounter. Measured against the
            // package. The line stays, with this comment, because deleting it
            // hides the obligation where naming it records one — see §13.6.
            .AddMeter("Microsoft.Extensions.Caching.Hybrid")
            .AddMeter("StackExchange.Redis"))
        .WithTracing(t => t
            .AddAspNetCoreInstrumentation(o =>
                o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
            .AddHttpClientInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddSource("MassTransit"))
        .UseOtlpExporter();

    return builder;
}
```

Two pieces of §13.2's telemetry are not in this block, and neither absence is
a mistake.
The rule is that an instrumentation lands with the package it instruments —
unlike a meter name, which is a string, each costs a package reference, and a
reference to a library nothing uses is a claim about the dependency graph that
is not yet true. `AddEntityFrameworkCoreInstrumentation` therefore landed at
**PR-08**, the PR that gave a service a `DbContext`, and is live.
`AddRedisInstrumentation` landed at **PR-12** — but inside
`AddRedisConnections` ([§8.2](08-caching-redis.md)), not here, and permanently
so: §8.1's connections are keyed services, the parameterless overload
discovers only an unkeyed `IConnectionMultiplexer`, so registered in this
block it would silently instrument nothing — and it would hand
`StackExchange.Redis` transitively to every host, including the ones with no
Redis. The authentication block above is in `AddCommonWebDefaults` rather than
in `AddObservability`, and it landed at PR-16 with the scheme that makes its
policy mean anything — the two arrive together because neither works alone.

> **The EF Core call takes no options, and the option this block used to pass
> is gone.** It configured `SetDbStatementForText = true` until PR-08 tried to
> compile it: the property does not exist on the instrumentation package, which
> emits the command text through the semantic-convention attributes by default
> instead. The package's documentation also describes a `SetDbQueryParameters`
> switch that would add raw parameter **values** to a span — every password,
> token and card number the application has ever bound, written where
> [§13.4](13-observability.md)'s redactor never looks — but it is not exposed
> on the pinned version either, verified against the compiler rather than the
> XML docs, which list it. Nothing here can turn it on. **If a later bump
> exposes it, it stays off.**

> **A third attribute reaches the resource and no line above puts it there.**
> `deployment.track` — `stable` or `canary` — is what
> [§15.5](15-cicd-deployment.md)'s rollout compares the two versions by, and it
> arrives through `OTEL_RESOURCE_ATTRIBUTES`, the SDK's own mechanism, set by
> the chart from `canary.enabled` (§15.4,
> [ADR-022](adr/ADR-022-the-canary-is-a-second-release-weighted-by-replicas.md)).
> The resource builder above already honours it, so nothing in `Common.Web`
> knows the word *canary* and no `AddAttributes` line was added for it.
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
its meaning drifts:

```csharp
namespace Ordering.Application.Orders;

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
        _value = meter.CreateHistogram<double>("orders.value", unit: "EUR");
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

```csharp
// RecordPendingFactsAsync — the placement claim. Money is reassembled from the
// row rather than taken from the event, because the event that triggers this
// call may be a cancellation. Through Money.Of (§5.3), which is the only way
// in and the one that normalises the padded CHAR(3) the row returns.
if (placed is not null)
    metrics.Placed(Money.Of(placed.TotalAmount, placed.Currency.Trim()));
```

```csharp
// The cancellation claim. PlacedCounted = 1 in the predicate is what orders the
// two counters; CancelReason is written by the handler through
// CancellationReasons.ToCode, the same table the parse uses, which keeps the
// dimension a bounded, stable set.
if (cancelled is not null)
    metrics.Cancelled(cancelled);
```

Fulfilment duration is recorded there for a second reason on top of that one. It
spans placement to confirmation, and the summary row is the only place that sees
both ends — the handler that confirms an order knows nothing about when it was
placed:

```csharp
// OrderSummaryProjection.RecordPendingFactsAsync (§6.6), one of three claims.
FulfilmentFact? fulfilment =
    await connection.QuerySingleOrDefaultAsync<FulfilmentFact>(
        """
        UPDATE ordering.OrderSummaries
        SET FulfilmentCounted = 1
        OUTPUT inserted.PlacedAt, inserted.ConfirmedAt
        WHERE OrderId = @OrderId
            AND PlacedAt IS NOT NULL
            AND ConfirmedAt IS NOT NULL
            AND FulfilmentCounted = 0;
        """, args);

if (fulfilment is not null)
    metrics.Fulfilled(fulfilment.ConfirmedAt - fulfilment.PlacedAt);
```

> The projection is the right home for a *duration* metric because it is the
> component that has already gathered both halves. A handler measuring this
> would have to re-read the aggregate to find its own start time.
>
> **This used to draw an analogy with the denormalised product name, and
> [ADR-027](adr/ADR-027-the-order-summary-stores-product-ids-and-resolves-the-name-locally.md)
> took the other half of it away.** The name is no longer gathered beside the
> order at all — it is written to a product-keyed table and resolved on read —
> so the two are now opposites rather than a pair. The duration keeps the
> argument because both of *its* halves really do land on the same row.
>
> **Note what the predicate is not.** An earlier version of this measured
> `now − PlacedAt` when the `Confirmed` event arrived, guarded by
> `if (placedAt is not null)`. That guard was defensive against a case that
> could not happen — until the out-of-order fix in §6.6 made `PlacedAt`
> legitimately NULL for an order whose confirmation was claimed first. The
> guard then silently dropped the measurement, permanently, for exactly the
> orders whose delivery was disordered — which correlates with load, which is
> when the number matters. Claiming on "both timestamps present and not yet
> counted" has no such ordering assumption to be wrong about.

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
> The rule runs one way only. **Every alert and SLO row must name an instrument**
> (§13.6, §13.7) — a target with no signal reads as satisfied. An instrument
> with no alert is just a number somebody looks at, which is most of them. The
> asymmetry is worth stating because the tidy-looking mistake is to invent
> thresholds for the other three so every metric has a row, and a page for
> "cancellations up 20%" is one nobody can act on at 3 a.m.

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
| `OutboxMetrics` | §13.6, `Ordering.Infrastructure` | `outbox.oldest.age`, read once per lane |
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

```csharp
// Common.Application — registered by AddOrderingApplication (§4.2) and forced
// at startup wherever a MetricsInitialiser exists (§13.6) — Ordering has one
// and Catalog does not, which §13.6 records as a gap and check.py asserts:
// "a behaviour injects it"
// is not the same as "something has constructed it".
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

```csharp
// Common.Infrastructure — registered by AddOrderingInfrastructure, because
// all four call sites are Infrastructure types (§9.4, §9.5).
//
// It grows in instalments, on PluggableInterfaces.All's terms: Projected
// lands with the outbox, because ProjectionInvoker is its only call site, the
// next two with the consumers that record them, and Suppressed when §9.5's
// silent drop was given a signal. An instrument nothing writes to would be an
// empty series on a dashboard rather than a signal — which is the rule for
// when one may be added, not a claim that four is the number.
public sealed class MessagingMetrics
{
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
            description: "OccurredAt to consumer start.");
        _projectionLag = meter.CreateHistogram<double>(
            "projection.lag",
            unit: "s",
            description: "Event raised to projection applied.");
        _rejected = meter.CreateCounter<long>(
            "command.domain_rejected",
            description: "Message-borne commands the domain refused (§9.8).");
        _suppressed = meter.CreateCounter<long>(
            "messaging.inbox.suppressed",
            description: "Messages the inbox dropped as already handled (§9.5).");
    }

    public void Delivered(string message, TimeSpan lag) =>
        _deliveryLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Rejected(string message, string error) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("error", error));

    // Neither tag is the MessageId, and that is the constraint rather than an
    // omission: a message id is unbounded, so it belongs in the log line the
    // filter writes beside this and never on a series. Both of these are
    // closed sets fixed at registration (§9.8).
    public void Suppressed(string message, string endpoint) =>
        _suppressed.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("endpoint", endpoint));
}
```

The two lags read `OccurredAt` from **different places**, because they measure
different lanes. `Delivered` reads it **off the message**: it covers the broker
lane, every integration event carries the field (§9.1), and
`IntegrationEventConsumer<T>` reaches it through the `IIntegrationEvent`
constraint — so there is no header to define and nothing to keep in sync.
`Projected` reads it **off the outbox row**, which the claim now returns (§9.4)
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

Both lags compare a timestamp made on another machine, so both carry the same
caveat: they are useful at second granularity and meaningless below it, which is
why §13.7's targets for them are in seconds and not milliseconds. The two
counters carry no such caveat, because neither reads a clock at all:
`command.domain_rejected` is recorded by `CommandConsumer` at the moment the
dispatcher returns a failure (§9.8), and `messaging.inbox.suppressed` by
`InboxFilter<T>` at the moment it drops a delivery (§9.5) — both on the
machine doing the work.

**That sentence said *the third instrument* until there was a fourth**, and
the ordinal is what made it wrong rather than merely incomplete: it read as
closing a three-way partition, so the instrument that arrived next had nowhere
to be. `MessagingMetrics` itself dropped the ordinal in the same change, for
the same reason.

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

The behaviour that records the first of these is the one behaviour §6.3 never
showed:

```csharp
// Common.Application. Registered first, so it is outermost (§6.3): the span
// covers validation, idempotency, the transaction and the handler.
public sealed class LoggingBehavior<TRequest, TResult>(
    ILogger<LoggingBehavior<TRequest, TResult>> logger,
    RequestMetrics metrics,
    TimeProvider clock)
    : IPipelineBehavior<TRequest, TResult>
{
    // Compiled once per closed behaviour rather than parsed per request. CA1848
    // is met rather than waived here: this behaviour is outermost on every
    // dispatched request, which is exactly the hot path the rule is about.
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

        // A scope, not a log property: everything written inside the handler
        // inherits it, including EF Core's and MassTransit's own logging.
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RequestType"] = name
        });

        try
        {
            TResult result = await next();

            // Read once and used twice. Two calls to GetElapsedTime would put
            // a different number in the log line and the histogram, and the
            // one a reader trusts is whichever they looked at first.
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
one that stops matching:

```csharp
// Common.Web — the one never-log vocabulary, read by SensitiveDataRedactor
// for a record's attributes and by RedactingScopeProvider for the scopes those
// records inherit. Public so a test can pin it: the list is the control, so a
// term removed in a refactor has to fail a test rather than silently widen
// what is exported.
public static class SensitiveKeys
{
    // Matching is by substring, ordinal and case-insensitive. The field that
    // leaks is never named exactly "password" — it is "NewPassword",
    // "card_number", "id_token". The cost is that a term which is a substring
    // of an innocent word redacts that word too, which is why "pin" is
    // deliberately absent: "Shipping" contains it.
    //
    // Both spellings of the snake_case entries are listed rather than
    // normalised, because normalising a key would have to guess at the
    // separator and a miss here is silent.
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

    // The never-log terms, in declaration order — wrapped, not handed out.
    // IReadOnlyList<T> is a static view and not a guarantee, so returning
    // Terms lets any caller write (string[])SensitiveKeys.All and rewrite the
    // vocabulary at run time, which the pinning test below could never see:
    // it would go on asserting about a list the process had stopped using.
    // Array.AsReadOnly wraps once at type initialisation and cannot be cast
    // back; the private array stays for the loop, the only hot path.
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(Terms);

    // A foreach rather than Terms.Any(t => key.Contains(t, ...)): the lambda
    // would capture `key`, so the closure allocates once per attribute
    // inspected — including on the no-match path the callers are written to
    // keep allocation-free. This runs on every attribute of every log record.
    public static bool Matches(string key)
    {
        foreach (string term in Terms)
        {
            if (key.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // Whether a VALUE carries a secret whatever its key is called, and this is
    // the half that survives a key nobody predicted: the list above can only
    // catch a name someone thought of, and the failure is silent — no test can
    // be written for the term that is missing. Two shapes are recognised
    // because both are unmistakable and both are what this platform actually
    // holds: a connection string, which every service builds from
    // configuration and which carries Password= inline, and a JWT, which §11.3
    // puts on every authenticated request.
    //
    // Deliberately not a general entropy test. A high-entropy string is an id
    // as often as it is a credential, and redacting every id would empty the
    // records an incident is triaged by — §13.1's whole argument.
    public static bool LooksLikeSecret(object? value)
    {
        if (value is not string text || text.Length == 0)
            return false;

        if (Assigns(text, "password") || Assigns(text, "pwd"))
            return true;

        // A JWT's header is base64url of a JSON object opening `{"`, which is
        // always the three characters below, and the compact serialisation has
        // exactly two dots. Anchored on the prefix so the dot count — the
        // expensive half — is reached by almost nothing.
        if (!text.StartsWith("eyJ", StringComparison.Ordinal))
            return false;

        int dots = 0;

        foreach (char c in text)
        {
            if (c == '.')
                dots++;
        }

        return dots == 2;
    }

    // Whether the text ASSIGNS to the key — the key, any whitespace, then `=`.
    // The whitespace is the whole reason this is not a substring test: an
    // ADO.NET connection string is a list of keyword=value pairs and the
    // parser tolerates spaces around the separator, so `Password = hunter2` is
    // as valid as `Password=hunter2` and a check for the literal "password="
    // misses it — a value walking past a guarantee written as "whatever its
    // key is called". Requiring the `=` is what keeps this off prose: "the
    // password was rejected" assigns nothing. Scanning rather than parsing,
    // because what reaches here is an arbitrary logged value and
    // SqlConnectionStringBuilder would throw on most of it.
    private static bool Assigns(string text, string key)
    {
        int from = 0;

        while (from <= text.Length - key.Length)
        {
            int at = text.IndexOf(key, from, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                return false;

            int after = at + key.Length;

            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;

            if (after < text.Length && text[after] == '=')
                return true;

            from = at + 1;
        }

        return false;
    }
}
```

The processor reads that vocabulary and rewrites what a record carries of its
own — the attributes first, then the two fields the exporter would otherwise
ship the same secret through:

```csharp
// Common.Web — added to the OpenTelemetry logging pipeline in §13.2, which is
// the point: every host calls AddObservability, so the rule applies to all of
// them. In a service's own project it would protect that service alone.
// Public for the same reason `MetricsInitialiser` (§13.6) and `Program` (§4.2)
// are: the test below constructs it, and no test project lives inside
// Common.Web. One access modifier beats an InternalsVisibleTo that has to name
// its consumer.
public sealed class SensitiveDataRedactor : BaseProcessor<LogRecord>
{
    // The key ILogger puts the message template under. Its presence is what
    // makes Body a template rather than a rendered line — see OnEnd.
    private const string OriginalFormat = "{OriginalFormat}";

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

            // The value check is the half that survives a key nobody predicted,
            // and {OriginalFormat} is exempt from it: the template is written
            // by the author rather than bound from data, so a template reading
            // "connecting with password={Pwd}" would otherwise redact the one
            // attribute the fallback below depends on.
            if (!SensitiveKeys.Matches(attribute.Key) &&
                (attribute.Key == OriginalFormat || !SensitiveKeys.LooksLikeSecret(attribute.Value)))
            {
                continue;
            }

            // Copy only when something actually matches — the common case is
            // no match, and this runs on every log record on every request.
            scrubbed ??= [.. record.Attributes];

            // Keep what was removed. The exception check below needs the
            // values, not the keys — see the rule it enforces.
            if (attribute.Value?.ToString() is { Length: > 0 } secret)
                (secrets ??= []).Add(secret);

            scrubbed[i] = new KeyValuePair<string, object?>(attribute.Key, "[redacted]");
        }

        if (scrubbed is null)
            return;

        record.Attributes = scrubbed;

        // Attributes alone are not enough. IncludeFormattedMessage is set
        // above, and with it the exporter sends FormattedMessage as the
        // record's body — the template with every argument substituted.
        // Redacting Password while "Login for ada with hunter2" ships beside
        // it protects nothing and reads in review as though it does.
        //
        // Body is only that template when the state carried {OriginalFormat}.
        // Without it OpenTelemetry fills Body with the formatter's own output
        // — the rendered line, secret and all — so falling back to Body there
        // would re-export what the scrub just removed.
        record.FormattedMessage = hasTemplate && record.Body is not null
            ? record.Body
            : "[redacted]";

        // The rule this enforces: never export a value the processor has just
        // decided is sensitive. OTLP serialises Exception separately from both
        // Attributes and FormattedMessage — as exception.message and
        // exception.stacktrace — so scrubbing those two and leaving the
        // exception alone ships the secret through a third channel.
        //
        // ToString() rather than Message, because it covers inner exceptions
        // and the stack trace too. Dropped rather than rewritten: Exception
        // .Message is read-only, and reconstructing the type is not something
        // to attempt on a logging path. The record keeps its level, template
        // and every non-sensitive attribute, so the error is still visible —
        // what is lost is the trace, on the records that demonstrably carry a
        // live secret in it.
        //
        // Deliberately narrower than "drop the exception whenever anything was
        // redacted": that would destroy stack traces on every record that
        // merely has a Password attribute beside an unrelated failure, which
        // is most of them.
        if (record.Exception is not null && secrets is not null && Reveals(record.Exception, secrets))
            record.Exception = null;
    }

    private static bool Reveals(Exception exception, List<string> secrets)
    {
        string text = exception.ToString();

        foreach (string secret in secrets)
        {
            if (text.Contains(secret, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
```

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
is nothing to match it against and it survives. That is the interpolation case again: text an author
wrote by hand, which no key-based mechanism can inspect. `throw new
InvalidOperationException($"bad token {token}")` is the same mistake as
`$"Token is {token}"` and is caught by neither.

**Scopes are redacted as well, and by a second mechanism rather than by more
of the processor.** `IncludeScopes` is on (§13.2), so every record inherits
the scopes open around it. This section used to list that as a fourth limit
and argue it was harmless: the platform opened two scopes and neither could
carry a secret — `LoggingBehavior`'s `RequestType` (§13.3), which is a type
name, and `UseCorrelationId`'s `CorrelationId`
([§10.4](10-api-gateway.md)), which it called a trace ID or a GUID. The second
half was false when it was written. A client-supplied `X-Correlation-Id` was
adopted verbatim, so that scope carried whatever the caller sent, on every
record written inside the request. It is true today only because §10.4's
middleware now bounds what it will adopt — and a safety claim that rests on a
neighbouring component's validation is one that expires the next time that
component is edited, silently.

> **A processor could never have fixed this, only noticed it.** `LogRecord`
> exposes `ForEachScope` and no settable scope provider — measured against
> OpenTelemetry 1.17 rather than assumed — so a `BaseProcessor<LogRecord>` that
> walked the scopes could read a secret out of one and would have no way to
> put anything else back. Redaction has to happen where the scope is *read*.

That place is one layer lower than the pipeline: the `IExternalScopeProvider`
`LoggerFactory` resolves from the container and hands to every provider it
holds. §13.2 registers a wrapper there, which is why the fix covers the scopes
EF Core and MassTransit open as well as the platform's own two. That breadth
is the point rather than a bonus — the argument this replaces was an inventory
of *this repository's* `BeginScope` calls, and a library's calls were never on
it.

```csharp
// Common.Web — registered by AddObservability (§13.2) as a singleton
// IExternalScopeProvider wrapping LoggerExternalScopeProvider.
public sealed class RedactingScopeProvider(IExternalScopeProvider inner, bool ownsInner = false)
    : IExternalScopeProvider, IDisposable, IAsyncDisposable
{
    // Registers this wrapper as the container's IExternalScopeProvider, around
    // whatever was registered before it — wrapping rather than deferring,
    // because §13.4 is a guarantee and not a default. The prior descriptor is
    // removed and rebuilt inside the factory rather than resolved, because
    // resolving IExternalScopeProvider from within its own factory is
    // unbounded recursion. Only the last non-keyed registration is wrapped,
    // for the reason the container itself gives: single-service resolution
    // returns the last, so the earlier ones were already unreachable.
    public static IServiceCollection WrapScopesForRedaction(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? existing = services.LastOrDefault(
            d => d.ServiceType == typeof(IExternalScopeProvider) && !d.IsKeyedService);

        if (existing is not null)
            services.Remove(existing);

        // ownsInner follows who CREATED the inner provider, because that is
        // who the container would have disposed. Removing the descriptor takes
        // the inner service out of the container's disposal tracking, so a
        // factory or an implementation type is this wrapper's to dispose. An
        // ImplementationInstance is not: the container never disposes an
        // instance it did not create, and neither does this.
        services.AddSingleton<IExternalScopeProvider>(
            sp => new RedactingScopeProvider(
                Inner(sp, existing),
                ownsInner: existing is not null && existing.ImplementationInstance is null));

        // Wrapping what came BEFORE is only half of it. AddCommonWebDefaults
        // runs ahead of a host's own registrations and the container resolves
        // the LAST, so an AddSingleton<IExternalScopeProvider>(…) written
        // afterwards replaces this wrapper and exports every scope raw.
        // Nothing here can prevent that — the line that would beat it has not
        // been written yet — so the guard refuses to start the host instead.
        services.AddHostedService<ScopeRedactionGuard>();

        return services;
    }

    // Refuses to start a host whose IExternalScopeProvider is not the wrapper.
    // A resolve-time check, because a registration-time one cannot see the
    // future — and failing the host is the direction §13.5's readiness guard
    // already takes: a control that is silently absent is worse than a host
    // that will not boot, because only one of the two is noticed. It reports
    // rather than repairs; re-registering here would leave a host running with
    // a provider its own author did not choose.
    private sealed class ScopeRedactionGuard(IExternalScopeProvider scopes) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (scopes is not RedactingScopeProvider)
            {
                throw new InvalidOperationException(
                    $"IExternalScopeProvider resolves to {scopes.GetType().FullName}, not " +
                    $"{nameof(RedactingScopeProvider)}, so §13.4's scope redaction is switched " +
                    "off and every log scope exports raw. …");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // The three shapes a descriptor can carry. A keyed one never reaches here
    // — the query above excludes them, because a keyed registration is not
    // what LoggerFactory resolves.
    private static IExternalScopeProvider Inner(IServiceProvider sp, ServiceDescriptor? existing)
    {
        if (existing is null)
            return new LoggerExternalScopeProvider();

        if (existing.ImplementationInstance is IExternalScopeProvider instance)
            return instance;

        if (existing.ImplementationFactory is not null)
            return (IExternalScopeProvider)existing.ImplementationFactory(sp);

        return (IExternalScopeProvider)ActivatorUtilities.CreateInstance(
            sp,
            existing.ImplementationType!);
    }

    private readonly IExternalScopeProvider _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    // It redacts on the way out, not on the way in: Push stores the caller's
    // object untouched, because a scope is also a live object the application
    // may read back, and only the enumeration a logging provider performs is
    // rewritten. That also keeps the cost on the path that logs rather than on
    // the path that opens a scope.
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // A static lambda with the caller's callback carried in the state, so
        // nothing is captured. The obvious spelling closes over `callback` and
        // allocates a display class every time a provider enumerates scopes —
        // which is every log record, since §10.4 opens a correlation scope on
        // every request.
        _inner.ForEachScope(
            static (object? scope, (Action<object?, TState> Callback, TState State) s) =>
                s.Callback(Redact(scope), s.State),
            (Callback: callback, State: state));
    }

    public IDisposable Push(object? state) => _inner.Push(state);

    // Ownership follows creation, which is the rule the container itself uses.
    // Both interfaces, because a provider implementing only IAsyncDisposable
    // leaves DI tracking with its descriptor like any other and the container
    // may take either path.
    public void Dispose()
    {
        if (!ownsInner)
            return;

        switch (_inner)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            case IAsyncDisposable asyncDisposable:
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!ownsInner)
            return;

        switch (_inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private const string Redacted = "[redacted]";

    private static object? Redact(object? scope)
    {
        // IEnumerable rather than IReadOnlyList, and the difference is the
        // whole of the keyed case: BeginScope(new Dictionary<,>) — which is
        // what §10.4 and §13.3 both open — produces a Dictionary, and a
        // Dictionary is NOT an IReadOnlyList. Matching on the list interface
        // alone left every scope this platform actually opens unredacted while
        // the unit tests over MEL's own FormattedLogValues stayed green.
        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            return RedactPairs(scope, pairs);

        // A scope with no keys at all reaches the exporter as a single unkeyed
        // value, so only the value check can say anything about it. Narrow, and
        // it is the shape BeginScope(someString) produces.
        return SensitiveKeys.LooksLikeSecret(scope) ? Redacted : scope;
    }

    // Scanned before it is copied, so the common case — nothing sensitive —
    // returns the caller's own object and allocates only the enumerator. The
    // second pass is paid on the match path alone, which is the one that was
    // about to export a secret.
    private static object RedactPairs(object scope, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        if (!AnySensitive(pairs))
            return scope;

        List<KeyValuePair<string, object?>> scrubbed = [];

        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            scrubbed.Add(IsSensitive(pair)
                ? new KeyValuePair<string, object?>(pair.Key, Redacted)
                : pair);
        }

        return new RedactedScope(scrubbed);
    }

    private static bool AnySensitive(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            if (IsSensitive(pair))
                return true;
        }

        return false;
    }

    private static bool IsSensitive(KeyValuePair<string, object?> pair) =>
        SensitiveKeys.Matches(pair.Key) || SensitiveKeys.LooksLikeSecret(pair.Value);

    // ToString is overridden, and that is the load-bearing half: MEL's own
    // scope type renders its values from ToString, so a provider that formats
    // a scope rather than enumerating it would otherwise print the secret
    // straight back out of a list this class had just scrubbed — the same
    // failure the FormattedMessage rewrite above exists to prevent, one layer
    // over.
    private sealed class RedactedScope(List<KeyValuePair<string, object?>> pairs)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => pairs.Count;

        public KeyValuePair<string, object?> this[int index] => pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public override string ToString() => string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }
}
```

**The processor governs one pipeline, which is why §13.2 leaves only one.** A
`BaseProcessor<LogRecord>` sees records inside OpenTelemetry and nowhere else.
Any other `ILoggerProvider` on the host formats the original state itself and
never passes through this code — so `AddObservability` calls
`ClearProviders()` before adding OpenTelemetry, making it the sole provider.

That is not tidiness. `WebApplication.CreateBuilder` installs Console, Debug and
EventSource before a host reaches `AddCommonWebDefaults` (§4.2), and container
stdout is collected in most clusters, so a `{Password}` scrubbed on the OTLP
path shipped in clear text on the console one. The redaction looked complete and
covered a single destination — the same shape as the `FormattedMessage` gap
above, one layer further out.

Two things follow, both worth stating. The guarantee covers providers registered
**before** `AddObservability`, which is every default and the only case §4.2
produces; a service that adds a provider afterwards has opted out and owns the
consequence. And the visible cost is local: `dotnet run` no longer prints to the
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
away underneath it:

```csharp
// CA1848 is enforced repo-wide (ADR-019) and does not exempt test projects, so
// the template goes through LoggerMessage.Define exactly as production logging
// does. The point survives intact: the attribute keys still come from a message
// template, read through ILogger.
private static readonly Action<ILogger, string, string, Exception?> Login =
    LoggerMessage.Define<string, string>(
        LogLevel.Information,
        new EventId(1, nameof(Login)),
        "Login for {User} with {Password}");

private static LogRecord EmitRecord(Action<ILogger> write)
{
    List<LogRecord> exported = [];

    // Built exactly as AddObservability builds it (§13.2) — ILoggingBuilder,
    // the same extension, and IncludeFormattedMessage set the same way, so the
    // test covers the seam the host uses. A block rather than a using
    // declaration: the factory has to be disposed before the exported list is
    // read, and a declaration would defer that to the end of the method.
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

[Fact]
public void Sensitive_attributes_are_redacted()
{
    IReadOnlyList<KeyValuePair<string, object?>> attributes =
        EmitRecord(logger => Login(logger, "ada", "hunter2", null)).Attributes!;

    attributes.Single(a => a.Key == "Password").Value.ShouldBe("[redacted]");

    // The other half of the assertion, and the one that catches a deny-list
    // grown careless: everything not on it survives intact.
    attributes.Single(a => a.Key == "User").Value.ShouldBe("ada");
}

[Fact]
public void A_redacted_record_does_not_export_the_rendered_secret()
{
    // The assertion above is cosmetic without this one: the exported body is
    // the rendered string, and it is what a log backend indexes.
    LogRecord record = EmitRecord(logger => Login(logger, "ada", "hunter2", null));

    record.FormattedMessage.ShouldBe("Login for {User} with {Password}");
}
```

**That comment is true of the attribute half only.** The factory above
registers no scope provider, so nothing on this path exercises the scope
redaction argued above; that half is asserted separately, against the
`IExternalScopeProvider` a `LoggerFactory` hands its providers — the seam the
mechanism actually rests on, and the one that would go quiet if a release ever
stopped resolving the registration.

Going through `ILogger` also means the test exercises message templates, which
is where the attribute keys come from — so the `{Token}` naming advice above is
verified by this test rather than merely stated near it.

## 13.5 Health checks

Three distinct endpoints, because Kubernetes asks three distinct questions.

Registration and exposure live in different places, for one reason: the checks
need connection strings and the endpoints do not.

**The checks** are registered by the service's own Infrastructure — the block
shown in `AddOrderingInfrastructure` (§4.2), which has the configuration:

```csharp
services
    .AddHealthChecks()
    .AddSqlServer(configuration.GetConnectionString("Ordering")!, name: "sql", tags: ["ready"])
    .AddRedis(configuration.GetConnectionString("RedisCache")!, name: "redis-cache", tags: ["ready"])
    // No RabbitMQ line, deliberately: AddMassTransit registers the bus health
    // check itself — "masstransit-bus", tagged ready — see below.
    // No outbox line either: the backlog is observed through §13.6's gauges
    // and alerts, and the note below says why it must not be a check.
    .AddRedis(configuration.GetConnectionString("RedisCoordination")!, name: "redis-coordination", tags: ["ready"]);
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
after `builder.Build()` (§4.2):

```csharp
namespace Common.Web;

// The tag §13.5 names, spelled once: the two predicates below and the startup
// guard have to be asking about the same set, and a tag that matched in one
// place and not the other would fail open in exactly the direction this guard
// exists to close.
private const string Ready = "ready";

// ownsNoReadinessDependencies is true for a host that deliberately gates
// readiness on nothing. Passing it is a written decision; the default is a
// startup failure.
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

    // AllowAnonymous is required, not cosmetic: the kubelet sends no token,
    // so an authenticated probe fails and the pod is restarted in a loop.
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

// Read from the options rather than from HealthCheckService: the service
// exposes no registration list, and the options are what the predicates above
// are evaluated against — so this asks the same question the probe will ask,
// rather than one that merely correlates with it.
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
guard above rather than left as prose. The **gateway** and the **BFF** own no
database (§4.2), so they declare their empty set at the call site and an
absence becomes a written decision. Every other host fails to start: each
service owns a schema, including the two with no public API, since Shipping
and Notifications both ship a migrator and both register a SQL check (§4.1,
[§3.2](03-bounded-contexts.md)).

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
| Error queue depth | > 0 | A business process has stopped — and since [#124](https://github.com/alexander-shamray/blueprint-backend/issues/124) one arrival is a saga's deliberate escalation rather than a fault: a `PaymentAuthorised` for an order whose instance has already been finalised correlates to nothing, so §9.6 faults it here instead of letting it be consumed cleanly and gone. Money that moved on an order nothing is tracking is what this queue is for. The depth cannot tell that arrival from a broken consumer, so the faulted message's type is what triage reads first | `error-queue.md` |
| Skipped queue depth | > 0 | An endpoint was handed a message it has **no consumer for**, and MassTransit parked it in `<queue>_skipped`. Nothing threw and nothing will retry it, so the business fact is lost as quietly as an unwatched queue can lose one. Deliberately not folded into the row above with a `.+_(error\|skipped)` selector: an error-queue message is one a consumer accepted and could not finish, this is one no consumer would take, and the two are triaged from opposite ends — replay is the right first move here and the wrong one there. **It fires on a correctly ordered release exactly never**, which is what makes it worth paging on and what makes [§9.2](09-messaging.md)'s consumer-before-producer rule enforceable rather than advisory | `skipped-queue.md` |
| Saga age | any saga unfinalised > 1 h **outside `Confirmed`**, or > 4 days **in** it | Orders are stuck. **The margin is argued against the longest wait rather than the set of them**, because a list of thresholds is the half that rots: the shortest in §9.6 is the five-minute reservation wait and the longest is a payment verdict at **thirty minutes** — armed at fifteen when `AuthorisePayment` is sent, and re-armed once when `Compensating` inherits it unanswered ([#124](https://github.com/alexander-shamray/blueprint-backend/issues/124)) — so an hour still clears every one of them. This cell used to name three thresholds and stopped being true the day a wait was re-armed; the waits also compose along a path, and the margin survives that too. But the despatch wait is **three days** by design, three orders of magnitude further out, and an unqualified hour would page on the healthy path for most of a saga's real lifetime. A despatch that genuinely expires escalates to the row below, not to this one. **The state is named rather than described, and this row used to describe it**: §9.6 has no `AwaitingDespatch`, because the saga arms `DespatchTimeout` on the transition *into* `Confirmed` — the order is confirmed and now waiting on Shipping. A selector spelled the way the description reads would match no series, exclude nothing, and page on every healthy confirmed order. **The four-day branch is not a refinement but the other half of the alert**: excluded outright, a `Confirmed` saga whose three-day timeout was never delivered is invisible here *and* to the row below, because that timeout is what creates the review row. Nothing would page at all | `stuck-saga.md` |
| Orders awaiting review | any row in `ordering.OrderReviews` older than 1 h | A saga escalated work the Ordering workflow cannot finish itself (§9.6) — a wait it could not compensate, an authorisation that landed while compensation was already under way, or a **confirmation** that did. **Neither phrase here is the one this row carried**: "work it has no contract to do" is wider than the truth, since §3.2 gives Payments both a `Refund` aggregate and `OrderCancelled` to act on — what Ordering lacks is a way to *ask*; and "once the order was already being cancelled" asserts a cancellation that on two of the three doors has not happened yet. **The second is not necessarily a customer cancelling**, and the runbook used to say it was: compensation begins on a cancellation, a decline or the fifteen-minute payment timeout alike, so a slow PSP raises that row with nobody having cancelled anything. **Whether the saga has finalised is a property of the BRANCH that raised the row, not of the row and not reliably of the reason**: most are raised on the way out, while `payment_authorised_during_compensation` and — since [#126](https://github.com/alexander-shamray/blueprint-backend/issues/126) — `cancelled_after_confirmation` are raised mid-wait from `Compensating` and can sit beside a live instance. That second one is why "the reason" is no longer enough: `cancelled_after_confirmation` is raised from `Confirmed` too, where it finalises, so one code now spans both answers. **Since [#124](https://github.com/alexander-shamray/blueprint-backend/issues/124) the branch is not quite enough either, and the narrowing runs in both directions**: `Compensating` finalises on a join — the stock half settled *and* no payment verdict outstanding — so the branch raising `payment_authorised_during_compensation` finalises in the same transition when the stock half has already settled, and leaves the instance standing when it has not. "Raised mid-wait" no longer implies a live instance, and a raised row no longer implies a wait. What survives is the predicate: a branch whose `Finalize` is absent or conditional can outlive its row's first hour, and the instance is what says which. **Those are the cases the saga-age alert can reach, and it will not usually reach them either**: both thresholds are an hour, and every wait that can hold `Compensating` open ends well inside one — the release wait at ten minutes, and the payment verdict at thirty from `AuthorisePayment` — so the instance is normally gone before this row is old enough to page. The two coincide when a wait failed to arrive at all, which is why this row exists rather than being folded into the one above | `order-review.md` |
| Migration job failed | Helm `pre-install,pre-upgrade` hook non-zero, or a release stuck pending | The deploy stopped before any pod rolled ([§7.4](07-persistence.md)). On an **upgrade** the previous version is still serving, which is why nothing else fires — so this alert is the only signal. On a first **install** there is no previous version and no pod at all, so nothing else fires for the opposite reason: check which before promising availability | `migration-failure.md` |
| Cache hit ratio collapse | hits ÷ (hits + misses) < 50% over 10 min. **The expression lives in the rule file, not here** — see below | Redis lost its working set; every miss becomes a database read, and the databases are sized for a warm cache (ADR-006) | `redis-cold.md` |
| Business volume | `orders.placed` per hour drops > 50% vs the same hour last week | The most valuable alert here — it catches failures no technical metric detects. §6.6's worked case: `ordering.ProductPrices` has no row for a product, every order containing it is **refused by the domain**, and the result is a 422 `order.products_unavailable` the customer sees, no exception, no 5xx and no lag. **Not a 400, and the difference is where the on-call looks**: the request is well-formed and the validator passed it (§10.5 maps `Error.Rule` to 422, `ValidationException` to 400), so a 400 dashboard shows a path this request never took. Week-over-week rather than a fixed floor, because a volume alert without a seasonality model is the first pager people mute | `business-volume.md` |

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
| **Outbox growth** | `outbox.pending.count` > 1000 and rising over 10 min, replicas deduplicated | Either lane, not keeping up | Dispatcher not running, batch size too small for load, a slow deliverer. **Not a failed purge** — this gauge counts `ProcessedAt IS NULL` and the purge deletes *processed* rows, so a stopped purge grows the table without moving this number. It was listed here until the runbook was written and said the opposite | `outbox-growth.md` |
| **Abandoned rows** | `outbox.abandoned.count` > 0, per lane | Permanent data loss, disguised as an ordinary stall | A message that will never be delivered and is no longer being retried. The `lane` tag says whose loss: `Broker`, and other services never learned something; `Local`, and this service's read model is permanently wrong | `outbox-abandoned.md` |

Thresholds differ by an order of magnitude because the lanes have different
floors. The local lane is in-process with no network hop, so 30 seconds of lag
already means something is wrong. The broker lane crosses a network and should
absorb a short RabbitMQ blip or a rolling broker restart without paging anyone.

> **Alert on abandoned rows specifically.** The dispatcher claims rows `WHERE
> Attempts < 10` (§9.4), so a row that exceeds the cap is silently skipped
> forever, and without this alert permanent loss of a business event has no
> signal of its own.
>
> **What that looks like depends on how the other two gauges count, and this
> platform's count it in.** The classic failure is the one this callout used to
> describe: a backlog that excludes abandoned rows drains to zero and the graph
> goes green *precisely because* the message was given up on.
> `OutboxStats.PendingCount` and `OldestAgeSeconds` filter on `ProcessedAt IS
> NULL` alone, so an abandoned row keeps both non-zero instead — which trades
> that failure for a quieter one: permanent loss then looks exactly like an
> ordinary stall, and the on-call works `outbox-broker.md` for ever. **Either
> way the abandoned gauge is what tells the two apart**, which is the point;
> only the disguise changes.

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
above can say which one it is talking about:

```csharp
// Singleton, and eagerly constructed (below). Observable gauges are callbacks
// held by the Meter: if this object is never built, or is built and dropped,
// the instrument does not exist and the alert is silent.
public sealed class OutboxMetrics
{
    // The meter name is the contract with §13.2's AddMeter — an instrument on
    // an unregistered meter is collected by nothing and alerted on in vain.
    public const string MeterName = "Ordering.Outbox";

    // The tag value is the enum's own name, never a hand-written string. The
    // Lane column stores lane.ToString() and §9.4's dispatcher compares against
    // "Broker", so a lowercase tag here would give the same value three
    // spellings across SQL, C# and PromQL — and an alert querying the wrong one
    // matches no series and never fires, which looks exactly like health.
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());

    // The only thing that separates a contained failure from a healthy quiet
    // lane, because both are an absent series. LoggerMessage.Define on ADR-019's
    // terms: CA1848 is an error, and this runs once per export interval for as
    // long as the failure lasts.
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Outbox gauge read failed. This interval's measurements are omitted, so "
            + "every outbox series is absent rather than wrong — see OutboxMetrics.");

    public OutboxMetrics(IMeterFactory factory, IOutboxStats stats, ILogger<OutboxMetrics> logger)
    {
        Meter meter = factory.Create(MeterName);

        meter.CreateObservableGauge(
            "outbox.oldest.age",
            () => PerLane(stats.OldestAgeSeconds, logger),
            unit: "s",
            description: "Age of the oldest unprocessed row, per lane.");

        // Depth, per lane. The growth alert needs a count and the age gauge
        // cannot supply one: a single very old row and a backlog of ten
        // thousand recent ones read identically on oldest-age.
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => PerLane(lane => stats.PendingCount(lane), logger),
            unit: "{message}",
            description: "Unprocessed rows, per lane.");

        // Also per lane, and this is the one where it matters most: a Broker
        // abandonment means other services never learned something, a Local one
        // means this service's own read model is permanently wrong. Different
        // blast radius, different recovery, and outbox-abandoned.md asks which
        // one first.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => PerLane(lane => stats.AbandonedCount(lane), logger),
            unit: "{message}",
            description: "Rows past the attempt cap, per lane.");
    }

    // One measurement per lane, read from the enum rather than from a list
    // written out at each of three call sites. A lane added to OutboxLane and
    // forgotten at one of them would be a lane with no gauge and therefore no
    // alert — the silent gap this section spends a callout on, arriving through
    // the instrument instead of through the query.
    //
    // THE READ IS CONTAINED, and the loop is what contains it. An observable
    // callback that throws does not fail alone: RecordObservableInstruments
    // propagates and abandons the rest of the pass, so a SqlException here can
    // stop unrelated observable instruments being collected. Every lane is
    // dropped rather than the failing one, because a lane missing from a
    // `max by (lane)` reads as a healthy zero rather than as no data.
    private static List<Measurement<double>> PerLane(
        Func<OutboxLane, double> read,
        ILogger logger)
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
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(value, Tag(lane)));
        }

        return measurements;
    }
}
```

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
> a row in §13.6's table, on the same terms as the four this chapter already
> ships unloaded.

`IOutboxStats` is read from a singleton on the collector's schedule, so it must
not hold a `DbContext` — and it satisfies that by never asking for one. §6.5's
`IDbConnectionFactory` is itself a singleton holding a connection string, so it
is injected directly and the reads are Dapper on a connection the caller
disposes:

> **This used to reach for an `IServiceScopeFactory`, and PR-24 removed it.**
> The stated reason was the right one — a metrics singleton that captured a
> scoped `DbContext` would hold a connection open for the life of the process —
> but the remedy assumed the port it wanted was scoped. It is not, and
> §4.2's own sample says so, so the scope was ceremony around a resolution that
> returns the same instance either way. **A guard against the wrong dependency
> shape is not evidence about which shape you have.**

```csharp
// Every member takes the lane. Three questions about one table, each of which
// has a different answer per lane and a different runbook behind it.
public interface IOutboxStats
{
    double OldestAgeSeconds(OutboxLane lane);
    int PendingCount(OutboxLane lane);
    int AbandonedCount(OutboxLane lane);
}

internal sealed class OutboxStats : IOutboxStats, IDisposable
{
    // Cached briefly: the collector polls every few seconds and these are
    // aggregate queries over a filtered index, not free. A metrics type that
    // loads the database it is measuring is a monitor causing the symptom.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IDbConnectionFactory _connections;
    private readonly string _oldestSql;

    public OutboxStats(IDbConnectionFactory connections, OutboxTable table)
    {
        _connections = connections;

        // Composed from the registered table rather than written literally,
        // for the reason OutboxTable itself gives: this chapter writes
        // `ordering.OutboxMessages` because it is a chapter about Ordering, and
        // a second literal in code is a second place the schema has to be right.
        _oldestSql =
            $"""
            SELECT DATEDIFF(second, MIN(OccurredAt), SYSDATETIMEOFFSET())
            FROM {table.QualifiedName}
            WHERE ProcessedAt IS NULL
                AND Lane = @lane;
            """;
    }

    public double OldestAgeSeconds(OutboxLane lane) => _cache.GetOrCreate(
        $"oldest:{lane}",
        e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
            using IDbConnection connection = _connections.Create();

            // NULL rather than zero is what an empty lane returns — MIN over no
            // rows — and COUNT never returns it. One coalesce covers both.
            //
            // BOTH TIMEOUTS ARE BOUNDED, and neither is optional. These run
            // inside observable gauge callbacks on the metric reader's own
            // thread, so an unbounded wait stalls the reader and takes
            // UNRELATED telemetry down with these gauges. `commandTimeout`
            // bounds the statement; the connection string this type is built
            // with bounds `ConnectTimeout`, because a command timer does not
            // start until a connection is open and SqlClient waits fifteen
            // seconds for that by default. One without the other is a
            // safeguard that is stated and absent.
            return connection.ExecuteScalar<double?>(
                new CommandDefinition(
                    _oldestSql,
                    new { lane = lane.ToString() },
                    commandTimeout: 2)) ?? 0;
        });

    // PendingCount and AbandonedCount follow the same shape — same cache, same
    // connection per call, same @lane parameter — over COUNT(*) with
    // `ProcessedAt IS NULL AND Lane = @lane`, plus
    // `AND Attempts >= OutboxDispatcher.MaxAttempts` for the second. That cap is
    // READ rather than written again: §9.4 claims rows below it, this counts the
    // rows above it, and two copies of one number stop agreeing on the day
    // somebody tunes it.
    //
    // The lane predicate is not optional on any of the three: an untagged gauge
    // cannot answer the first question its runbook asks.
}
```

> **Two gauges, because they answer different questions and fail differently.**
> `outbox.oldest.age` catches a lane that has *stopped*; `outbox.pending.count`
> catches one that is *falling behind*. Neither substitutes for the other: a
> single stuck row pins the age gauge at hours while the count stays at 1, and
> a backlog of ten thousand rows all seconds old leaves the age gauge flat.
> The alerts in the table read one each, which is why both exist.

Registration is the step that makes any of this exist, and it is the step
`ValidateOnBuild` cannot check — nothing depends on a metrics class, so the
container is happy without it (§6.2):

```csharp
// In AddOrderingInfrastructure (§4.2). Both of Infrastructure's metrics types:
// OutboxMetrics reads the database, MessagingMetrics is injected by
// IntegrationEventConsumer<T>, CommandConsumer<,> and InboxFilter<T> and
// resolved by the outbox invoker (§13.3).
// ITS OWN connection factory, with the bounded ConnectTimeout the callbacks
// need. Resolving the shared IDbConnectionFactory here would leave SqlClient's
// fifteen-second default on the open, and the commandTimeout above never gets
// to run — the two bounds only work together.
string metricsConnectionString =
    new SqlConnectionStringBuilder(configuration.GetConnectionString("Ordering"))
    {
        ConnectTimeout = OutboxStats.ConnectTimeoutSeconds
    }.ConnectionString;

services.AddSingleton<IOutboxStats>(sp => new OutboxStats(
    new SqlConnectionFactory(metricsConnectionString),
    sp.GetRequiredService<OutboxTable>()));
services.AddSingleton<OutboxMetrics>();
services.AddSingleton<MessagingMetrics>();

// OrderMetrics and RequestMetrics are NOT registered here — they are
// Application types, and AddOrderingApplication registers them (§4.2, where
// OrderMetrics is shown as the specified shape rather than the shipped one).
// OrderMetrics arrives with §6.6's OrderSummaries projection.
// A second AddSingleton would not fail: the container keeps both and resolves
// the last, which is the trap. Two instances mean two sets of instruments on
// one meter, and the one MetricsInitialiser forces need not be the one the
// projection injects. The counters read zero for ever while a live instrument
// publishes to nobody.

// Singleton registration alone is lazy — the instruments appear on first
// resolve, which for a class nothing injects is never. Force construction at
// startup, for every one that exists. MetricsInitialiser is registered by
// Infrastructure, which may reference Application; the reverse would not
// compile.
services.AddHostedService<MetricsInitialiser>();
```

```csharp
// Public, not internal, for the same reason `Program` is (§4.2): the test
// below names the type from another assembly, and one access modifier is a
// smaller commitment than an InternalsVisibleTo that has to name the consumer.
public sealed class MetricsInitialiser : IHostedService
{
    // Resolving the parameters is the entire job: constructing them registers
    // the instruments with their meters, and nothing here needs to keep them.
    // The guards are what make them READ — see below.
    public MetricsInitialiser(OutboxMetrics outbox, MessagingMetrics messaging, RequestMetrics requests)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(messaging);
        ArgumentNullException.ThrowIfNull(requests);
    }

    // `cancellationToken`, not this blueprint's usual `ct`: CA1725 requires an
    // implementation's parameter name to match the interface it implements.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

> **This sample was written as a primary constructor whose parameters were
> named `_`, `__` and `___`, and it did not compile.** Two rules, both made
> errors by [ADR-019](adr/ADR-019-warnings-are-errors-and-the-editorconfig-is-a-build-input.md),
> and PR-24 met each by changing the code rather than waiving anything.
> **CS9113** — *parameter is unread* — fires three times, and the
> discard-looking names do not escape it: `_` in a primary constructor is an
> ordinary parameter, not a discard. **CA1725** rejects `ct` against
> `IHostedService`'s `cancellationToken`. Measured rather than reasoned about,
> which is worth recording because the rule a reader expects to fire on those
> names — CA1707, on underscores in identifiers — does **not**.
>
> The guards are not ceremony bought to satisfy a compiler. A null here would
> mean the container resolved a metrics type to nothing, which is precisely the
> silent-instrument failure this class exists to prevent.

**`OrderMetrics` is not in that constructor, and its absence is a schedule
rather than an exemption.** §13.3 puts it in `Ordering.Application` with
`OrderSummaryProjection` as its only call site, and §6.6's `OrderSummaries`
projection has not been built. It joins in the pull request that adds that
projection — and nobody has to remember, because the test below reads the
container's registrations rather than this list, so an unforced metrics type
fails a build the day it is registered.

**The test for membership is not "is it a gauge".** It is *"can this service run
for an hour without constructing it"* — and for every metrics type in this
document the answer is yes, which is why each belongs in that constructor as it
comes to exist. **Three are there today**; `OrderMetrics` is the fourth and
joins with the projection that is its only call site, per the paragraph above.

That includes `RequestMetrics`, and the reasoning that nearly excluded it is
worth keeping as the worked example. `LoggingBehavior` injects it, a behaviour
runs on every dispatched request, and it is tempting to conclude that any live
service has therefore constructed it. It has not. `IPipelineBehavior` runs for
what `IDispatcher` handles; a health probe is mapped by `MapHealthChecks`
(§13.5) and never enters the pipeline. A canary before cutover, a replica behind
a rate limiter, or a service whose traffic has simply stopped all publish
nothing — and **Notifications and Shipping have no public API at all** (§3.2),
so on those two the instrument would never exist under any circumstances.

`OrderMetrics` is the second worked example, and a different failure. It used to
be injected by `PlaceOrderHandler`, which constructed it on the first command;
moving the counters to the projection (§6.6) moved it into this list without
anybody editing this list. **A constructor parameter is a dependency on a call
site that may move**, which is why the rule is about reachability and not about
instrument type.

Reachability is not decidable from a type, so no test can assert the rule as
stated. What a test *can* do is refuse to let a metrics type appear without
somebody deciding, and make the decision the thing under review:

```csharp
// Types deliberately not forced, each with the reason it does not need to be.
// Empty today, and that is the point: a name lands here only when someone
// argues it in a pull request.
private static readonly Dictionary<Type, string> NotForced = new();

[Fact]
public void Every_metrics_type_is_forced_or_has_a_stated_reason_not_to_be()
{
    // The COLLECTION, not a built provider. IServiceCollection is the input to
    // BuildServiceProvider and is not itself a registered service, so asking a
    // provider for one throws — registrations cannot be enumerated after the
    // build. BuildServices() stops one step earlier than BuildProvider().
    //
    // It runs BOTH helpers, which matters here and nowhere else: the types are
    // split across AddOrderingApplication (RequestMetrics today, OrderMetrics
    // when it exists) and AddOrderingInfrastructure (OutboxMetrics,
    // MessagingMetrics). A helper that ran only one half would see a subset and
    // fail against a correct MetricsInitialiser — the test reporting a defect
    // in the thing it is guarding.
    IEnumerable<Type> registered = BuildServices()
        .Select(d => d.ServiceType)
        .Where(t => t.Name.EndsWith("Metrics"))
        .Distinct();

    HashSet<Type> forced = typeof(MetricsInitialiser)
        .GetConstructors()
        .Single()
        .GetParameters()
        .Select(p => p.ParameterType)
        .ToHashSet();

    // Both directions. Unforced-and-unexplained is the drift this exists for;
    // forced-but-unregistered is a host that will not start.
    registered
        .Where(t => !forced.Contains(t) && !NotForced.ContainsKey(t))
        .ShouldBeEmpty("add it to MetricsInitialiser, or to NotForced with a reason");

    forced.ShouldBeSubsetOf(registered);
}
```

> **The naming filter is a heuristic, and it is worth being honest about which
> way it fails.** `EndsWith("Metrics")` is how the test finds candidates, so a
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
> Two of the alerts in this document were written against signals that did not
> exist, and both looked correct: the dashboard is empty either way, whether
> the system is healthy or the metric was never published.
>
> Where a condition is **derived** rather than measured — the cache hit ratio
> is computed from HybridCache's hit and miss counters, not published as a
> ratio — write the expression, not an invented metric name. A name that looks
> like an instrument and is not is the hardest version of this to spot.

### Four of them have no signal yet

PR-24 wrote every condition above out as Prometheus rules and found that **four
of them read an instrument nothing publishes**. That is this section's own
callout coming true, at the moment the alerts stopped being a table and became
files — and it is recorded here rather than resolved by quietly shipping rules
that cannot fire.

| Alert | What is owed |
|---|---|
| Saga age | A gauge over `ordering.OrderFulfilmentStates`. §9.6 persists every saga, so the reading is a query away — there is simply no instrument over it |
| Orders awaiting review | A gauge over `ordering.OrderReviews`, which the `IX_OrderReviews_RaisedAt` index already exists for |
| Cache hit ratio collapse | **An instrument, and only an instrument — see the callout below.** §13.2 registers the `Microsoft.Extensions.Caching.Hybrid` meter and the package publishes no meter at all. The second half of this row used to be "no host calls `AddRedisConnections` either"; §8.5's PR wired both services, and the alert is exactly as unpublished as it was |
| Business volume | `OrderMetrics`, which arrives with §6.6's `OrderSummaries` projection |

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

> **The cache row is the one the self-clearing claim does not cover, and only
> reading the package settled why.** The other three are owed a `Create*` call
> the gate can see in `src/`. This one was first taken to be owed a
> **consumer** — at the time nothing called `AddRedisConnections`, so nothing
> constructed a `HybridCache` — and gating on that call was written, tested
> red, and then removed, because the premise is false.
>
> **§8.5's PR wired Redis into Catalog and Ordering and the row did not move**,
> which is the sharpest confirmation the removal was right: had the gate been
> kept, it would have gone green→red on the day the connection landed and moved
> a silent alert into the loaded file, while the meter still published nothing.
>
> **`Microsoft.Extensions.Caching.Hybrid` 10.0.0 publishes no `Meter`.** The
> assembly references `System.Diagnostics.Tracing` and not
> `System.Diagnostics.Metrics`: it reports through `HybridCacheEventSource`
> with `PollingCounter`, which is EventCounters. So the `AddMeter` line in
> §13.2 collects nothing today and would still collect nothing with Redis
> wired — **the registered-name trap this section warns about, in this
> platform's own configuration**, and it survived being written, reviewed and
> quoted because the name looks exactly like an instrument.
>
> A consumer is therefore necessary and not sufficient, and gating on one would
> have been worse than the gap: the gate would go red the day Redis was wired,
> somebody would move the rule into the loaded file, and it would sit there
> silent. What the row is owed is an **instrument** — an EventCounters bridge
> written here, which check 5 would see, or a package that publishes a meter,
> **which no gate in this repository can observe.** That second half is a
> residual, named rather than implied.

### The gap is per service as well as per metric

A fifth absence sits underneath all of this and is invisible to the checks
above, because they are about metric *names*. **The four loaded outbox alerts
group `by (service_name)`, and only Ordering publishes those gauges.** Catalog
hosts §9.4's dispatcher and registers no `OutboxMetrics`, so a stalled Catalog
lane is precisely the silent case this section exists to prevent — arriving
through a service missing from a series rather than through a metric nobody
declared.

It is not closed here because §13.3 places `OutboxMetrics` in
`Ordering.Infrastructure`, and moving it is a decision about **the template**:
Catalog is what §4.5's scaffold renders, so every new service inherits the gap
until the type is common and the scaffold emits it. Ordering is the one-off
that closed it for itself.

What this pull request does instead is refuse to let the absence be quiet.
`check.py` requires every service hosting the dispatcher to publish the gauges
**or** to be on a declared exemption with a reason, and it fails in both
directions — a new unexempted service, and a stale exemption for one that no
longer needs it. **A gap somebody argued is not the same as a dashboard nobody
noticed was empty**, and that distinction is the whole of what is being bought.

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

> **And today that row has no producer either, which is a second gap and a
> sharper one.** `MessagingMetrics.Projected` exists and `ProjectionInvoker`
> calls it — after a registered `IProjectionHandler<T>` succeeds. **No service
> registers one**: §6.6's `OrderSummaries` is not built, and Ordering's
> composition root says so in as many words. So the instrument is declared, the
> meter is collected, and nothing ever writes to it.
>
> That is the same shape §13.6 records for the HybridCache meter one section
> up — **a registered name is not a live signal** — and it is why
> `deploy/observability/slo/slo.js` names this row as not evaluated instead of
> asserting it. Asserting a row nothing can satisfy would fail every run on a
> healthy platform, which is how a gate gets switched off. The row stays in
> this table because it states the target the projection will be held to; what
> it does not yet do is measure anything.

**Broker-fed read-model staleness therefore has no SLO here, and the honest
move is to say so rather than to point at a row that nearly fits.** The
**event end-to-end** row above is the near miss: `IntegrationEventConsumer<T>`
records `messaging.delivery.lag` at the top of `Consume`, *before* it resolves
a handler, so the measurement stops where the projection starts. It excludes
the SQL round trip, every retry §9.8 schedules, and a handler that fails
terminally — which means that row can sit inside its two-second target while
`ordering.ProductPrices` is stale, or was never written at all. Adopting it
would restate this table's own defect one row over: a target that is met while
the thing it names is broken.

Closing it needs an instrument that fires *after* a broker-lane handler
commits — the `Projected` half of `MessagingMetrics` reaches only the local
lane today, because `ProjectionInvoker` is its only call site. That is a
§13.3 change with a dashboard behind it, so it belongs with the observability
work rather than with the service PR that exposed the gap. Until then this is
a **named** gap, which is the same standing as the two rows cut below: an SLO
nobody can compute is worse than an absence somebody has written down.

Two rows were removed rather than left unmeasurable. **Gateway added latency**
would need the gateway's own duration minus the backend's, correlated per
request — no single instrument produces it, and the number that was published
here could only ever have been guessed at. **Query p95 split by cache hit and
miss** needed a tag no query handler sets; the cache's own hit ratio (§13.6,
from the `Microsoft.Extensions.Caching.Hybrid` meter) answers the question the
split was really asking, which is whether the cache is working.

Cutting a row is the honest move when the alternative is a target nobody can
compute. An SLO that cannot be evaluated is not a weak SLO — it is a claim that
the service is meeting a bar nobody is checking.

Verify order-of-magnitude with the **k6 SLO run against staging**
([§15.1](15-cicd-deployment.md)) — `deploy/observability/slo/slo.js`, the load
run in CD, which asserts the four rows of this table it can evaluate — the two
request rows and the two outbox lanes, with the other three named below — and is
the
first real gate after the dev deploy. (NBomber was the stated alternative until
PR-24 picked one; a stage that names two tools names none.)

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

**Three of the seven are not evaluated there, and each is named in the script
rather than quietly dropped.** Cutting a row rather than pretending is this
table's own rule, applied to the gate that reads it — and a gate that fails on
a healthy platform is a gate that gets switched off:

| Row | Why the run cannot evaluate it |
|---|---|
| Availability | A **monthly** objective; a three-minute run cannot compute one. The run bounds its own error rate instead, says so, and reports no pass for the row |
| Read-model staleness, own events | `projection.lag` has **no producer** — nothing registers an `IProjectionHandler<T>`, per the callout above |
| Event end-to-end | `messaging.delivery.lag` is recorded by `IntegrationEventConsumer<T>`; the run places orders, and the consumer that records it handles Catalog's product events, which neither scenario produces |

The remaining four — the two request rows and the two outbox lanes — are what
the run actually asserts. Not a "smoke test": §15.1 declines to have
one and §12.1 gives the reason, which is that a stage named for what it actually
does gets maintained. This is also not a capacity test — it catches the
regression where a query loses its index and goes from 40 ms to 4 s, which no
unit test will find.

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

Since PR-24 that directory holds the alert rules and the SLO run on the same
argument, and a gate over all three:

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
| `docs/runbooks/outbox-broker.md` | Broker lane stalled: checking RabbitMQ reachability, credentials, queue limits; what downstream services are missing while it is stopped |
| `docs/runbooks/projection-lag.md` | Local lane stalled: finding the throwing handler, deciding whether to serve from the write model meanwhile, replaying a projection from scratch |
| `docs/runbooks/outbox-growth.md` | Total backlog rising: dispatcher liveness, batch sizing, and the retention purge as a *diagnostic* rather than a cause — a stopped purge deletes nothing and this gauge counts only unprocessed rows, so it cannot have moved it |
| `docs/runbooks/outbox-abandoned.md` | Rows past the attempt cap: reading the payload and `LastError`, deciding repair vs discard, resetting `Attempts` to replay |
| `docs/runbooks/stuck-saga.md` | Finding unfinalised sagas, reading their state, manual compensation |
| `docs/runbooks/order-review.md` | Working the `OrderReviews` queue: what each reason code means, how to resolve it, and deleting the row when done |
| `docs/runbooks/migration-failure.md` | A migration job that failed mid-deploy, and how to roll forward |
| `docs/runbooks/redis-cold.md` | Cache-loss load spike on the databases, and how to shed load while it warms |

Write each one when the corresponding alert is created, not after it first
fires. Every row above landed with PR-24 — including the four whose alert cannot
fire yet, because a procedure whose queries read tables that already exist is
useful before its pager is.

**The pairing is a gate, not a convention.** `deploy/observability/check.py`
fails the build on an alert whose `runbook_url` names a file that is not there,
on a runbook no alert points at, and on a runbook claimed by two alerts that
its `SHARED_RUNBOOKS` does not declare — §13.8's error-rate pair is the one
declared sharer, with its reason beside it, so a second is argued for in that
file rather than added. Both directions were observed red before the gate was
trusted, which is this repository's rule for any gate: the failure it exists to
catch has to have been seen.

`docs/runbooks/README.md` is the index and is excluded from the pairing by
name — one declared exception, so a second non-runbook file in that directory
has to be argued for rather than added.

---

[← §12 Test strategy](12-test-strategy.md) · [Index](README.md) · [§14 Local development →](14-local-development.md)

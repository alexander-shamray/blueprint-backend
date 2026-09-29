using System.Diagnostics.Metrics;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// §13.3's messaging instruments, on the <c>Commerce.Messaging</c> meter that
/// <c>AddObservability</c> already collects. Infrastructure rather than
/// Application because every call site is — the outbox dispatcher's invoker,
/// the two consumers beside this file, and §9.5's inbox filter one folder over.
/// </summary>
/// <remarks>
/// <b>It arrives in instalments on <c>PluggableInterfaces.All</c>'s terms, and
/// it is not closed.</b> <c>Projected</c> landed with the outbox because
/// <c>ProjectionInvoker</c> is its only call site, the next two with the
/// consumers that record them, and <see cref="Suppressed"/> when §9.5's silent
/// drop was given a signal (#64) — an instrument nothing writes to would be an
/// empty series on a dashboard rather than a signal, which is the rule that
/// decides when one may be added rather than a statement that four is the
/// number. This doc-comment read <i>complete since PR-15</i> until the fourth
/// arrived, which is what a closure claim is worth on a class whose members
/// are added by whoever needs one.
/// <para>
/// <b>The two lags read <c>OccurredAt</c> from different places, because they
/// measure different lanes.</b> <see cref="Delivered"/> reads it off the
/// message: it covers the broker lane, every integration event carries the
/// field (§9.1), and <c>IntegrationEventConsumer&lt;T&gt;</c> reaches it through
/// the <c>IIntegrationEvent</c> constraint. <see cref="Projected"/> reads it off
/// the outbox row, which the claim returns and which <c>Stage</c> copies from
/// the message rather than from a clock — it has to, because the local lane
/// carries domain events and <c>ProjectionInvoker</c> is deliberately
/// unconstrained.
/// </para>
/// <para>
/// Both lags compare a timestamp made on another machine, so both are useful at
/// second granularity and meaningless below it — which is why §13.7's targets
/// for them are in seconds. <see cref="Rejected"/> carries no such caveat: it
/// is recorded at the moment the dispatcher returns a failure, on the same
/// machine.
/// </para>
/// </remarks>
public sealed class MessagingMetrics
{
    /// <summary>
    /// The bucket bounds both lags are exported on, in seconds. Given as advice
    /// because the SDK gives seconds-scale defaults only to instruments it knows
    /// by name, and millisecond bounds put every lag under five seconds in one
    /// bucket, where a quantile reads the same whatever the lag was. §13.7's
    /// targets for both lags are among them, so a quantile against either is
    /// read at a bucket edge rather than interpolated across one.
    /// </summary>
    private static readonly InstrumentAdvice<double> LagBuckets = new()
    {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10, 30]
    };

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

    public void Delivered(string message, TimeSpan lag) =>
        _deliveryLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    public void Projected(string message, TimeSpan lag) =>
        _projectionLag.Record(lag.TotalSeconds, new KeyValuePair<string, object?>("message", message));

    /// <summary>
    /// A command the domain refused (§9.8). Feeds no SLO row — it is a business
    /// signal that happens to share this meter, and it belongs on a dashboard
    /// rather than on a pager.
    /// </summary>
    /// <remarks>
    /// <b><paramref name="error"/> is an <c>Error.Code</c> and nothing else.</b>
    /// It is tempting to reach for a cancellation reason here, and the two
    /// vocabularies describe opposite events: a payment-declined cancellation is
    /// a command the domain <em>accepted</em>, so it can never appear on a
    /// counter of commands it refused. Both are lowercase snake_case strings on
    /// a counter tagged <c>error</c>, which is what makes the mistake invisible
    /// in a dashboard. <c>Error.Code</c> is also a closed set by construction
    /// (§10.5), so the tag cannot explode.
    /// </remarks>
    public void Rejected(string message, string error) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("error", error));

    /// <summary>
    /// A message §9.5's inbox dropped because its id was already recorded for
    /// this endpoint (#64).
    /// </summary>
    /// <remarks>
    /// <b>The drop used to be a bare <c>return;</c>, and an invisible drop path
    /// is why the suppression had no signal anywhere in §13.</b> A duplicate
    /// the broker redelivered and a message suppressed by an id it has never
    /// legitimately carried are the same event from inside the filter, so
    /// neither is separable here — what this counter buys is that the class is
    /// measurable at all, and a rate that does not match the redelivery rate is
    /// the thing worth looking at.
    /// <para>
    /// <b>Neither tag is the <c>MessageId</c>, and that is the constraint
    /// rather than an omission.</b> A message id is unbounded, so it belongs in
    /// the log line the filter writes beside this and never on a series.
    /// <paramref name="message"/> is a type name and
    /// <paramref name="endpoint"/> a queue name; both are closed sets fixed at
    /// registration (§9.8), which is what keeps this counter from exploding.
    /// </para>
    /// </remarks>
    public void Suppressed(string message, string endpoint) =>
        _suppressed.Add(
            1,
            new KeyValuePair<string, object?>("message", message),
            new KeyValuePair<string, object?>("endpoint", endpoint));
}

using System.Diagnostics.Metrics;
using Common.Application;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Observability;

/// <summary>
/// §13.6's three per-lane outbox gauges. Infrastructure rather than
/// Application because it reads the database, which is also why the
/// instruments are observable rather than pushed (§13.3).
/// </summary>
/// <remarks>
/// <b>Singleton, and eagerly constructed by <see cref="MetricsInitialiser"/>.</b>
/// Observable gauges are callbacks held by the <see cref="Meter"/>: if this
/// object is never built, or is built and dropped, the instrument does not
/// exist and every alert reading it is silent — which on a dashboard is
/// indistinguishable from health.
/// <para>
/// <b>Two gauges over the same rows, because they answer different questions
/// and fail differently.</b> <c>outbox.oldest.age</c> catches a lane that has
/// <em>stopped</em>; <c>outbox.pending.count</c> catches one that is
/// <em>falling behind</em>. A single stuck row pins the age gauge at hours
/// while the count stays at one, and a backlog of ten thousand rows all
/// seconds old leaves the age gauge flat. §13.6's alerts read one each.
/// </para>
/// </remarks>
public sealed class OutboxMetrics
{
    /// <summary>
    /// The contract with §13.2's <c>AddMeter</c>. An instrument on an
    /// unregistered meter is collected by nothing and alerted on in vain, so
    /// this constant and the <c>AddMeter("Inventory.Outbox")</c> line in
    /// <c>ObservabilityExtensions</c> are one claim in two files — asserted by
    /// a test rather than left to agree by inspection.
    /// </summary>
    public const string MeterName = "Inventory.Outbox";

    /// <summary>
    /// The only thing that distinguishes a contained failure from a healthy
    /// quiet lane, because both are an absent series on the graph.
    /// </summary>
    /// <remarks>
    /// <c>LoggerMessage.Define</c> rather than an interpolated call, on the
    /// terms ADR-019 already settled for §6.3's <c>LoggingBehavior</c>: CA1848
    /// is an error here, and this runs on the collector's thread once per
    /// export interval for as long as the failure lasts.
    /// </remarks>
    private static readonly Action<ILogger, Exception?> GaugeReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(GaugeReadFailed)),
            "Outbox gauge read failed. PerLane runs once per gauge, so this "
            + "collection omits only that gauge's lane measurements, absent "
            + "rather than wrong — see OutboxMetrics.");

    public OutboxMetrics(IMeterFactory factory, IOutboxStats stats, ILogger<OutboxMetrics> logger)
    {
        Meter meter = factory.Create(MeterName);

        meter.CreateObservableGauge(
            "outbox.oldest.age",
            () => PerLane(stats.OldestAgeSeconds, logger),
            unit: "s",
            description: "Age of the oldest unprocessed row, per lane.");

        // Depth, per lane. The growth alert needs a count and the age gauge
        // cannot supply one — see the class remarks.
        meter.CreateObservableGauge(
            "outbox.pending.count",
            () => PerLane(lane => stats.PendingCount(lane), logger),
            unit: "{message}",
            description: "Unprocessed rows, per lane.");

        // Also per lane, and this is the one where it matters most: a Broker
        // abandonment means other services never learned something, a Local
        // one means this service's own read model is permanently wrong.
        // Different blast radius, different recovery, and outbox-abandoned.md
        // asks which one first.
        meter.CreateObservableGauge(
            "outbox.abandoned.count",
            () => PerLane(lane => stats.AbandonedCount(lane), logger),
            unit: "{message}",
            description: "Rows past the attempt cap, per lane.");
    }

    /// <summary>
    /// One measurement per lane, read from the enum rather than from a list
    /// written out here. A lane added to <see cref="OutboxLane"/> and forgotten
    /// at one of three call sites would be a lane with no gauge and therefore
    /// no alert, which is the silent gap §13.6 spends a callout on.
    /// </summary>
    /// <remarks>
    /// <b>The read is contained, because an observable callback that throws
    /// does not fail alone.</b> <c>MeterListener.RecordObservableInstruments</c>
    /// propagates the exception and abandons the rest of the pass, so a
    /// <c>SqlException</c> from one lane can stop <em>unrelated</em> observable
    /// instruments being collected — a database outage taking telemetry with
    /// it that has nothing to do with the database.
    /// <para>
    /// This repository proved that on itself:
    /// <c>A_foreign_meter_of_the_same_name_is_not_collected</c> only avoids a
    /// <c>SqlException</c> by never enabling the foreign callback, which is a
    /// demonstration that enabling it would have thrown through the collector.
    /// An earlier comment on <c>OutboxStats</c> claimed the SDK swallowed it;
    /// that was an assumption, and this makes it true by construction instead.
    /// </para>
    /// <para>
    /// Returning no measurements is the right failure for a <em>transient</em>
    /// outage: the series goes absent for that interval, and an outbox alert
    /// firing because SQL Server is briefly unreachable would page the wrong
    /// person with the wrong runbook.
    /// </para>
    /// <para>
    /// <b>A persistent failure is a different case, and containment alone does
    /// not cover it.</b> Schema drift, a revoked grant or a renamed table make
    /// every read fail for ever — and then all four outbox alerts are silent
    /// while the service stays ready, because §13.5's readiness check proves
    /// the connection opens and nothing about this table. An earlier version of
    /// this remark said readiness "already reports properly"; it reports
    /// connectivity, which is not the same claim. That is why the failure is
    /// <b>logged</b> rather than only swallowed: an empty outbox dashboard is
    /// indistinguishable from a healthy one, and the log is the only thing that
    /// tells them apart.
    /// </para>
    /// <para>
    /// <b>An alert on the absence itself would be the complete answer and is
    /// deliberately not here.</b> It needs a thirteenth alert, a thirteenth
    /// runbook and a row in §13.6's table — a chapter decision rather than a
    /// fix, and one taken at a review ceiling would be the worst moment for it.
    /// Named as owed, on the same terms as §13.6's four unloaded alerts.
    /// </para>
    /// </remarks>
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
                // Every lane is dropped, not just this one: half a reading is
                // worse than none, because a lane missing from a `max by (lane)`
                // reads as a healthy zero rather than as no data.
                GaugeReadFailed(logger, exception);
                return [];
            }

            measurements.Add(new Measurement<double>(value, Tag(lane)));
        }

        return measurements;
    }

    /// <summary>
    /// The tag value is the enum's own name, never a hand-written string. The
    /// <c>Lane</c> column stores <c>lane.ToString()</c> and §9.4's dispatcher
    /// compares against <c>"Broker"</c>, so a lowercase tag here would give one
    /// value three spellings across SQL, C# and PromQL — and an alert querying
    /// the wrong one matches no series and never fires, which looks exactly
    /// like health.
    /// </summary>
    private static KeyValuePair<string, object?> Tag(OutboxLane lane) =>
        new("lane", lane.ToString());
}

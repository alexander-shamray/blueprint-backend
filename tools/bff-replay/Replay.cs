using Common.Contracts;
using Common.Infrastructure.Outbox;
using Dapper;
using MassTransit;
using Microsoft.Data.SqlClient;

namespace BffReplay;

/// <summary>ADR-051's rebuild: every source answers, then an optional reset, then the eight are replayed.</summary>
public static class Replay
{
    /// <summary>The BFF's queue, held equal to the host's own constant by its suite.</summary>
    public const string Queue = "bff-order-events";

    /// <summary>How long the broker has to answer before anything is deleted.</summary>
    public static readonly TimeSpan BrokerDeadline = TimeSpan.FromSeconds(30);

    public static async Task<ReplayReport> RunAsync(
        ReplaySettings settings,
        bool reset,
        TextWriter output,
        TimeSpan brokerDeadline,
        CancellationToken ct)
    {
        MessageTypeMap types = new([typeof(IIntegrationEvent).Assembly]);
        OutboxJson json = new([]);
        List<SqlConnection> sources = [];
        IBusControl bus = Bus.Factory.CreateUsingRabbitMq(cfg => cfg.Host(new Uri(settings.Broker)));
        bool started = false;

        try
        {
            // The BFF's database answers first, so neither a repair nor a reset meets it broken mid-run.
            await using (SqlConnection bff = new(settings.Bff))
            {
                await bff.OpenAsync(ct);
                await bff.ExecuteAsync(new CommandDefinition(ProjectionReset.ProbeSql, cancellationToken: ct));
            }

            // Every source answers before the reset, so a reset never precedes a read that cannot run.
            foreach (PublisherConnection source in settings.Publishers)
            {
                SqlConnection connection = new(source.ConnectionString);
                sources.Add(connection);
                await connection.OpenAsync(ct);
                await connection.ExecuteAsync(
                    new CommandDefinition(OutboxRows.ProbeSql(source.Publisher), cancellationToken: ct));
            }

            started = await StartAsync(bus, settings.Broker, brokerDeadline, ct);

            ISendEndpoint endpoint = await bus.GetSendEndpoint(new Uri($"queue:{Queue}"));

            if (reset)
            {
                // A row that cannot be decoded fails here, since the same row after the delete would fail every rerun.
                for (int i = 0; i < settings.Publishers.Count; i++)
                {
                    Publisher publisher = settings.Publishers[i].Publisher;
                    string[] names = [.. ReplayedEvents.PublishedBy(publisher).Select(types.NameOf)];

                    IAsyncEnumerable<OutboxRow> rows = OutboxRows.ReadAsync(sources[i], publisher, names);
                    await foreach (OutboxRow row in rows.WithCancellation(ct))
                        ReplayPayload.Read(row, types, json);
                }

                await ProjectionReset.RunAsync(settings.Bff, settings.Broker, ct);
                await output.WriteLineAsync($"Reset: the order rows and {Queue}'s inbox rows are deleted.");
            }

            // A copy the inbox would drop is dropped before the queue, where it would land in _skipped, which pages.
            HashSet<Guid> handled = reset ? [] : await ProjectionReset.HandledAsync(settings.Bff, settings.Broker, ct);
            ReplayReport report = new();

            for (int i = 0; i < settings.Publishers.Count; i++)
            {
                Publisher publisher = settings.Publishers[i].Publisher;
                string[] names = [.. ReplayedEvents.PublishedBy(publisher).Select(types.NameOf)];

                await foreach (OutboxRow row in OutboxRows.ReadAsync(sources[i], publisher, names).WithCancellation(ct))
                {
                    if (handled.Contains(row.MessageId))
                    {
                        report.Skipped();
                        continue;
                    }

                    IIntegrationEvent message = ReplayPayload.Read(row, types, json);

                    // To the queue alone; the grant would refuse a contract's exchange in any case (ADR-036).
                    await endpoint.Send(
                        message,
                        message.GetType(),
                        c =>
                        {
                            c.MessageId = row.MessageId;
                            c.CorrelationId = row.CorrelationId;
                        },
                        ct);

                    report.Sent(publisher, row.MessageType, row);
                }

                await output.WriteLineAsync(report.Describe(publisher));
            }

            foreach ((string type, int count) in report.SentByType)
                await output.WriteLineAsync($"  {type}: {count}");

            if (!reset)
                await output.WriteLineAsync($"Skipped {report.SkippedCount} event(s) the BFF's inbox had handled.");

            return report;
        }
        finally
        {
            if (started)
                await bus.StopAsync(CancellationToken.None);

            foreach (SqlConnection connection in sources)
                await connection.DisposeAsync();
        }
    }

    // Bounded, so an unreachable broker refuses the run here rather than retrying behind a deleted projection.
    private static async Task<bool> StartAsync(
        IBusControl bus,
        string broker,
        TimeSpan deadline,
        CancellationToken ct)
    {
        string host = new Uri(broker).Host;

        try
        {
            using CancellationTokenSource answered = CancellationTokenSource.CreateLinkedTokenSource(ct);
            answered.CancelAfter(deadline);
            await bus.StartAsync(answered.Token);
        }
        catch (Exception exception) when (
            exception is RabbitMqConnectionException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The type is named and the inner exception is not carried: a fault's text can print the address, and
            // the address holds the account's credential.
            throw new InvalidOperationException(
                $"The broker at {host} did not answer within {deadline} ({exception.GetType().Name}), so nothing " +
                "was deleted or sent.");
        }

        BusHealthStatus health = await bus.WaitForHealthStatus(BusHealthStatus.Healthy, deadline);
        if (health != BusHealthStatus.Healthy)
        {
            await bus.StopAsync(CancellationToken.None);
            throw new InvalidOperationException(
                $"The broker at {host} did not answer within {deadline} (the bus reads {health}), so nothing " +
                "was deleted or sent.");
        }

        return true;
    }
}

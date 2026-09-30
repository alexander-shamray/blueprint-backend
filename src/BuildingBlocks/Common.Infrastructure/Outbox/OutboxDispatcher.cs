using System.Data;
using System.Text.Json;
using Common.Application;
using Common.Contracts;
using Common.Domain;
using Dapper;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Outbox;

/// <summary>§9.4's dispatcher: an atomic claim that leases a batch, then delivery that fails per row.</summary>
public sealed class OutboxDispatcher : BackgroundService
{
    /// <summary>§9.4's attempt cap, public so §13.6's abandoned-rows gauge reads this number, not a copy.</summary>
    public const int MaxAttempts = 10;

    /// <summary>How many rows one claim leases, public so §13.6's runbook sizes this number, not a copy.</summary>
    public const int ClaimBatchSize = 100;

    /// <summary>A claim's lease: longer than a slow batch, short enough for a killed replica.</summary>
    public const int LeaseSeconds = 60;

    /// <summary>The backoff: <c>2^min(Attempts, BackoffAttemptCap) × BackoffBaseSeconds</c> seconds.</summary>
    /// <remarks>The cap keeps <see cref="MaxAttempts"/> near enough for §13.6's alert to act on.</remarks>
    public const int BackoffBaseSeconds = 5;

    /// <inheritdoc cref="BackoffBaseSeconds"/>
    public const int BackoffAttemptCap = 8;

    /// <summary>How often the dispatcher polls, which §13.7's <c>projection.lag</c> target leaves room for.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // Compiled once, for CA1848 (ADR-019): this loop runs every PollInterval.
    private static readonly Action<ILogger, Exception?> ClaimFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(1, nameof(ClaimFailed)),
            "Outbox claim failed; retrying next tick.");

    private static readonly Action<ILogger, Guid, string, int, int, Exception?> DeliveryFailed =
        LoggerMessage.Define<Guid, string, int, int>(
            LogLevel.Error,
            new EventId(2, nameof(DeliveryFailed)),
            "Outbox message {MessageId} on lane {Lane} failed, attempt {Attempt} of {Max}.");

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxDispatcher> _log;

    // Composed once from the registered table, which is why these are fields, not consts.
    private readonly string _claimSql;
    private readonly string _completeSql;
    private readonly string _failSql;

    public OutboxDispatcher(IServiceScopeFactory scopes, OutboxTable table, ILogger<OutboxDispatcher> log)
    {
        _scopes = scopes;
        _log = log;

        // Selects and leases in one statement, so two replicas cannot take the same row.
        _claimSql =
            $"""
            WITH claimable AS (
                SELECT TOP ({ClaimBatchSize}) *
                FROM {table.QualifiedName} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAt IS NULL
                    AND Attempts < @MaxAttempts
                    AND (LockedUntil IS NULL OR LockedUntil < SYSDATETIMEOFFSET())
                ORDER BY OccurredAt
            )
            UPDATE claimable
            SET LockedUntil = DATEADD(second, {LeaseSeconds}, SYSDATETIMEOFFSET())
            OUTPUT
                inserted.Id,
                inserted.MessageId,
                inserted.CorrelationId,
                inserted.MessageType,
                inserted.Payload,
                inserted.Lane,
                inserted.Attempts,
                inserted.OccurredAt;
            """;

        _completeSql =
            $"""
            UPDATE {table.QualifiedName}
            SET ProcessedAt = SYSDATETIMEOFFSET(), LockedUntil = NULL
            WHERE Id = @Id;
            """;

        // Backs off by pushing the lease forward, which makes the cap reachable.
        _failSql =
            $"""
            UPDATE {table.QualifiedName}
            SET
                Attempts    = Attempts + 1,
                LastError   = LEFT(@Error, {OutboxMessage.LastErrorMaxLength}),
                LockedUntil = DATEADD(
                    second,
                    POWER(2, CASE WHEN Attempts > {BackoffAttemptCap}
                                  THEN {BackoffAttemptCap}
                                  ELSE Attempts END) * {BackoffBaseSeconds},
                    SYSDATETIMEOFFSET())
            WHERE Id = @Id;
            """;
    }

    // stoppingToken, not ct: CA1725 keeps the base's name, an error under ADR-019.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The claim failed. The token, not the type: a projection's own deadline throws the same type.
                ClaimFailed(_log, ex);
            }
        }
    }

    /// <summary>One claim-and-deliver pass, public so tests drive it rather than race a timer (§12.4).</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        // The claim's scope holds only the connection, which outlives the per-row delivery scopes.
        await using AsyncServiceScope claimScope = _scopes.CreateAsyncScope();

        using IDbConnection connection =
            claimScope.ServiceProvider.GetRequiredService<IDbConnectionFactory>().Create();

        // CommandDefinition, so a shutdown's token reaches a blocked claim.
        List<OutboxClaim> claimed =
        [
            .. await connection.QueryAsync<OutboxClaim>(
                new CommandDefinition(_claimSql, new { MaxAttempts }, cancellationToken: ct))
        ];

        int completed = 0;

        foreach (OutboxClaim message in claimed)
        {
            try
            {
                // A scope per row, so a handler that throws cannot hand the next row its half-mutated state.
                await using AsyncServiceScope delivery = _scopes.CreateAsyncScope();

                await DeliverAsync(delivery.ServiceProvider, message, ct);

                await connection.ExecuteAsync(
                    new CommandDefinition(_completeSql, new { message.Id }, cancellationToken: ct));
                completed++;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One bad message does not affect the rest; the token, not the type, as above.
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        _failSql, new { message.Id, Error = ex.ToString() }, cancellationToken: ct));

                DeliveryFailed(_log, message.MessageId, message.Lane, message.Attempts + 1, MaxAttempts, ex);
            }
        }

        return completed;
    }

    private static async Task DeliverAsync(IServiceProvider sp, OutboxClaim message, CancellationToken ct)
    {
        // Through the map, not Type.GetType, so a name survives an assembly version bump (§9.4).
        Type type = sp.GetRequiredService<MessageTypeMap>().Resolve(message.MessageType);

        // The registered options Stage wrote through, converters included (§9.4).
        object payload = JsonSerializer.Deserialize(
            message.Payload,
            type,
            sp.GetRequiredService<OutboxJson>().Options)!;

        if (message.Lane == nameof(OutboxLane.Broker))
        {
            // Checked again: the row's lane and type were never validated by this process (§5.5).
            if (payload is not IIntegrationEvent)
            {
                throw new InvalidOperationException(
                    $"Outbox row {message.MessageId} is on the Broker lane carrying " +
                    $"{type.Name}, which is not an {nameof(IIntegrationEvent)}. Publishing it " +
                    "would put a domain event on the bus (§5.5).");
            }

            await sp.GetRequiredService<IPublishEndpoint>().Publish(
                payload,
                type,
                c =>
                {
                    c.MessageId = message.MessageId;
                    c.CorrelationId = message.CorrelationId;
                },
                ct);
            return;
        }

        if (message.Lane != nameof(OutboxLane.Local))
        {
            throw new InvalidOperationException(
                $"Outbox row {message.MessageId} carries lane '{message.Lane}', which is neither " +
                "Broker nor Local. The row is left for the §13.6 abandoned-row alert rather than " +
                "guessed at.");
        }

        // Local lane: projections outside the write transaction (§7.5), timed from the row's OccurredAt (§13.3).
        // The Broker guard's mirror: an unconstrained invoker would project anything the row carries.
        if (payload is not IDomainEvent)
        {
            throw new InvalidOperationException(
                $"Outbox row {message.MessageId} is on the Local lane carrying {type.Name}, " +
                $"which is not an {nameof(IDomainEvent)}. Projections run on domain events " +
                "(§7.5).");
        }

        await ProjectionInvoker.InvokeAllAsync(sp, payload, type, message.OccurredAt, ct);
    }
}

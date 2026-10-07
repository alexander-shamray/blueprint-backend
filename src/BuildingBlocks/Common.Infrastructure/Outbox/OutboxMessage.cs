using System.Diagnostics;
using System.Text.Json;
using Common.Application;
using Common.Contracts;
using Common.Domain;

namespace Common.Infrastructure.Outbox;

/// <summary>The staging path's whole row (§9.4); <see cref="OutboxClaim"/> is the dispatcher's read of it.</summary>
public sealed class OutboxMessage
{
    /// <summary>The widest <c>LastError</c> the column holds, and the dispatcher's <c>LEFT</c> width.</summary>
    public const int LastErrorMaxLength = 2000;

    /// <summary>The widest <see cref="OutboxLane"/> name the column holds.</summary>
    public const int LaneMaxLength = 16;

    /// <summary>A W3C <c>traceparent</c> at version 00, the only version an activity writes.</summary>
    public const int TraceParentMaxLength = 55;

    /// <summary>The W3C ceiling on <c>tracestate</c>; a longer one is not staged rather than truncated.</summary>
    public const int TraceStateMaxLength = 512;

    public long Id { get; private set; }

    public Guid MessageId { get; private set; }

    public Guid CorrelationId { get; private set; }

    public string MessageType { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public OutboxLane Lane { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>The trace that staged the row, which the dispatcher restores as its delivery's parent (§9.4).</summary>
    /// <remarks>Null on a row staged with no W3C activity, or before the column existed (§7.4).</remarks>
    public string? TraceParent { get; private set; }

    public string? TraceState { get; private set; }

    public static OutboxMessage Stage(
        object message,
        OutboxLane lane,
        Guid correlationId,
        MessageTypeMap types,
        OutboxJson json)
    {
        // One identity: an integration event's envelope ids go to the transport the inbox dedupes on (§9.5).
        // A Local row carries a domain event, with no envelope, so it mints its own id.
        // The lane checks below make §9.3's allow-list structural; first, C# does not confine an enum to its members.
        if (lane is not (OutboxLane.Broker or OutboxLane.Local))
        {
            throw new InvalidOperationException(
                $"{lane} is not a lane. A row carries Broker or Local (§9.4), and one that " +
                "carries neither can only be discovered after it is committed.");
        }

        // Before either lane check, since a type that is both would pass both (§5.5).
        if (message is IDomainEvent and IIntegrationEvent)
        {
            throw new InvalidOperationException(
                $"{message.GetType().Name} implements {nameof(IDomainEvent)} and " +
                $"{nameof(IIntegrationEvent)}. They are different things (§5.5): one is internal " +
                "and free to change, the other is a published contract. A type that is both can " +
                "be staged on either lane and is correct on neither.");
        }

        if (lane is OutboxLane.Broker && message is not IIntegrationEvent)
        {
            throw new InvalidOperationException(
                $"{message.GetType().Name} is not an {nameof(IIntegrationEvent)} and cannot be " +
                "staged on the Broker lane. A domain event reaching the broker is the leak the " +
                "§9.3 allow-list exists to prevent — map it to a contract first.");
        }

        if (lane is OutboxLane.Local && message is not IDomainEvent)
        {
            throw new InvalidOperationException(
                $"{message.GetType().Name} is not an {nameof(IDomainEvent)} and cannot be staged " +
                "on the Local lane, which carries this service's own events to its projection " +
                "handlers (§7.5).");
        }

        // The staging request's trace, read here because the row is written in that request's transaction.
        Activity? staging = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current : null;

        return new OutboxMessage
        {
            MessageId = message is IIntegrationEvent e ? e.MessageId : Guid.CreateVersion7(),
            CorrelationId = message is IIntegrationEvent c ? c.CorrelationId : correlationId,
            MessageType = types.NameOf(message.GetType()),
            Payload = JsonSerializer.Serialize(message, message.GetType(), json.Options),
            Lane = lane,

            // The message's own timestamp, never the staging clock, as §13.7's projection.lag requires.
            OccurredAt = message is IIntegrationEvent o
                ? o.OccurredAt
                : ((IDomainEvent)message).OccurredAt,

            TraceParent = staging?.Id,
            TraceState = staging?.TraceStateString is { Length: <= TraceStateMaxLength } state ? state : null
        };
    }
}

using System.Text.Json;
using Common.Contracts;
using Common.Infrastructure.Outbox;

namespace BffReplay;

/// <summary>Turns a row back into its publisher's event, through the map and serialiser that wrote it.</summary>
public static class ReplayPayload
{
    public static IIntegrationEvent Read(OutboxRow row, MessageTypeMap types, OutboxJson json)
    {
        Type type = types.Resolve(row.MessageType);
        object? payload = JsonSerializer.Deserialize(row.Payload, type, json.Options);

        // The row's id is the one the BFF's inbox keys on (§9.5), so a payload naming another is a stranger.
        return payload is IIntegrationEvent message && message.MessageId == row.MessageId
            ? message
            : throw new InvalidOperationException(
                $"Outbox row {row.Id} names message {row.MessageId}, and its payload reads as " +
                $"{(payload as IIntegrationEvent)?.MessageId.ToString() ?? "no integration event"}. A replay " +
                "sends the row's own message or nothing, so this row stops the run.");
    }
}

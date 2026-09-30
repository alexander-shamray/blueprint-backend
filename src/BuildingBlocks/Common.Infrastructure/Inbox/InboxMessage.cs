namespace Common.Infrastructure.Inbox;

/// <summary>§9.5's inbox row: one message, on one receive endpoint, already handled.</summary>
/// <remarks>Keyed on endpoint, not type or handler, since one type may bind to several endpoints (§9.5).</remarks>
public sealed class InboxMessage(Guid messageId, string endpoint, DateTimeOffset handledAt)
{
    /// <summary>The widest endpoint address the column holds, which keeps the composite key under 900 bytes.</summary>
    public const int EndpointMaxLength = 300;

    public Guid MessageId { get; private set; } = messageId;

    public string Endpoint { get; private set; } = endpoint;

    public DateTimeOffset HandledAt { get; private set; } = handledAt;
}

namespace Common.Assistant;

/// <summary>
/// The bounds the core enforces (AI assistant design §9). A host sets them from its own configuration; the defaults are
/// the builder's call (design §16).
/// </summary>
public sealed record AssistantLimits
{
    /// <summary>Tool rounds in one turn; a turn that asks for more ends with <c>max_steps</c>.</summary>
    public int MaxSteps { get; init; } = 8;

    /// <summary>
    /// The output bound of one provider round. A turn's spend is bounded by <see cref="MaxSteps"/> × this and by
    /// <see cref="ConversationTokenBudget"/>.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>
    /// The context a conversation may reach: its latest request's input tokens, cached ones included. A turn ends with
    /// <c>budget</c> before the round after the one that reached it, which is still sent, and a later turn at once,
    /// holding none of its text. Not a spend cap.
    /// </summary>
    public long ConversationTokenBudget { get; init; } = 150_000;

    /// <summary>How long a client tool call waits for its result before it is an error result.</summary>
    public TimeSpan ClientToolTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a server tool call runs before it is an error result.</summary>
    public TimeSpan ServerToolTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Open conversations per caller; the least recently used idle one makes room for a new one.</summary>
    public int MaxConversations { get; init; } = 8;

    /// <summary>Callers the store holds at once.</summary>
    public int MaxCallers { get; init; } = 64;

    /// <summary>
    /// The characters a caller's conversations hold together: each message as sent, and the caller's text and replies
    /// again for the conversation read. A turn or round that would start past it forgets the caller's least recently
    /// used idle conversation, or ends with <c>budget</c> (ADR-208).
    /// </summary>
    public long CallerHistoryChars { get; init; } = 2_000_000;

    /// <summary>How long an idle conversation is kept.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Events a conversation's stream can resume from; the conversation read holds every message regardless.
    /// </summary>
    public int EventRing { get; init; } = 1024;

    /// <summary>
    /// Whether a provider failure's redacted message goes to the client in the error event's <c>detail</c>. Off, the
    /// event carries its code and title alone and the host learns the failure's type from
    /// <see cref="TurnSummary.Failure"/>; a loopback operator console turns it on.
    /// </summary>
    public bool ErrorDetail { get; init; }

    /// <summary>The size bound on one tool result, in characters, after redaction.</summary>
    public int MaxToolResultChars { get; init; } = 16_000;
}

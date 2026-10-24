using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Common.Assistant.Tools;

namespace Common.Assistant.Protocol;

/// <summary>
/// One Server-Sent Event of a conversation (AI assistant design §5.3): <see cref="EventName"/> is the SSE event name
/// and the record the data. Every sentence-like field is a code the client words itself (§5.4).
/// </summary>
public abstract record AssistantEvent
{
    [JsonIgnore]
    public abstract string EventName { get; }
}

/// <summary>A delta of the reply.</summary>
public sealed record TextEvent(string Delta) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "text";
}

/// <summary>A tool call began. <see cref="Args"/> holds only the few input values a label shows.</summary>
public sealed record ToolStartedEvent(string CallId, string Name, ToolSide Side, JsonObject Args) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "tool_started";
}

public sealed record ToolFinishedEvent(string CallId, bool Ok) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "tool_finished";
}

/// <summary>The client runs this and posts the result to <c>…/tool-results</c>.</summary>
public sealed record ClientToolCallEvent(string CallId, string Name, JsonObject Input) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "client_tool_call";
}

/// <summary>
/// The turn is over, by <see cref="TurnEndReason"/>, with the provider's refusal category if it gave one, this turn's
/// usage and the conversation's running total, so spend is visible (§9).
/// </summary>
public sealed record TurnEndedEvent(string Reason, string? Category, Usage Usage, Usage Total) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "turn_ended";
}

/// <summary>An RFC 9457 problem, as the rest of <c>/api</c> answers.</summary>
public sealed record ErrorEvent(string Type, string Title, int Status, string? Detail) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "error";
}

/// <summary>
/// Reserved (§6.3): an action awaiting the operator's confirmation. Never emitted until actions are allowed, and
/// a client ignores it until then.
/// </summary>
public sealed record ConfirmationRequiredEvent(
    string CallId,
    string Name,
    JsonObject Args,
    string? Confirmation) : AssistantEvent
{
    [JsonIgnore]
    public override string EventName => "confirmation_required";
}

/// <summary>Tokens: input, output, and input read from the provider's cache.</summary>
public sealed record Usage(long Input, long Output, long Cached)
{
    public static Usage None { get; } = new(0, 0, 0);

    public static Usage operator +(Usage a, Usage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.Cached + b.Cached);
}

/// <summary>Why a turn ended (§5.3).</summary>
public static class TurnEndReason
{
    public const string Done = "done";
    public const string MaxSteps = "max_steps";
    public const string Budget = "budget";
    public const string Cancelled = "cancelled";
    public const string Refused = "refused";
    public const string Error = "error";
}

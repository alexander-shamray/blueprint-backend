using System.Text.Json.Nodes;

namespace Common.Assistant.Providers;

/// <summary>
/// One message of a conversation's history (AI assistant design §7). The history is append-only: nothing earlier is
/// edited, reordered or trimmed, and an assistant message's opaque blocks go back to the adapter exactly as it gave
/// them.
/// </summary>
public abstract record HistoryMessage;

/// <summary>
/// The caller's message as the provider is sent it: redacted, with the turn's context and the language rule.
/// </summary>
public sealed record UserMessage(string Content) : HistoryMessage;

/// <summary>
/// One provider round's reply: its text, the tools it asked for, and the blocks only its adapter reads.
/// </summary>
public sealed record AssistantMessage(
    string Text,
    IReadOnlyList<ToolCall> Calls,
    IReadOnlyList<JsonNode> Opaque) : HistoryMessage;

/// <summary>Every result of one tool round, in one message, in the order the calls were asked for.</summary>
public sealed record ToolResultsMessage(IReadOnlyList<ToolResult> Results) : HistoryMessage;

/// <summary>A tool call's result, redacted and cut to the size bound.</summary>
public sealed record ToolResult(string CallId, bool Ok, string Content);

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Common.Assistant.Tools;

namespace Common.Assistant.Protocol;

// The bodies of /api/assistant (AI assistant design §5.1), camelCase JSON with web defaults. These records own the
// protocol: the browser client's protocol.ts mirrors them, and the golden files under Protocol/golden/ pin the JSON
// both sides read and write (design §14).

/// <summary><c>GET /api/assistant</c>. Never the credential, nor where it came from.</summary>
public sealed record AssistantStatus(bool Enabled, string? Provider, string? Model, string? Reason)
{
    public static AssistantStatus Off { get; } = new(false, null, null, StatusReason.Off);
}

/// <summary>Why the assistant is not enabled, as a code the client words in its own language (design §5.4).</summary>
public static class StatusReason
{
    public const string Off = "off";
    public const string NoCredential = "no-credential";
    public const string Unreachable = "unreachable";
}

/// <summary>Which client opened a conversation.</summary>
public sealed record ClientInfo(string Name, string Version);

/// <summary>
/// One thing the client can explain (design §6.1): its id, its label as the client shows it in the active locale,
/// where it lives, and its help. The client's registry owns the text; the client sends it.
/// </summary>
public sealed record CatalogueEntry(string Id, string Label, string Where, string Help);

/// <summary><c>POST /api/assistant/conversations</c>.</summary>
public sealed record OpenConversation(
    ClientInfo Client,
    string Locale,
    IReadOnlyList<CatalogueEntry> Catalogue,
    IReadOnlyList<ToolDescriptor> ClientTools,
    JsonObject? HostOptions);

public sealed record ConversationOpened(string Id);

/// <summary>
/// <c>POST …/turns</c>: the caller's message, what they are looking at, and optionally a new locale or host options.
/// </summary>
public sealed record StartTurn(string Text, JsonObject? Context, string? Locale, JsonObject? HostOptions);

/// <summary><c>POST …/tool-results</c>: the result of a client tool call.</summary>
public sealed record ClientToolResult(string CallId, bool Ok, string Content);

/// <summary>
/// <c>GET …/{id}</c>: the conversation as the client shows it, and the event id its stream continues from. No
/// provider-opaque block and no tool result body.
/// </summary>
public sealed record ConversationView(
    string Id,
    IReadOnlyList<ConversationItem> Items,
    bool Running,
    long LastEventId,
    // The client tool calls still waiting for a result, in the event's own shape: a client reloaded after one was
    // emitted resumes past it, so this is how it answers them.
    IReadOnlyList<ClientToolCallEvent> PendingCalls);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(OperatorItem), "operator")]
[JsonDerivedType(typeof(ReplyItem), "reply")]
[JsonDerivedType(typeof(ToolItem), "tool")]
[JsonDerivedType(typeof(EndItem), "end")]
public abstract record ConversationItem;

/// <summary>The caller's message, as they wrote it.</summary>
public sealed record OperatorItem(string Text) : ConversationItem;

/// <summary>The text of one provider round of a reply.</summary>
public sealed record ReplyItem(string Text) : ConversationItem;

/// <summary>A tool call: its label's name and args, and its outcome once it has one.</summary>
public sealed record ToolItem(string CallId, string Name, ToolSide Side, JsonObject Args, bool? Ok) : ConversationItem;

/// <summary>Where a turn ended, by <see cref="TurnEndReason"/>.</summary>
public sealed record EndItem(string Reason, string? Category) : ConversationItem;

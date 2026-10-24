using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Common.Assistant.Tools;

/// <summary>
/// A tool in the Model Context Protocol's tool shape (AI assistant design §6), plus the core's own <see cref="Side"/>,
/// so the MCP route of §12 is a transport and not a remodel. Names are snake_case and unique across both sides.
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    ToolAnnotations Annotations,
    ToolSide Side);

/// <summary>MCP's tool annotations. §6.3 refuses a tool that is not read-only and not destructive.</summary>
public sealed record ToolAnnotations(bool ReadOnlyHint, bool DestructiveHint, bool IdempotentHint, bool OpenWorldHint);

/// <summary>Who runs a tool: the host, or the client the conversation was opened from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolSide>))]
public enum ToolSide
{
    [JsonStringEnumMemberName("server")]
    Server,

    [JsonStringEnumMemberName("client")]
    Client,
}

/// <summary>
/// What a tool call came back with: an error result is one the model reads, never an exception that ends the turn.
/// </summary>
public sealed record ToolOutcome(bool Ok, string Content);

/// <summary>
/// What a host's tool call knows (§5.2): the caller and the conversation, so a source can scope its results, and the
/// client's <c>hostOptions</c>, handed through untouched.
/// </summary>
public sealed record ToolContext(Caller Caller, string ConversationId, JsonObject HostOptions);

/// <summary>
/// A host's read-only tools (§6.2). A source names the read each tool mirrors in its own doc comment.
/// </summary>
public interface IToolSource
{
    IReadOnlyList<ToolDescriptor> Tools { get; }

    ValueTask<ToolOutcome> InvokeAsync(
        string name,
        JsonObject input,
        ToolContext context,
        CancellationToken cancellationToken);
}

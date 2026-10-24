using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Tools;

namespace Common.Assistant.Providers;

/// <summary>
/// One model behind one interface (AI assistant design §8.1). An adapter does one provider round per
/// <see cref="TurnAsync"/>, translating descriptors to its API's tool format and back; nothing above it sees a vendor
/// type. The loop around the rounds is <see cref="AssistantSession"/>'s.
/// </summary>
public interface IAssistantProvider
{
    string Id { get; }

    ValueTask<ProviderStatus> StatusAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderEvent> TurnAsync(TurnRequest request, CancellationToken cancellationToken);
}

/// <summary>Whether the provider can take a turn, with which model, and if not, a <see cref="StatusReason"/>.</summary>
public sealed record ProviderStatus(bool Available, string Model, string? Reason);

/// <summary>
/// One provider round, in cache order (design §7): the tools, the fixed system text and the catalogue (which do not
/// change between rounds), then the history. The turn's context and the language rule are already in the last user
/// message.
/// </summary>
public sealed record TurnRequest(
    IReadOnlyList<ToolDescriptor> Tools,
    string System,
    IReadOnlyList<CatalogueEntry> Catalogue,
    IReadOnlyList<HistoryMessage> History,
    int MaxOutputTokens);

public abstract record ProviderEvent;

/// <summary>A delta of the reply's text.</summary>
public sealed record TextDelta(string Text) : ProviderEvent;

/// <summary>The model asks for a tool: the id its result must carry, the tool's name and its input.</summary>
public sealed record ToolCall(string Id, string Name, JsonObject Input) : ProviderEvent;

/// <summary>
/// Blocks the adapter must be handed back unchanged on its assistant message (a model's reasoning among them); the core
/// keeps them and never reads them.
/// </summary>
public sealed record Opaque(JsonNode Payload) : ProviderEvent;

public sealed record UsageReport(Usage Usage) : ProviderEvent;

/// <summary>
/// The round is over: done (with or without tool calls), or refused, with the provider's category if any.
/// </summary>
public sealed record RoundEnd(ProviderStop Stop, string? Category = null) : ProviderEvent;

public enum ProviderStop
{
    Done,
    Refused,
}

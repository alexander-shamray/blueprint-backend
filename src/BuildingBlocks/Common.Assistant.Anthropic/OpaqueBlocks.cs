using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;

namespace Common.Assistant.Anthropic;

/// <summary>
/// The blocks the model returns that only this adapter reads (AI assistant design §7): its reasoning, signed and
/// redacted. The core keeps each as the JSON payload written here and hands it back on the assistant message, and
/// <see cref="ToParam"/> turns it into the request block it came from, byte for byte.
/// </summary>
internal static class OpaqueBlocks
{
    public const string Thinking = "thinking";
    public const string RedactedThinking = "redacted_thinking";

    public static JsonObject OfThinking(string thinking, string signature) =>
        new() { ["type"] = Thinking, ["thinking"] = thinking, ["signature"] = signature };

    public static JsonObject OfRedacted(string data) => new() { ["type"] = RedactedThinking, ["data"] = data };

    /// <summary>
    /// The request block for a payload this adapter wrote, or null for one it did not (which is left out).
    /// </summary>
    public static BetaContentBlockParam? ToParam(JsonNode payload)
    {
        // Byte for byte, because current Claude models reject or drop reasoning whose history was edited.
        if (payload is not JsonObject block)
        {
            return null;
        }

        return (string?)block["type"] switch
        {
            Thinking when block["thinking"] is JsonValue text && block["signature"] is JsonValue signature =>
                new BetaThinkingBlockParam { Thinking = (string)text!, Signature = (string)signature! },
            RedactedThinking when block["data"] is JsonValue data =>
                new BetaRedactedThinkingBlockParam { Data = (string)data! },
            _ => null,
        };
    }
}

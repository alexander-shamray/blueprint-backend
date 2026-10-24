using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Anthropic.Models.Beta.Messages;

namespace Common.Assistant.Anthropic;

/// <summary>
/// One provider round's stream, as the core reads it (AI assistant design §8.2): text as it arrives, each tool call
/// once its input is whole, the reasoning blocks as opaque payloads, one usage report and the end. A stream is mapped
/// by one instance; it keeps what a block needs from its start to its stop.
/// </summary>
internal sealed class StreamMapper
{
    private readonly Dictionary<long, Pending> blocks = [];

    private long fresh;
    private long created;
    private long output;
    private long cached;
    private string? stop;
    private string? category;

    /// <summary>Whether the stream reached its <c>message_stop</c>; one that ends without it was cut off.</summary>
    public bool Stopped { get; private set; }

    public IEnumerable<ProviderEvent> On(BetaRawMessageStreamEvent streamEvent)
    {
        if (streamEvent.TryPickStart(out BetaRawMessageStartEvent? start))
        {
            Take(
                start.Message.Usage.InputTokens,
                start.Message.Usage.CacheCreationInputTokens,
                start.Message.Usage.CacheReadInputTokens,
                start.Message.Usage.OutputTokens);
        }
        else if (streamEvent.TryPickContentBlockStart(out BetaRawContentBlockStartEvent? begun))
        {
            Begin(begun);
        }
        else if (streamEvent.TryPickContentBlockDelta(out BetaRawContentBlockDeltaEvent? delta))
        {
            foreach (ProviderEvent value in Delta(delta))
            {
                yield return value;
            }
        }
        else if (streamEvent.TryPickContentBlockStop(out BetaRawContentBlockStopEvent? ended))
        {
            foreach (ProviderEvent value in End(ended.Index))
            {
                yield return value;
            }
        }
        else if (streamEvent.TryPickDelta(out BetaRawMessageDeltaEvent? message))
        {
            // The wire strings, which an enum's own ToString() does not give.
            stop = message.Delta.StopReason?.Raw() ?? stop;
            category = message.Delta.StopDetails?.Category?.Raw() ?? category;
            Take(
                message.Usage.InputTokens,
                message.Usage.CacheCreationInputTokens,
                message.Usage.CacheReadInputTokens,
                message.Usage.OutputTokens);
        }
        else if (streamEvent.TryPickStop(out _))
        {
            Stopped = true;

            yield return new UsageReport(new Usage(fresh + created, output, cached));

            // A reply stopped by a limit is incomplete, and ending it `done` would let the operator take it for whole.
            // It fails the round as a stream cut short does, so the core keeps the text it had and reports the cause.
            if (stop is "max_tokens" or "model_context_window_exceeded")
            {
                throw new IOException($"The reply was cut off before it finished ({stop}).");
            }

            yield return stop == "refusal"
                ? new RoundEnd(ProviderStop.Refused, category)
                : new RoundEnd(ProviderStop.Done);
        }
    }

    // `input_tokens` leaves out what was read from or written to the cache, and the core counts both toward the
    // context, so a cache write is input and a cache read is reported on its own. The delta carries totals; a field it
    // leaves out keeps the start's figure, so each is kept apart and summed once, at the end.
    private void Take(long? inputTokens, long? written, long? read, long? outputTokens)
    {
        fresh = inputTokens ?? fresh;
        created = written ?? created;
        cached = read ?? cached;
        output = outputTokens ?? output;
    }

    private void Begin(BetaRawContentBlockStartEvent start)
    {
        ContentBlock block = start.ContentBlock;

        if (block.TryPickBetaToolUse(out BetaToolUseBlock? use))
        {
            blocks[start.Index] = new Pending(PendingKind.Tool) { Id = use.ID, Name = use.Name };
        }
        else if (block.TryPickBetaThinking(out BetaThinkingBlock? thinking))
        {
            blocks[start.Index] = new Pending(PendingKind.Thinking);
            blocks[start.Index].Text.Append(thinking.Thinking);
            blocks[start.Index].Signature = thinking.Signature;
        }
        else if (block.TryPickBetaRedactedThinking(out BetaRedactedThinkingBlock? redacted))
        {
            blocks[start.Index] = new Pending(PendingKind.Redacted) { Signature = redacted.Data };
        }
    }

    private IEnumerable<ProviderEvent> Delta(BetaRawContentBlockDeltaEvent delta)
    {
        if (delta.Delta.TryPickText(out BetaTextDelta? text))
        {
            yield return new TextDelta(text.Text);
        }
        else if (blocks.TryGetValue(delta.Index, out Pending? pending))
        {
            if (delta.Delta.TryPickInputJson(out BetaInputJsonDelta? json))
            {
                pending.Text.Append(json.PartialJson);
            }
            else if (delta.Delta.TryPickThinking(out BetaThinkingDelta? thinking))
            {
                pending.Text.Append(thinking.Thinking);
            }
            else if (delta.Delta.TryPickSignature(out BetaSignatureDelta? signature))
            {
                pending.Signature = signature.Signature;
            }
        }
    }

    private IEnumerable<ProviderEvent> End(long index)
    {
        if (!blocks.Remove(index, out Pending? pending))
        {
            yield break;
        }

        switch (pending.Kind)
        {
            case PendingKind.Tool:
                yield return new ToolCall(pending.Id!, pending.Name!, Input(pending.Text));

                break;
            case PendingKind.Thinking:
                yield return new Opaque(OpaqueBlocks.OfThinking(pending.Text.ToString(), pending.Signature ?? ""));

                break;
            case PendingKind.Redacted:
                yield return new Opaque(OpaqueBlocks.OfRedacted(pending.Signature ?? ""));

                break;
        }
    }

    // A tool with no arguments streams no JSON at all, and a model that streams an object streams it in pieces.
    private static JsonObject Input(StringBuilder json) =>
        json.Length == 0
            ? []
            : JsonNode.Parse(json.ToString()) as JsonObject
                ?? throw new JsonException("A tool call's input was not an object.");

    private enum PendingKind
    {
        Tool,
        Thinking,
        Redacted,
    }

    private sealed class Pending(PendingKind kind)
    {
        public PendingKind Kind { get; } = kind;

        public string? Id { get; init; }

        public string? Name { get; init; }

        public StringBuilder Text { get; } = new();

        // The signature of a thinking block, or the data of a redacted one.
        public string? Signature { get; set; }
    }
}

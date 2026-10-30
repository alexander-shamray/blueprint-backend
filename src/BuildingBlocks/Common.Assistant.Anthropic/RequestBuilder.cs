using System.Text.Json;
using System.Text.Json.Nodes;
using Common.Assistant.Providers;
using Common.Assistant.Tools;
using Anthropic.Models.Beta.Messages;

namespace Common.Assistant.Anthropic;

/// <summary>
/// One provider round as a Messages request (AI assistant design §8.2). The request is in cache order: tools, then the
/// system text and the catalogue, then the history, with a breakpoint at the end of the first three and one at the end
/// of the history, so the next request reads the conversation so far from the cache.
/// </summary>
internal static class RequestBuilder
{
    /// <summary>The beta that carries the server-side refusal fallback, which is on a beta endpoint.</summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    public static MessageCreateParams Build(TurnRequest request, AnthropicSettings settings)
    {
        // Nothing that varies comes before either breakpoint: the turn's context and the language rule are already
        // inside the latest user message.
        return new MessageCreateParams
        {
            Model = settings.Model,
            MaxTokens = request.MaxOutputTokens,
            Betas = [FallbackBeta],
            // The model's own server-defined fallback when a safety classifier declines; a refusal that survives it
            // arrives as the `refusal` stop reason, which the mapper reports as refused.
            Fallbacks = new BetaFallbacksParam(new Default()),
            // Adaptive thinking is the model's default, so the parameter is omitted. `auto` because the current models
            // refuse a forced tool choice.
            ToolChoice = new BetaToolChoiceAuto(),
            OutputConfig = new BetaOutputConfig { Effort = EffortOf(settings.Effort) },
            Tools = Tools(request.Tools),
            System = new List<BetaTextBlockParam>
            {
                new() { Text = request.System },
                new() { Text = Prompt.Catalogue(request.Catalogue), CacheControl = new BetaCacheControlEphemeral() },
            },
            Messages = Messages(request.History),
        };
    }

    // The API's complexity limits for strict schemas, per request, across all of them ("Schema complexity limits" in
    // the structured outputs documentation). A request over any of them is refused whole, so every turn would fail.
    internal const int MaxStrictTools = 20;

    internal const int MaxOptionalParameters = 24;

    internal const int MaxUnionParameters = 16;

    // Strict while the request stays inside the limits, in the order the core gave (sorted by name, so the same tools
    // are strict from one request to the next and the cached prefix does not move). A tool past the budget is sent
    // without `strict`: its host or client checks the input anyway, and the model still reads the closed schema.
    private static List<BetaToolUnion> Tools(IReadOnlyList<ToolDescriptor> descriptors)
    {
        List<BetaToolUnion> tools = [];
        int strictTools = 0;
        int optional = 0;
        int unions = 0;

        foreach (ToolDescriptor descriptor in descriptors)
        {
            // Closed objects are what a strict schema asks for, and cost nothing on a tool that is not.
            JsonObject closed = (JsonObject)descriptor.InputSchema.DeepClone();
            closed.TryAdd("type", "object");
            Close(closed);

            (int tool, int union) = (Optional(closed), Unions(closed));
            bool strict = strictTools < MaxStrictTools
                && optional + tool <= MaxOptionalParameters
                && unions + union <= MaxUnionParameters;

            if (strict)
            {
                strictTools++;
                optional += tool;
                unions += union;
            }

            tools.Add(Tool(descriptor, closed, strict));
        }

        return tools;
    }

    private static BetaToolUnion Tool(ToolDescriptor descriptor, JsonObject closed, bool strict)
    {
        Dictionary<string, JsonElement> schema =
            closed.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

        return new BetaTool
        {
            Name = descriptor.Name,
            Description = descriptor.Description,
            Strict = strict ? true : null,
            InputSchema = InputSchema.FromRawUnchecked(schema),
        };
    }

    // Each property of a schema object that its `required` leaves out, at any depth.
    private static int Optional(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject schema:
                int count = 0;

                // Any schema with a `properties` map, whatever its `type` (a type list, or an `anyOf` branch with
                // none). The map is told from a property that is itself called `properties` by holding only schemas.
                if (schema["properties"] is JsonObject properties && properties.All(p => p.Value is JsonObject))
                {
                    HashSet<string> required = schema["required"] is JsonArray list
                        ? [.. list.OfType<JsonValue>().Select(v => v.TryGetValue(out string? name) ? name : "")]
                        : [];
                    count += properties.Count(p => !required.Contains(p.Key));
                }

                return count + schema.Sum(e => Optional(e.Value));
            case JsonArray array:
                return array.Sum(Optional);
            default:
                return 0;
        }
    }

    // Each schema that is an `anyOf` or has a type list, at any depth.
    private static int Unions(JsonNode? node) => node switch
    {
        JsonObject schema => ((schema["anyOf"] is JsonArray || schema["type"] is JsonArray) ? 1 : 0)
            + schema.Sum(e => Unions(e.Value)),
        JsonArray array => array.Sum(Unions),
        _ => 0,
    };

    // The bounds a strict schema may not carry, which the SDK's own schema helper moves into the description
    // (SchemaPropertyAttribute in the pinned Anthropic package): numeric limits and string lengths, and minItems above
    // 1. The tool still checks its input, so the model reads the bound and the host enforces it.
    private static readonly string[] UnsupportedBounds =
        ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "minLength", "maxLength"];

    // Closes every object schema in the tree, the root and those under `properties`, `items`, `$defs` and the
    // combinators, without overruling one that already names `additionalProperties`, and moves each schema's
    // unsupported bounds into its description. Only a string `type` marks a schema, so a property called `type` in a
    // `properties` map, which holds a schema, is not mistaken for one, and nor is a property called `minimum`.
    private static void Close(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject schema:
                if (schema["type"] is JsonValue type && type.TryGetValue(out string? name))
                {
                    if (name == "object")
                    {
                        schema.TryAdd("additionalProperties", false);
                    }

                    MoveBoundsToDescription(schema);
                }

                foreach (KeyValuePair<string, JsonNode?> entry in schema.ToList())
                {
                    Close(entry.Value);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    Close(item);
                }

                break;
        }
    }

    private static void MoveBoundsToDescription(JsonObject schema)
    {
        JsonObject moved = [];

        foreach (string bound in UnsupportedBounds)
        {
            if (schema[bound] is JsonValue value)
            {
                schema.Remove(bound);
                moved[bound] = value;
            }
        }

        if (schema["minItems"] is JsonValue minItems && minItems.TryGetValue(out int least) && least > 1)
        {
            schema.Remove("minItems");
            moved["minItems"] = least;
        }

        if (moved.Count > 0)
        {
            string described =
                schema["description"] is JsonValue d && d.TryGetValue(out string? text) ? $"{text} " : "";
            schema["description"] = $"{described}{moved.ToJsonString()}";
        }
    }

    // A block is built with the cache mark or without it, because the SDK's blocks cannot be changed once made; the
    // breakpoint at the end of the history goes on the last block of the last message, whatever kind it is.
    private delegate BetaContentBlockParam Block(BetaCacheControlEphemeral? mark);

    private static List<BetaMessageParam> Messages(IReadOnlyList<HistoryMessage> history)
    {
        List<BetaMessageParam> messages = [];

        for (int i = 0; i < history.Count; i++)
        {
            (BetaMessageParamRole role, List<Block> blocks)? message = history[i] switch
            {
                UserMessage user => (BetaMessageParamRole.User, [mark => TextBlock(user.Content, mark)]),
                AssistantMessage assistant => (BetaMessageParamRole.Assistant, Blocks(assistant)),
                ToolResultsMessage results => (BetaMessageParamRole.User, Results(results)),
                _ => null,
            };

            if (message is not { } built || built.blocks.Count == 0)
            {
                continue;
            }

            bool last = i == history.Count - 1;
            List<BetaContentBlockParam> content = [];

            for (int b = 0; b < built.blocks.Count; b++)
            {
                content.Add(
                    built.blocks[b](last && b == built.blocks.Count - 1 ? new BetaCacheControlEphemeral() : null));
            }

            messages.Add(built.role == BetaMessageParamRole.User
                ? new BetaUserMessageParam { Content = content }
                : new BetaAssistantMessageParam { Content = content });
        }

        return messages;
    }

    // The reasoning first, as it was returned, then the text, then the tool calls.
    private static List<Block> Blocks(AssistantMessage message)
    {
        List<Block> blocks =
        [
            .. message.Opaque
                .Select(OpaqueBlocks.ToParam)
                .OfType<BetaContentBlockParam>()
                .Select<BetaContentBlockParam, Block>(b => _ => b)
        ];

        // The API refuses a text block of whitespace alone, and the history keeps a round's text as it came, a lone
        // newline before a tool call included; replaying that block would fail every later request of the conversation.
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            blocks.Add(mark => TextBlock(message.Text, mark));
        }

        foreach (ToolCall call in message.Calls)
        {
            blocks.Add(mark => ToolUse(call, mark));
        }

        return blocks;
    }

    private static List<Block> Results(ToolResultsMessage message) =>
        [.. message.Results.Select<ToolResult, Block>(r => mark => ToolResult(r, mark))];

    // The SDK writes a property it was given as null, and the cache mark is set on one block in a request, so a block
    // without it is built without the property.
    private static BetaTextBlockParam TextBlock(string text, BetaCacheControlEphemeral? mark) =>
        mark is null
            ? new BetaTextBlockParam { Text = text }
            : new BetaTextBlockParam { Text = text, CacheControl = mark };

    private static BetaToolUseBlockParam ToolUse(ToolCall call, BetaCacheControlEphemeral? mark)
    {
        Dictionary<string, JsonElement> input =
            call.Input.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

        return mark is null
            ? new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input }
            : new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input, CacheControl = mark };
    }

    private static BetaToolResultBlockParam ToolResult(ToolResult result, BetaCacheControlEphemeral? mark) =>
        mark is null
            ? new BetaToolResultBlockParam { ToolUseID = result.CallId, Content = result.Content, IsError = !result.Ok }
            : new BetaToolResultBlockParam
            {
                ToolUseID = result.CallId,
                Content = result.Content,
                IsError = !result.Ok,
                CacheControl = mark,
            };

    private static Effort EffortOf(string effort) => effort switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => throw new InvalidOperationException(
            $"The effort '{effort}' is not one of {string.Join(", ", AnthropicSettings.Efforts)}."),
    };
}

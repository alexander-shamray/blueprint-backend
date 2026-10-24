using System.Text.Json.Nodes;
using Common.Assistant.Providers;
using Common.Assistant.Tools;
using Shouldly;

namespace Common.Assistant.Anthropic.Tests;

/// <summary>
/// The request the adapter sends (AI assistant design §8.2), read off the wire: no network behind it.
/// </summary>
public sealed class RequestTests
{
    private static async Task<Sent> SentFor(TurnRequest request, AnthropicSettings? settings = null)
    {
        FakeHttp http = new FakeHttp().Stream("text-with-cache");
        AnthropicProvider provider = new(http.Client(), settings ?? AnthropicSettings.Default, TimeProvider.System);

        await provider.Played(request);

        return http.Requests.Single();
    }

    [Fact]
    public async Task It_asks_for_a_stream_from_the_configured_model_and_effort_with_the_fallback_on()
    {
        Sent sent = await SentFor(
            Given.Request(new UserMessage("hi")),
            new AnthropicSettings("claude-haiku-5-5", "high"));
        JsonObject body = sent.Body!.AsObject();

        sent.Method.ShouldBe(HttpMethod.Post);
        sent.Uri.AbsolutePath.ShouldBe("/v1/messages");
        sent.Headers["anthropic-beta"].ShouldContain("server-side-fallback-2026-07-01");
        sent.Headers["x-api-key"].ShouldBe("sk-ant-test");
        body["stream"]!.GetValue<bool>().ShouldBeTrue();
        body["model"]!.GetValue<string>().ShouldBe("claude-haiku-5-5");
        body["max_tokens"]!.GetValue<int>().ShouldBe(2048);
        body["output_config"]!["effort"]!.GetValue<string>().ShouldBe("high");
        body["fallbacks"]!.GetValue<string>().ShouldBe("default");
        body["tool_choice"]!["type"]!.GetValue<string>().ShouldBe("auto");
    }

    [Fact]
    public async Task Adaptive_thinking_is_the_default_so_the_parameter_is_left_out_and_nothing_sampled_is_sent()
    {
        JsonObject body = (await SentFor(Given.Request(new UserMessage("hi")))).Body!.AsObject();

        body.ContainsKey("thinking").ShouldBeFalse();
        body.ContainsKey("temperature").ShouldBeFalse();
        body.ContainsKey("top_p").ShouldBeFalse();
        body.ContainsKey("top_k").ShouldBeFalse();
    }

    [Fact]
    public async Task Every_tool_is_strict_with_a_closed_schema_and_in_the_order_the_core_gave()
    {
        JsonArray tools = (await SentFor(Given.Request(new UserMessage("hi")))).Body!["tools"]!.AsArray();

        tools.Select(t => t!["name"]!.GetValue<string>()).ShouldBe(["open_screen", "stack_status"]);
        tools.ShouldAllBe(t => t!["strict"]!.GetValue<bool>());
        tools.ShouldAllBe(t => t!["input_schema"]!["additionalProperties"]!.GetValue<bool>() == false);
        tools[0]!["input_schema"]!["required"]![0]!.GetValue<string>().ShouldBe("path");
        tools[0]!["description"]!.GetValue<string>().ShouldBe("The open_screen tool.");
    }

    private static ToolDescriptor Optionals(string name, int optional)
    {
        JsonObject properties = [];

        for (int i = 0; i < optional; i++)
        {
            properties[$"p{i}"] = new JsonObject { ["type"] = "string" };
        }

        return Given.Tool(name) with
        {
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = properties },
        };
    }

    private static bool[] StrictOf(JsonArray tools) =>
        [.. tools.Select(t => t!.AsObject().ContainsKey("strict") && t["strict"]!.GetValue<bool>())];

    [Fact]
    public async Task Past_twenty_strict_tools_the_rest_are_sent_without_strict_so_the_request_is_not_refused()
    {
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools = [.. Enumerable.Range(0, 25).Select(i => Given.Tool($"tool_{i:00}"))],
        };

        JsonArray tools = (await SentFor(request)).Body!["tools"]!.AsArray();

        StrictOf(tools).Count(s => s).ShouldBe(RequestBuilder.MaxStrictTools);
        StrictOf(tools).Take(RequestBuilder.MaxStrictTools).ShouldAllBe(s => s);
        StrictOf(tools).Skip(RequestBuilder.MaxStrictTools).ShouldAllBe(s => !s);
        // Absent on the wire, not `"strict":null`, which the API might refuse whole.
        tools.Skip(RequestBuilder.MaxStrictTools).ShouldAllBe(t => !t!.AsObject().ContainsKey("strict"));
        // Still closed, so the model reads the same schema either way.
        tools.ShouldAllBe(t => t!["input_schema"]!["additionalProperties"]!.GetValue<bool>() == false);
    }

    [Fact]
    public async Task
    Past_twenty_four_optional_parameters_a_tool_that_would_pass_the_limit_is_not_strict_and_a_later_one_that_fits_is()
    {
        // Five optional parameters each: four tools reach 20, a fifth would make 25. A tool with none still fits.
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                Optionals("a", 5),
                Optionals("b", 5),
                Optionals("c", 5),
                Optionals("d", 5),
                Optionals("e", 5),
                Given.Tool("f"),
            ],
        };

        JsonArray tools = (await SentFor(request)).Body!["tools"]!.AsArray();

        StrictOf(tools).ShouldBe([true, true, true, true, false, true]);
    }

    [Fact]
    public async Task A_required_parameter_costs_nothing_against_the_optional_limit_and_a_nested_optional_one_does()
    {
        JsonObject nested = JsonNode.Parse(
            """{"type":"object","properties":{"outer":{"type":"object","properties":""" +
            """{"a":{"type":"string"},"b":{"type":"string"}}}},"required":["outer"]}""")!.AsObject();
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                .. Enumerable.Range(0, 12)
                    .Select(i => Given.Tool($"n{i:00}") with { InputSchema = (JsonObject)nested.DeepClone() }),
                Given.Tool("z"),
            ],
        };

        JsonArray tools = (await SentFor(request)).Body!["tools"]!.AsArray();

        // Two nested optional parameters per tool: twelve tools reach 24, and `z`, with none, is the thirteenth strict
        // one.
        StrictOf(tools).ShouldAllBe(s => s);
    }

    [Fact]
    public async Task Optional_parameters_are_counted_under_a_type_list_and_an_anyOf_branch_too()
    {
        // Two optional parameters hide behind `["object","null"]` and three behind an anyOf branch with no `type`.
        JsonObject hidden = JsonNode.Parse("""
    {"type":"object","properties":{
      "a":{"type":["object","null"],"properties":{"x":{"type":"string"},"y":{"type":"string"}}},
      "b":{"anyOf":[{"properties":{"p":{"type":"string"},"q":{"type":"string"},"r":{"type":"string"}}},{"type":"null"}]}
    },"required":["a","b"]}
    """)!.AsObject();
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                .. Enumerable.Range(0, 6)
                    .Select(i => Given.Tool($"h{i:00}") with { InputSchema = (JsonObject)hidden.DeepClone() }),
            ],
        };

        JsonArray tools = (await SentFor(request)).Body!["tools"]!.AsArray();

        // Five optional parameters each: four tools reach 20, the fifth would make 25.
        StrictOf(tools).ShouldBe([true, true, true, true, false, false]);
    }

    [Fact]
    public async Task Past_sixteen_union_typed_parameters_a_tool_that_adds_one_more_is_not_strict()
    {
        JsonObject union = JsonNode.Parse(
            """{"type":"object","properties":{"v":{"anyOf":[{"type":"string"},{"type":"null"}]}},"required":""" +
            """["v"]}""")!.AsObject();
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                .. Enumerable.Range(0, 17)
                    .Select(i => Given.Tool($"u{i:00}") with { InputSchema = (JsonObject)union.DeepClone() }),
            ],
        };

        JsonArray tools = (await SentFor(request)).Body!["tools"]!.AsArray();

        StrictOf(tools).Count(s => s).ShouldBe(RequestBuilder.MaxUnionParameters);
        StrictOf(tools)[^1].ShouldBeFalse();
    }

    [Fact]
    public async Task A_schema_that_already_names_additionalProperties_is_not_overruled()
    {
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                Given.Tool("open") with
                {
                    InputSchema = JsonNode.Parse(
                        """{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject(),
                },
            ],
        };

        JsonArray tools = (await SentFor(request)).Body!["tools"]!.AsArray();

        tools[0]!["input_schema"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Objects_nested_in_a_schema_are_closed_too_and_a_root_without_a_type_is_given_one()
    {
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                Given.Tool("nested") with
                {
                    InputSchema = JsonNode.Parse("""
                {"properties":{"filter":{"type":"object","properties":{"type":{"type":"object","properties":{}}}},
                "rows":{"type":"array","items":{"type":"object","properties":{}}}}}
                """)!.AsObject(),
                },
            ],
        };

        JsonObject schema = (await SentFor(request)).Body!["tools"]![0]!["input_schema"]!.AsObject();

        schema["type"]!.GetValue<string>().ShouldBe("object");
        schema["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!["filter"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        schema["properties"]!["filter"]!["properties"]!["type"]!["additionalProperties"]!.GetValue<bool>()
            .ShouldBeFalse();
        schema["properties"]!["rows"]!["items"]!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task
        Bounds_a_strict_schema_may_not_carry_move_into_the_description_and_a_property_named_like_one_stays()
    {
        // recent_logs' schema as the host's AdminReadTools declares it, plus a string length, an array floor, and a
        // property called `minimum` that is a name, not a bound.
        TurnRequest request = Given.Request(new UserMessage("hi")) with
        {
            Tools =
            [
                Given.Tool("recent_logs") with
                {
                    InputSchema = JsonNode.Parse("""
                        {"type":"object","properties":{
                          "lines":{"type":"integer","minimum":1,"maximum":200,"description":"How many lines."},
                          "filter":{"type":"string","maxLength":80},
                          "ids":{"type":"array","minItems":2,"items":{"type":"string"}},
                          "minimum":{"type":"integer","minItems":1}}}
                        """)!.AsObject(),
                },
            ],
        };

        JsonObject properties = (await SentFor(request)).Body!["tools"]![0]!["input_schema"]!["properties"]!.AsObject();

        properties["lines"]!.AsObject().ContainsKey("minimum").ShouldBeFalse();
        properties["lines"]!.AsObject().ContainsKey("maximum").ShouldBeFalse();
        properties["lines"]!["description"]!.GetValue<string>()
            .ShouldBe("""How many lines. {"minimum":1,"maximum":200}""");
        properties["filter"]!["description"]!.GetValue<string>().ShouldBe("""{"maxLength":80}""");
        properties["ids"]!.AsObject().ContainsKey("minItems").ShouldBeFalse();
        properties.ContainsKey("minimum").ShouldBeTrue();
        properties["minimum"]!["minItems"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task The_system_text_and_catalogue_are_cached_and_nothing_that_varies_comes_before_the_breakpoint()
    {
        JsonObject body = (await SentFor(Given.Request(new UserMessage("hi")))).Body!.AsObject();
        JsonArray system = body["system"]!.AsArray();

        system.Count.ShouldBe(2);
        system[0]!["text"]!.GetValue<string>().ShouldBe("You are an assistant.");
        system[0]!.AsObject().ContainsKey("cache_control").ShouldBeFalse();
        system[1]!["text"]!.GetValue<string>().ShouldContain("- logs | Logs | /logs | Follow the platform's logs.");
        system[1]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public async Task The_second_breakpoint_is_the_last_block_of_the_history_whatever_kind_it_is()
    {
        TurnRequest request = Given.Request(
            new UserMessage("where are the logs?"),
            new AssistantMessage(
                "Opening.",
                [new ToolCall("toolu_1", "open_screen", new JsonObject { ["path"] = "logs" })],
                []),
            new ToolResultsMessage([new ToolResult("toolu_1", Ok: true, "Opened Logs.")]));

        JsonArray messages = (await SentFor(request)).Body!["messages"]!.AsArray();
        IEnumerable<JsonNode?> blocks = messages.SelectMany(m => m!["content"]!.AsArray());

        // Exactly one history breakpoint, and it is the tool result at the end.
        blocks.Count(b => b!.AsObject().ContainsKey("cache_control")).ShouldBe(1);
        messages[^1]!["content"]![0]!["type"]!.GetValue<string>().ShouldBe("tool_result");
        messages[^1]!["content"]![0]!["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
    }

    [Fact]
    public async Task The_history_is_replayed_with_the_reasoning_first_and_every_result_in_one_user_message()
    {
        JsonObject thinking = JsonNode.Parse("""{"type":"thinking","thinking":"hm","signature":"sig=="}""")!.AsObject();
        JsonObject redacted = JsonNode.Parse("""{"type":"redacted_thinking","data":"opaque=="}""")!.AsObject();
        TurnRequest request = Given.Request(
            new UserMessage("two things"),
            new AssistantMessage(
                "On it.",
                [
                    new ToolCall("toolu_1", "open_screen", new JsonObject { ["path"] = "logs" }),
                    new ToolCall("toolu_2", "stack_status", [])
                ],
                [thinking, redacted]),
            new ToolResultsMessage([
                new ToolResult("toolu_1", true, "ok"),
                new ToolResult("toolu_2", false, "the host did not answer")
            ]),
            new UserMessage("and now?"));

        JsonArray messages = (await SentFor(request)).Body!["messages"]!.AsArray();

        messages.Select(m => m!["role"]!.GetValue<string>()).ShouldBe(["user", "assistant", "user", "user"]);
        JsonArray assistant = messages[1]!["content"]!.AsArray();
        assistant.Select(b => b!["type"]!.GetValue<string>())
            .ShouldBe(["thinking", "redacted_thinking", "text", "tool_use", "tool_use"]);
        assistant[0]!["thinking"]!.GetValue<string>().ShouldBe("hm");
        assistant[0]!["signature"]!.GetValue<string>().ShouldBe("sig==");
        assistant[1]!["data"]!.GetValue<string>().ShouldBe("opaque==");
        assistant[3]!["input"]!["path"]!.GetValue<string>().ShouldBe("logs");

        JsonArray results = messages[2]!["content"]!.AsArray();
        results.Select(b => b!["tool_use_id"]!.GetValue<string>()).ShouldBe(["toolu_1", "toolu_2"]);
        results[0]!["is_error"]!.GetValue<bool>().ShouldBeFalse();
        results[1]!["is_error"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task A_rounds_whitespace_only_text_is_left_out_of_the_replay_and_its_tool_call_kept()
    {
        TurnRequest request = Given.Request(
            new UserMessage("status?"),
            new AssistantMessage("\n\n", [new ToolCall("toolu_1", "stack_status", [])], []),
            new ToolResultsMessage([new ToolResult("toolu_1", true, "up")]));

        JsonArray assistant = (await SentFor(request)).Body!["messages"]![1]!["content"]!.AsArray();

        assistant.Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["tool_use"]);
    }

    [Fact]
    public async Task A_payload_this_adapter_did_not_write_is_left_out_of_the_replay()
    {
        TurnRequest request = Given.Request(
            new UserMessage("hi"),
            new AssistantMessage(
                "Hello.",
                [],
                [JsonNode.Parse("""{"type":"something_else"}""")!, JsonValue.Create("not an object")!]));

        JsonArray assistant = (await SentFor(request)).Body!["messages"]![1]!["content"]!.AsArray();

        assistant.Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["text"]);
    }

    [Fact]
    public async Task An_effort_the_sdk_does_not_know_is_refused_before_a_request_is_sent()
    {
        FakeHttp http = new();
        AnthropicProvider provider =
            new(http.Client(), new AnthropicSettings("claude-opus-5-5", "extreme"), TimeProvider.System);

        await Should.ThrowAsync<InvalidOperationException>(() => provider.Played(Given.Request(new UserMessage("hi"))));

        http.Requests.ShouldBeEmpty();
    }
}

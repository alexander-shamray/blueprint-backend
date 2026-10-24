using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Shouldly;

namespace Common.Assistant.Anthropic.Tests;

/// <summary>
/// The provider's events from recorded stream bodies (AI assistant design §14), through the SDK's own parser and a
/// stand-in for the network. The bodies follow the Messages API's documented stream, written by hand: there is no
/// recording from a live credential here.
/// </summary>
public sealed class StreamTests
{
    private static AnthropicProvider Provider(FakeHttp http) =>
        new(http.Client(), AnthropicSettings.Default, TimeProvider.System);

    [Fact]
    public async Task Text_is_relayed_as_it_arrives_and_the_usage_counts_cache_writes_as_input_and_reads_as_cached()
    {
        FakeHttp http = new FakeHttp().Stream("text-with-cache");

        List<ProviderEvent> events = await Provider(http).Played(Given.Request(new UserMessage("where are the logs?")));

        events.OfType<TextDelta>().Select(t => t.Text).ShouldBe(["The logs are on ", "the Logs screen."]);
        // 12 fresh input tokens and 100 written to the cache are the request's own; the 300 read from it are reported
        // apart.
        events.OfType<UsageReport>().Single().Usage.ShouldBe(new Usage(Input: 112, Output: 25, Cached: 300));
        events.Last().ShouldBe(new RoundEnd(ProviderStop.Done));
    }

    [Fact]
    public async Task A_tool_call_is_emitted_once_its_input_is_whole_and_the_reasoning_is_kept_as_an_opaque_payload()
    {
        FakeHttp http = new FakeHttp().Stream("thinking-and-tool");

        List<ProviderEvent> events = await Provider(http).Played(Given.Request(new UserMessage("open the logs")));

        ToolCall call = events.OfType<ToolCall>().Single();
        call.Id.ShouldBe("toolu_01Open");
        call.Name.ShouldBe("open_screen");
        call.Input.ToJsonString().ShouldBe("""{"path":"logs"}""");

        JsonObject thinking = events.OfType<Opaque>().Single().Payload.AsObject();
        thinking["type"]!.GetValue<string>().ShouldBe("thinking");
        thinking["thinking"]!.GetValue<string>().ShouldBe("The user wants the logs; open the Logs screen.");
        thinking["signature"]!.GetValue<string>().ShouldBe("EqQBCkYIARgCKkD-signature-bytes==");

        // The reasoning is complete before the call it led to, and the usage and the end close the round.
        events.IndexOf(events.OfType<Opaque>().Single()).ShouldBeLessThan(events.IndexOf(call));
        events.OfType<UsageReport>().Single().Usage.ShouldBe(new Usage(Input: 40, Output: 67, Cached: 512));
        events.Last().ShouldBe(new RoundEnd(ProviderStop.Done));
    }

    [Fact]
    public async Task A_tool_that_streams_no_arguments_is_called_with_an_empty_object()
    {
        FakeHttp http = new FakeHttp().Stream("tool-without-arguments");

        List<ProviderEvent> events = await Provider(http).Played(Given.Request(new UserMessage("how is the stack?")));

        ToolCall call = events.OfType<ToolCall>().Single();
        call.Name.ShouldBe("stack_status");
        call.Input.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_refusal_ends_the_round_refused_with_the_providers_category()
    {
        FakeHttp http = new FakeHttp().Stream("refusal");

        List<ProviderEvent> events = await Provider(http).Played(Given.Request(new UserMessage("something declined")));

        events.OfType<TextDelta>().ShouldBeEmpty();
        events.Last().ShouldBe(new RoundEnd(ProviderStop.Refused, "cyber"));
    }

    [Fact]
    public async Task Redacted_reasoning_is_kept_whole_for_the_next_request()
    {
        FakeHttp http = new FakeHttp().Stream("redacted-thinking");

        List<ProviderEvent> events = await Provider(http).Played(Given.Request(new UserMessage("hello")));

        JsonObject redacted = events.OfType<Opaque>().Single().Payload.AsObject();
        redacted["type"]!.GetValue<string>().ShouldBe("redacted_thinking");
        redacted["data"]!.GetValue<string>().ShouldStartWith("EmwKAhgBEgy3va3pzix");
        events.OfType<TextDelta>().Single().Text.ShouldBe("Done.");
    }

    [Fact]
    public async Task A_delta_that_repeats_the_cache_figures_does_not_count_a_cache_write_twice()
    {
        FakeHttp http = new FakeHttp().Stream("cumulative-usage");

        List<ProviderEvent> events = await Provider(http).Played(Given.Request(new UserMessage("hello")));

        // The delta repeats the write and the read but leaves input_tokens out: the start's 12 and the 100 written,
        // once.
        events.OfType<UsageReport>().Single().Usage.ShouldBe(new Usage(Input: 112, Output: 25, Cached: 300));
    }

    [Fact]
    public async Task A_stream_that_ends_before_message_stop_is_a_failed_round_not_a_finished_one()
    {
        FakeHttp http = new FakeHttp().Stream("truncated");

        await Should.ThrowAsync<IOException>(() => Provider(http).Played(Given.Request(new UserMessage("hello"))));
    }

    [Fact]
    public async Task A_reply_stopped_at_the_output_limit_fails_the_round_after_its_text_rather_than_ending_done()
    {
        FakeHttp http = new FakeHttp().Stream("max-tokens");
        List<ProviderEvent> events = [];

        IOException cut = await Should.ThrowAsync<IOException>(async () =>
        {
            await foreach (ProviderEvent value in Provider(http).TurnAsync(
                Given.Request(new UserMessage("how is the stack?")),
                TestContext.Current.CancellationToken))
            {
                events.Add(value);
            }
        });

        cut.Message.ShouldContain("max_tokens");
        events.OfType<TextDelta>().ShouldNotBeEmpty();
        events.OfType<RoundEnd>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_rejected_request_surfaces_as_the_exception_the_session_turns_into_an_error_event()
    {
        FakeHttp http = new FakeHttp().Status(
            System.Net.HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");

        await Should.ThrowAsync<Exception>(() => Provider(http).Played(Given.Request(new UserMessage("hello"))));
    }
}

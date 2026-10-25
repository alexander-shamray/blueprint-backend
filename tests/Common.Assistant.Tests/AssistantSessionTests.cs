using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Common.Assistant.Tools;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Common.Assistant.Tests;

/// <summary>The loop of AI assistant design §7 against a scripted provider (design §14, "Core").</summary>
public sealed class AssistantSessionTests
{
    private static readonly Caller Operator = new("operator", "Operator");

    private static AssistantCore Core(
        ScriptedProvider provider,
        AssistantLimits? limits = null,
        TimeProvider? time = null,
        IRedactor? redactor = null,
        params IToolSource[] sources) =>
        new(
            provider,
            sources,
            ToolPolicy.ReadOnly,
            redactor ?? new PlantedRedactor(),
            "Host rules.",
            limits ?? new AssistantLimits(),
            time ?? TimeProvider.System);

    private static AssistantSession Open(
        AssistantCore core,
        string locale = "en-US",
        params ToolDescriptor[] clientTools) =>
        core.Open(Operator, Given.Open(locale, clientTools)).ShouldNotBeNull();

    [Fact]
    public async Task Text_is_relayed_as_it_arrives_and_the_turn_ends_done()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new TextDelta("Logs are "),
                new TextDelta("on Logs."),
                new UsageReport(new Usage(100, 7, 40)),
                new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider));

        session.TryStart(Given.Turn("where are the logs?")).ShouldBeTrue();
        List<AssistantEvent> events = await Given.UntilEnded(session);

        events.OfType<TextEvent>().Select(e => e.Delta).ShouldBe(["Logs are ", "on Logs."]);
        TurnEndedEvent ended = events.OfType<TurnEndedEvent>().Single();
        ended.Reason.ShouldBe(TurnEndReason.Done);
        ended.Usage.ShouldBe(new Usage(100, 7, 40));
        ended.Total.ShouldBe(new Usage(100, 7, 40));

        ConversationView view = session.View();
        view.Running.ShouldBeFalse();
        view.Items.ShouldBe([
            new OperatorItem("where are the logs?"),
            new ReplyItem("Logs are on Logs."),
            new EndItem(TurnEndReason.Done, null)
        ]);
        view.LastEventId.ShouldBe(3);
    }

    [Fact]
    public async Task A_server_round_runs_the_tool_with_the_callers_context_and_hands_the_result_back()
    {
        ScriptedTools tools = new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, "all up"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "stack_status", new JsonObject { ["service"] = "ordering" }),
                new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("All up."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider, sources: tools));

        session.TryStart(Given.Turn("is the stack up?"));
        List<AssistantEvent> events = await Given.UntilEnded(session);

        events.OfType<ToolStartedEvent>().Single().ShouldSatisfyAllConditions(
            e => e.Name.ShouldBe("stack_status"),
            e => e.Side.ShouldBe(ToolSide.Server),
            e => e.Args["service"]!.GetValue<string>().ShouldBe("ordering"));
        events.OfType<ToolFinishedEvent>().Single().Ok.ShouldBeTrue();
        tools.Calls.Single().Context.ShouldSatisfyAllConditions(
            c => c.Caller.ShouldBe(Operator),
            c => c.ConversationId.ShouldBe(session.Id),
            c => c.HostOptions["identity"]!.GetValue<string>().ShouldBe("demo"));

        provider.Requests[1].History[^1].ShouldBeOfType<ToolResultsMessage>()
            .Results.ShouldBe([new ToolResult("c1", true, "all up")]);
        session.View().Items.OfType<ToolItem>().Single().Ok.ShouldBe(true);
    }

    [Fact]
    public async Task A_client_round_emits_the_call_and_waits_for_the_clients_result()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "open_screen", new JsonObject { ["path"] = "logs" }),
                new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Opened Logs."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider), clientTools: Given.ReadOnly("open_screen", ToolSide.Client));

        session.TryStart(Given.Turn("open the logs"));
        Task<List<AssistantEvent>> ended = Given.UntilEnded(session);
        ClientToolCallEvent call = await NextOf<ClientToolCallEvent>(session);

        call.Name.ShouldBe("open_screen");
        call.Input["path"]!.GetValue<string>().ShouldBe("logs");
        session.TryAnswer(new ClientToolResult(call.CallId, true, "opened /logs")).ShouldBeTrue();
        session.TryAnswer(new ClientToolResult(call.CallId, true, "again")).ShouldBeFalse();

        (await ended).OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Done);
        provider.Requests[1].History[^1].ShouldBeOfType<ToolResultsMessage>()
            .Results.ShouldBe([new ToolResult("c1", true, "opened /logs")]);
    }

    [Fact]
    public async Task Every_result_of_a_round_server_and_client_goes_back_in_one_message_in_call_order()
    {
        ScriptedTools tools = new(("stack_status", async (_, _, ct) =>
        {
            await Task.Delay(50, ct);

            return new ToolOutcome(true, "up");
        }
        ));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "stack_status", []),
                new ToolCall("c2", "open_screen", new JsonObject { ["path"] = "stack" }),
                new RoundEnd(ProviderStop.Done))
            .Round(new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(
            Core(provider, sources: tools),
            clientTools: Given.ReadOnly("open_screen", ToolSide.Client));

        session.TryStart(Given.Turn("show me the stack"));
        Task<List<AssistantEvent>> ended = Given.UntilEnded(session);
        ClientToolCallEvent call = await NextOf<ClientToolCallEvent>(session);
        session.TryAnswer(new ClientToolResult(call.CallId, true, "opened")).ShouldBeTrue();
        await ended;

        provider.Requests[1].History.OfType<ToolResultsMessage>().Single().Results.ShouldBe(
            [new ToolResult("c1", true, "up"), new ToolResult("c2", true, "opened")]);
    }

    [Fact]
    public async Task A_turn_that_asks_for_more_than_MaxSteps_rounds_ends_with_max_steps_and_every_call_has_a_result()
    {
        ScriptedTools tools = new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, "up"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "stack_status", []), new RoundEnd(ProviderStop.Done))
            .Round(new ToolCall("c2", "stack_status", []), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider, new AssistantLimits { MaxSteps = 1 }, sources: tools));

        session.TryStart(Given.Turn("loop"));
        List<AssistantEvent> events = await Given.UntilEnded(session);

        events.OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.MaxSteps);
        tools.Calls.Count.ShouldBe(1);
        session.History[^1].ShouldBeOfType<ToolResultsMessage>().Results.Single().ShouldSatisfyAllConditions(
            r => r.CallId.ShouldBe("c2"),
            r => r.Ok.ShouldBeFalse());
    }

    [Fact]
    public async Task A_conversation_at_its_context_budget_ends_the_next_round_with_budget()
    {
        ScriptedTools tools = new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, "up"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "stack_status", []),
                new UsageReport(new Usage(600, 10, 500)),
                new RoundEnd(ProviderStop.Done));
        AssistantSession session =
            Open(Core(provider, new AssistantLimits { ConversationTokenBudget = 1000 }, sources: tools));

        session.TryStart(Given.Turn("status"));
        List<AssistantEvent> events = await Given.UntilEnded(session);

        events.OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Budget);
        provider.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_ends_a_turn_mid_stream_with_cancelled_and_keeps_the_text_it_had()
    {
        TaskCompletionSource started = new();
        ScriptedProvider provider = new ScriptedProvider().Round((_, ct) => ScriptedProvider.Hang("Look", started, ct));
        AssistantSession session = Open(Core(provider));

        session.TryStart(Given.Turn("where?"));
        Task<List<AssistantEvent>> ended = Given.UntilEnded(session);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        session.Cancel();

        (await ended).OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Cancelled);
        AssistantMessage kept = session.History[^1].ShouldBeOfType<AssistantMessage>();
        kept.Text.ShouldBe("Look");
        kept.Calls.ShouldBeEmpty();
        kept.Opaque.ShouldBeEmpty();
        session.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_call_waiting_when_the_turn_is_cancelled_ends_failed_and_the_read_holds_it_until_then()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "open_screen", new JsonObject { ["path"] = "logs" }),
                new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider), clientTools: Given.ReadOnly("open_screen", ToolSide.Client));

        session.TryStart(Given.Turn("open logs"));
        Task<List<AssistantEvent>> ended = Given.UntilEnded(session);
        _ = await NextOf<ClientToolCallEvent>(session);

        // A client reloaded now resumes past the call, so the read is how it learns the call still waits.
        ClientToolCallEvent pending = session.View().PendingCalls.ShouldHaveSingleItem();
        pending.CallId.ShouldBe("c1");
        pending.Input["path"]!.GetValue<string>().ShouldBe("logs");

        session.Cancel();
        List<AssistantEvent> events = await ended;

        events.OfType<ToolFinishedEvent>().Single().ShouldBe(new ToolFinishedEvent("c1", false));
        session.View().Items.OfType<ToolItem>().Single().Ok.ShouldBe(false);
        session.View().PendingCalls.ShouldBeEmpty();
    }

    [Fact]
    public void A_redactor_that_throws_leaves_no_turn_held()
    {
        AssistantSession session = Open(Core(new ScriptedProvider(), redactor: new ThrowingRedactor()));

        Should.Throw<InvalidOperationException>(() => session.TryStart(Given.Turn("hello")));

        session.Running.ShouldBeFalse();
        session.View().Items.ShouldBeEmpty();
        session.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refusal_ends_with_refused_and_the_providers_category()
    {
        ScriptedProvider provider = new ScriptedProvider().Round(new RoundEnd(ProviderStop.Refused, "cyber"));
        AssistantSession session = Open(Core(provider));

        session.TryStart(Given.Turn("help me"));
        TurnEndedEvent ended = (await Given.UntilEnded(session)).OfType<TurnEndedEvent>().Single();

        ended.Reason.ShouldBe(TurnEndReason.Refused);
        ended.Category.ShouldBe("cyber");
        session.View().Items[^1].ShouldBe(new EndItem(TurnEndReason.Refused, "cyber"));
    }

    [Fact]
    public async Task A_client_tool_the_client_never_answers_is_an_error_result_after_the_timeout_not_a_hung_turn()
    {
        FakeTimeProvider time = new();
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "open_screen", new JsonObject { ["path"] = "logs" }),
                new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("It did not open."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(
            Core(provider, new AssistantLimits { ClientToolTimeout = TimeSpan.FromSeconds(30) }, time),
            clientTools: Given.ReadOnly("open_screen", ToolSide.Client));

        session.TryStart(Given.Turn("open logs"));
        Task<List<AssistantEvent>> ended = Given.UntilEnded(session);
        _ = await NextOf<ClientToolCallEvent>(session);

        // The call is emitted before its wait starts its timer, so the clock moves until the wait has seen it.
        for (int i = 0; i < 100 && !session.View().Items.OfType<ToolItem>().Any(t => t.Ok is not null); i++)
        {
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        (await ended).OfType<ToolFinishedEvent>().Single().Ok.ShouldBeFalse();
        provider.Requests[1].History[^1].ShouldBeOfType<ToolResultsMessage>().Results.Single().Ok.ShouldBeFalse();
    }

    [Fact]
    public async Task A_server_tool_that_throws_is_an_error_result_the_model_reads_and_the_turn_goes_on()
    {
        ScriptedTools tools =
            new(("stack_status", (_, _, _) => throw new InvalidOperationException("docker is not running")));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "stack_status", []), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Docker is down."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider, sources: tools));

        session.TryStart(Given.Turn("status"));
        (await Given.UntilEnded(session)).OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Done);

        ToolResult result = provider.Requests[1].History[^1].ShouldBeOfType<ToolResultsMessage>().Results.Single();
        result.Ok.ShouldBeFalse();
        result.Content.ShouldContain("docker is not running");
    }

    [Fact]
    public async Task A_second_turn_is_refused_while_one_runs()
    {
        TaskCompletionSource started = new();
        ScriptedProvider provider = new ScriptedProvider().Round((_, ct) => ScriptedProvider.Hang("…", started, ct));
        AssistantSession session = Open(Core(provider));

        session.TryStart(Given.Turn("one")).ShouldBeTrue();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        session.TryStart(Given.Turn("two")).ShouldBeFalse();
        session.View().Running.ShouldBeTrue();
        session.Cancel();
        await Given.UntilEnded(session);
    }

    [Fact]
    public async Task The_history_is_append_only_and_opaque_blocks_go_back_unchanged()
    {
        JsonObject thinking = new() { ["type"] = "thinking", ["signature"] = "sig-1" };
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new Opaque(thinking), new TextDelta("First."), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Second."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider));

        session.TryStart(Given.Turn("one"));
        await Given.UntilEnded(session);
        // Read before the turn starts: a scripted round is one text delta, so the turn can end before the read.
        long after = session.View().LastEventId;
        session.TryStart(Given.Turn("two"));
        await Given.UntilEnded(session, after);

        IReadOnlyList<HistoryMessage> first = provider.Requests[0].History;
        IReadOnlyList<HistoryMessage> second = provider.Requests[1].History;
        second.Take(first.Count).ShouldBe(first);
        AssistantMessage reply = second[1].ShouldBeOfType<AssistantMessage>();
        reply.Opaque.Single().ShouldBeSameAs(thinking);
        reply.Opaque.Single().ToJsonString().ShouldBe("""{"type":"thinking","signature":"sig-1"}""");
    }

    [Fact]
    public async Task
        The_language_rule_and_context_ride_in_the_latest_message_so_a_locale_change_leaves_the_prefix_identical()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta("Logs."), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Логи."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider), locale: "en-US");

        session.TryStart(Given.Turn("where are the logs?"));
        await Given.UntilEnded(session);
        // Read before the turn starts, as above.
        long after = session.View().LastEventId;
        session.TryStart(Given.Turn("ORD-1", locale: "ru-RU"));
        await Given.UntilEnded(session, after);

        TurnRequest first = provider.Requests[0];
        TurnRequest second = provider.Requests[1];
        second.Tools.ShouldBe(first.Tools);
        second.System.ShouldBe(first.System);
        second.Catalogue.ShouldBe(first.Catalogue);
        second.History[0].ShouldBe(first.History[0]);

        string latest = second.History[^1].ShouldBeOfType<UserMessage>().Content;
        Prompt.CallerText(latest).ShouldBe("ORD-1");
        latest.ShouldContain("reply in ru-RU");
        latest.ShouldContain("""{"page":"stack"}""");
        first.System.ShouldNotContain("ru-RU");
        first.System.ShouldEndWith("Host rules.");
    }

    [Fact]
    public async Task The_callers_text_and_every_tool_result_are_redacted_before_the_provider_sees_them()
    {
        ScriptedTools tools =
            new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, "password is hunter22"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "stack_status", []), new RoundEnd(ProviderStop.Done))
            .Round(new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider, redactor: new PlantedRedactor("hunter22"), sources: tools));

        session.TryStart(Given.Turn("my password is hunter22"));
        await Given.UntilEnded(session);

        string sent = string.Join('\n', provider.Requests[1].History.Select(m => m switch
        {
            UserMessage user => user.Content,
            ToolResultsMessage results => string.Join('\n', results.Results.Select(r => r.Content)),
            _ => "",
        }));
        sent.ShouldNotContain("hunter22");
        sent.ShouldContain("<redacted>");
    }

    [Fact]
    public async Task A_tool_result_is_cut_to_the_size_bound()
    {
        ScriptedTools tools =
            new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, new string('x', 500)))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "stack_status", []), new RoundEnd(ProviderStop.Done))
            .Round(new RoundEnd(ProviderStop.Done));
        AssistantSession session =
            Open(Core(provider, new AssistantLimits { MaxToolResultChars = 100 }, sources: tools));

        session.TryStart(Given.Turn("status"));
        await Given.UntilEnded(session);

        string content = provider.Requests[1].History[^1].ShouldBeOfType<ToolResultsMessage>().Results.Single().Content;
        content.ShouldStartWith(new string('x', 100));
        content.ShouldEndWith("[cut to 100 characters]");
    }

    [Fact]
    public async Task The_conversation_read_holds_every_message_after_more_events_than_the_ring_holds()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round([
                .. Enumerable.Range(0, 20).Select(i => (ProviderEvent)new TextDelta($"{i} ")),
                new RoundEnd(ProviderStop.Done)
            ]);
        AssistantSession session = Open(Core(provider, new AssistantLimits { EventRing = 4 }));

        session.TryStart(Given.Turn("count"));
        await Given.UntilEnded(session);

        ConversationView view = session.View();
        view.LastEventId.ShouldBe(21);
        view.Items[1].ShouldBe(new ReplyItem(string.Concat(Enumerable.Range(0, 20).Select(i => $"{i} "))));

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(2));
        List<long> resumed = [];

        await foreach ((long id, AssistantEvent _) in session.Events(0, stop.Token))
        {
            resumed.Add(id);

            if (id == view.LastEventId)
            {
                break;
            }
        }

        resumed.ShouldBe([18, 19, 20, 21]);
    }

    [Fact]
    public void Another_callers_conversation_reads_as_unknown()
    {
        AssistantCore core = Core(new ScriptedProvider());
        AssistantSession session = Open(core);

        core.Find(Operator, session.Id).ShouldBeSameAs(session);
        core.Find(new Caller("someone", "Someone"), session.Id).ShouldBeNull();
        core.Delete(new Caller("someone", "Someone"), session.Id).ShouldBeFalse();
        core.Delete(Operator, session.Id).ShouldBeTrue();
        core.Find(Operator, session.Id).ShouldBeNull();
    }

    [Fact]
    public void A_caller_at_MaxConversations_forgets_the_least_recently_used_idle_one()
    {
        FakeTimeProvider time = new();
        AssistantCore core = Core(new ScriptedProvider(), new AssistantLimits { MaxConversations = 2 }, time);

        AssistantSession first = Open(core);
        time.Advance(TimeSpan.FromSeconds(1));
        AssistantSession second = Open(core);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = first.View();
        AssistantSession third = Open(core);

        core.Find(Operator, first.Id).ShouldNotBeNull();
        core.Find(Operator, second.Id).ShouldBeNull();
        core.Find(Operator, third.Id).ShouldNotBeNull();
    }

    [Fact]
    public void An_idle_conversation_is_forgotten_after_the_idle_timeout()
    {
        FakeTimeProvider time = new();
        AssistantCore core =
            Core(new ScriptedProvider(), new AssistantLimits { IdleTimeout = TimeSpan.FromMinutes(5) }, time);
        AssistantSession session = Open(core);

        time.Advance(TimeSpan.FromMinutes(6));

        core.Find(Operator, session.Id).ShouldBeNull();
    }

    [Fact]
    public void An_idle_conversation_is_disposed_by_the_timer_though_nothing_opens_or_finds_one()
    {
        FakeTimeProvider time = new();
        AssistantCore core =
            Core(new ScriptedProvider(), new AssistantLimits { IdleTimeout = TimeSpan.FromMinutes(5) }, time);
        AssistantSession session = Open(core);

        time.Advance(TimeSpan.FromMinutes(7));

        session.TryStart(Given.Turn("hello")).ShouldBeFalse();
    }

    [Fact]
    public void A_disposed_core_stops_sweeping()
    {
        FakeTimeProvider time = new();
        AssistantCore core =
            Core(new ScriptedProvider(), new AssistantLimits { IdleTimeout = TimeSpan.FromMinutes(5) }, time);
        AssistantSession session = Open(core);

        core.Dispose();
        time.Advance(TimeSpan.FromMinutes(7));

        session.TryStart(Given.Turn("hello")).ShouldBeTrue();
    }

    [Fact]
    public async Task A_redactor_that_throws_on_a_tool_result_ends_the_turn_with_error_and_frees_it()
    {
        ScriptedTools tools =
            new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, "UNREDACTABLE output"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "stack_status", []), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider, redactor: new ThrowingRedactor("UNREDACTABLE"), sources: tools));

        session.TryStart(Given.Turn("status"));
        List<AssistantEvent> events = await Given.UntilEnded(session);

        events.OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Error);
        session.Running.ShouldBeFalse();
        session.View().Items.OfType<ToolItem>().Single().Ok.ShouldBe(false);
        session.History[^1].ShouldBeOfType<ToolResultsMessage>().Results.Single().Ok.ShouldBeFalse();
    }

    /// <summary>
    /// Throws on any text holding <paramref name="trigger"/>, with none on every text, naming the text it could not
    /// redact, so the failure's own message trips it again.
    /// </summary>
    private sealed class ThrowingRedactor(string? trigger = null) : IRedactor
    {
        public string Redact(string text, Caller caller) =>
            trigger is null || text.Contains(trigger, StringComparison.Ordinal)
                ? throw new InvalidOperationException($"could not redact: {text}")
                : text;
    }

    private static async Task<T> NextOf<T>(AssistantSession session)
        where T : AssistantEvent
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await foreach ((long _, AssistantEvent value) in session.Events(0, timeout.Token))
        {
            if (value is T found)
            {
                return found;
            }
        }

        throw new InvalidOperationException($"No {typeof(T).Name} arrived.");
    }
}

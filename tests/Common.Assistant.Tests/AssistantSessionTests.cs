using System.Text.Json.Nodes;
using System.Threading.Channels;
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
    public async Task The_host_learns_each_turn_s_usage_and_whose_turn_it_was()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta("Hi."), new UsageReport(new Usage(100, 7, 40)), new RoundEnd(ProviderStop.Done));
        TaskCompletionSource<TurnSummary> learnt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AssistantCore core = new(
            provider,
            [],
            ToolPolicy.ReadOnly,
            new PlantedRedactor(),
            "Host rules.",
            new AssistantLimits(),
            TimeProvider.System,
            learnt.SetResult);
        AssistantSession session = Open(core);

        session.TryStart(Given.Turn("hello")).ShouldBeTrue();
        TurnSummary summary = await learnt.Task.WaitAsync(TestContext.Current.CancellationToken);

        summary.Caller.ShouldBe(Operator);
        summary.ConversationId.ShouldBe(session.Id);
        summary.Usage.ShouldBe(new Usage(100, 7, 40));
        summary.Unreported.ShouldBe(Usage.None, "the provider reported this round");
    }

    [Fact]
    public async Task A_round_cancelled_before_its_usage_report_is_estimated_for_the_host_and_not_the_client()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedProvider provider = new ScriptedProvider().Round((_, ct) => ScriptedProvider.Hang("Look", started, ct));
        TaskCompletionSource<TurnSummary> learnt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AssistantLimits limits = new();
        using AssistantCore core = new(
            provider,
            [],
            ToolPolicy.ReadOnly,
            new PlantedRedactor(),
            "Host rules.",
            limits,
            TimeProvider.System,
            learnt.SetResult);
        AssistantSession session = Open(core);

        session.TryStart(Given.Turn("hello")).ShouldBeTrue();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        session.Cancel();
        TurnSummary summary = await learnt.Task.WaitAsync(TestContext.Current.CancellationToken);

        summary.Reason.ShouldBe(TurnEndReason.Cancelled);
        summary.Usage.ShouldBe(Usage.None, "nothing was reported, and the client's event carries what was");
        summary.Unreported.Input.ShouldBeGreaterThan(core.System.Length / 4, "the request carried the system text");
        summary.Unreported.Output.ShouldBe(limits.MaxOutputTokens);
    }

    [Fact]
    public async Task A_round_the_provider_refused_before_answering_is_not_estimated()
    {
        // An outage or a refused key: the provider throws before its first event, and bills nothing.
        TurnSummary summary = await SummaryAsync(new ScriptedProvider()
            .Round((_, _) => throw new HttpRequestException("503 from the provider")));

        summary.Reason.ShouldBe(TurnEndReason.Error);
        summary.Unreported.ShouldBe(Usage.None);
    }

    [Fact]
    public async Task A_round_broken_after_the_provider_started_it_is_estimated()
    {
        TurnSummary summary = await SummaryAsync(new ScriptedProvider()
            .Round((_, _) => StartedThenBroken()));

        summary.Reason.ShouldBe(TurnEndReason.Error);
        summary.Unreported.Output.ShouldBe(new AssistantLimits().MaxOutputTokens);
    }

    [Fact]
    public async Task A_provider_failure_reaches_the_host_by_type_and_the_client_without_its_message_unless_asked()
    {
        ScriptedProvider Failing() => new ScriptedProvider()
            .Round((_, _) => throw new HttpRequestException("balance of the owner's account is low"));

        TurnSummary summary = await SummaryAsync(Failing());
        AssistantSession quiet = Open(Core(Failing()));
        AssistantSession verbose = Open(Core(Failing(), new AssistantLimits { ErrorDetail = true }));

        quiet.TryStart(Given.Turn("hello"));
        verbose.TryStart(Given.Turn("hello"));
        ErrorEvent withheld = (await Given.UntilEnded(quiet)).OfType<ErrorEvent>().Single();
        ErrorEvent shown = (await Given.UntilEnded(verbose)).OfType<ErrorEvent>().Single();

        summary.Failure.ShouldBe(typeof(HttpRequestException).FullName);
        withheld.Type.ShouldBe("provider-error");
        withheld.Detail.ShouldBeNull();
        shown.Detail.ShouldBe("balance of the owner's account is low");
    }

    [Fact]
    public async Task A_client_that_lets_its_tool_calls_time_out_in_two_rounds_running_ends_the_turn()
    {
        FakeTimeProvider time = new();
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "open_screen", []), new RoundEnd(ProviderStop.Done))
            .Round(new ToolCall("c2", "open_screen", []), new RoundEnd(ProviderStop.Done))
            .Round(new ToolCall("c3", "open_screen", []), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(
            Core(provider, new AssistantLimits { ClientToolTimeout = TimeSpan.FromSeconds(30) }, time),
            clientTools: Given.ReadOnly("open_screen", ToolSide.Client));

        session.TryStart(Given.Turn("open"));
        Task<List<AssistantEvent>> ended = Given.UntilEnded(session);

        while (!ended.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        List<AssistantEvent> events = await ended;
        events.OfType<ErrorEvent>().Single().Type.ShouldBe("client-unresponsive");
        events.OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Error);
        provider.Requests.Count.ShouldBe(2, "the third round is never asked for");
        session.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_client_timeout_in_a_turn_cancelled_mid_round_is_not_counted_against_the_next_turn()
    {
        // The first turn leaves a client call timed out and a sibling server call running, then is cancelled, so its
        // round never reaches the line that reads and resets the count. The second turn answers its first round with
        // a server call alone and times out its second: inheriting the count, it would be two silent rounds running.
        FakeTimeProvider time = new();
        TaskCompletionSource slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedTools slow = new(("slow", async (_, _, ct) =>
        {
            slowStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);

            return new ToolOutcome(true, "unreachable");
        }
        ));
        ScriptedTools quick = new(("quick", (_, _, _) => Task.FromResult(new ToolOutcome(true, "done"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new ToolCall("c1", "open_screen", []),
                new ToolCall("s1", "slow", []),
                new RoundEnd(ProviderStop.Done))
            .Round(new ToolCall("s2", "quick", []), new RoundEnd(ProviderStop.Done))
            .Round(new ToolCall("c2", "open_screen", []), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Opened."), new RoundEnd(ProviderStop.Done));
        Channel<TurnSummary> summaries = Channel.CreateUnbounded<TurnSummary>();
        using AssistantCore core = new(
            provider,
            [slow, quick],
            ToolPolicy.ReadOnly,
            new PlantedRedactor(),
            "Host rules.",
            new AssistantLimits
            {
                ClientToolTimeout = TimeSpan.FromSeconds(30),
                ServerToolTimeout = TimeSpan.FromMinutes(10),
            },
            time,
            summary => _ = summaries.Writer.TryWrite(summary));
        AssistantSession session = Open(core, clientTools: Given.ReadOnly("open_screen", ToolSide.Client));

        session.TryStart(Given.Turn("open")).ShouldBeTrue();
        await slowStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(31));

        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            await foreach ((long _, AssistantEvent seen) in session.Events(0, timeout.Token))
            {
                if (seen is ToolFinishedEvent { CallId: "c1" })
                {
                    break;
                }
            }
        }

        session.Cancel();
        TurnSummary first = await summaries.Reader.ReadAsync(TestContext.Current.CancellationToken);
        first.Reason.ShouldBe(TurnEndReason.Cancelled);

        session.TryStart(Given.Turn("again")).ShouldBeTrue();

        TurnSummary second;

        while (!summaries.Reader.TryRead(out second!))
        {
            time.Advance(TimeSpan.FromSeconds(31));
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        second.Reason.ShouldBe(TurnEndReason.Done, "one timed-out round is not two in a row");
        provider.Requests.Count.ShouldBe(4, "the turn went on to its third round");
    }

    [Fact]
    public async Task A_round_that_ended_whole_without_a_report_is_not_estimated()
    {
        TurnSummary summary = await SummaryAsync(new ScriptedProvider()
            .Round(new TextDelta("Whole."), new RoundEnd(ProviderStop.Done)));

        summary.Reason.ShouldBe(TurnEndReason.Done);
        summary.Unreported.ShouldBe(Usage.None);
    }

    private static async IAsyncEnumerable<ProviderEvent> StartedThenBroken()
    {
        yield return new RoundStarted();
        await Task.Yield();

        throw new IOException("The provider's stream ended before the reply was complete.");
    }

    private static async Task<TurnSummary> SummaryAsync(ScriptedProvider provider)
    {
        TaskCompletionSource<TurnSummary> learnt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AssistantCore core = new(
            provider,
            [],
            ToolPolicy.ReadOnly,
            new PlantedRedactor(),
            "Host rules.",
            new AssistantLimits(),
            TimeProvider.System,
            learnt.SetResult);

        Open(core).TryStart(Given.Turn("hello")).ShouldBeTrue();

        return await learnt.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

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
    public async Task A_conversation_at_its_context_budget_holds_no_further_message_and_ends_each_turn_with_budget()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta("Full."), new UsageReport(new Usage(1000, 10, 0)), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider, new AssistantLimits { ConversationTokenBudget = 1000 }));

        session.TryStart(Given.Turn("fill it"));
        await Given.UntilEnded(session);
        int history = session.History.Count;
        long held = session.Held;

        for (int i = 0; i < 3; i++)
        {
            long after = session.View().LastEventId;
            session.TryStart(Given.Turn(new string('m', 4000))).ShouldBeTrue();
            (await Given.UntilEnded(session, after)).OfType<TurnEndedEvent>().Single().Reason
                .ShouldBe(TurnEndReason.Budget);
        }

        provider.Requests.Count.ShouldBe(1);
        session.History.Count.ShouldBe(history);
        session.Held.ShouldBe(held);
        session.View().Items.Count(item => item is EndItem { Reason: TurnEndReason.Budget }).ShouldBe(1);
    }

    [Fact]
    public async Task A_turn_that_takes_its_caller_past_their_history_budget_ends_with_budget_before_its_next_round()
    {
        ScriptedTools tools = new(("stack_status", (_, _, _) => Task.FromResult(new ToolOutcome(true, "up"))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta(new string('x', 4000)), new ToolCall("c1", "stack_status", []),
                new RoundEnd(ProviderStop.Done));
        AssistantSession session =
            Open(Core(provider, new AssistantLimits { CallerHistoryChars = 3000 }, sources: tools));

        session.TryStart(Given.Turn("status"));
        List<AssistantEvent> events = await Given.UntilEnded(session);

        events.OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Budget);
        provider.Requests.Count.ShouldBe(1);
        session.Held.ShouldBeGreaterThanOrEqualTo(3000);
    }

    [Fact]
    public async Task A_conversation_at_the_history_budget_alone_is_refused_and_leaves_the_callers_others()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta("Hi."), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta(new string('x', 4000)), new RoundEnd(ProviderStop.Done));
        AssistantCore core = Core(provider, new AssistantLimits { CallerHistoryChars = 3000 });
        AssistantSession small = Open(core);
        AssistantSession large = Open(core);

        small.TryStart(Given.Turn("one"));
        await Given.UntilEnded(small);
        large.TryStart(Given.Turn("two"));
        await Given.UntilEnded(large);

        long after = large.View().LastEventId;
        large.TryStart(Given.Turn("three")).ShouldBeTrue();
        (await Given.UntilEnded(large, after)).OfType<TurnEndedEvent>().Single().Reason
            .ShouldBe(TurnEndReason.Budget);

        provider.Requests.Count.ShouldBe(2);
        core.Find(Operator, small.Id).ShouldBeSameAs(small);
    }

    [Fact]
    public async Task A_caller_past_their_history_budget_keeps_a_conversation_whose_turn_is_running()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(
                new TextDelta(new string('a', 1500)),
                new ToolCall("c1", "open_screen", new JsonObject { ["path"] = "logs" }),
                new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta(new string('b', 1000)), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Opened."), new RoundEnd(ProviderStop.Done));
        AssistantCore core = Core(provider, new AssistantLimits { CallerHistoryChars = 5000 });
        AssistantSession parked = Open(core, clientTools: Given.ReadOnly("open_screen", ToolSide.Client));
        AssistantSession other = Open(core);

        parked.TryStart(Given.Turn("open logs"));
        Task<List<AssistantEvent>> parkedEnded = Given.UntilEnded(parked);
        ClientToolCallEvent call = await NextOf<ClientToolCallEvent>(parked);
        other.TryStart(Given.Turn("two"));
        await Given.UntilEnded(other);
        (parked.Held + other.Held).ShouldBeGreaterThanOrEqualTo(5000, "the two together are at the budget");
        other.Held.ShouldBeLessThan(5000, "the other alone is not");

        long after = other.View().LastEventId;
        other.TryStart(Given.Turn("three"));
        (await Given.UntilEnded(other, after)).OfType<TurnEndedEvent>().Single().Reason
            .ShouldBe(TurnEndReason.Budget);

        core.Find(Operator, parked.Id).ShouldBeSameAs(parked);
        parked.TryAnswer(new ClientToolResult(call.CallId, true, "opened")).ShouldBeTrue();
        (await parkedEnded).OfType<TurnEndedEvent>().Single().Reason.ShouldBe(TurnEndReason.Done);
    }

    [Fact]
    public async Task A_caller_past_their_history_budget_forgets_their_least_recently_used_idle_conversation()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta(new string('a', 2000)), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta(new string('b', 2000)), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Again."), new RoundEnd(ProviderStop.Done));
        AssistantCore core = Core(provider, new AssistantLimits { CallerHistoryChars = 6000 });
        AssistantSession first = Open(core);
        AssistantSession second = Open(core);

        first.TryStart(Given.Turn("one"));
        await Given.UntilEnded(first);
        second.TryStart(Given.Turn("two"));
        await Given.UntilEnded(second);
        (first.Held + second.Held).ShouldBeGreaterThan(6000, "the two together are past the budget");

        long after = second.View().LastEventId;
        second.TryStart(Given.Turn("three"));
        (await Given.UntilEnded(second, after)).OfType<TurnEndedEvent>().Single().Reason
            .ShouldBe(TurnEndReason.Done);

        core.Find(Operator, first.Id).ShouldBeNull();
        core.Find(Operator, second.Id).ShouldBeSameAs(second);
        first.TryStart(Given.Turn("gone")).ShouldBeFalse();
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
        session.Start(Given.Turn("two")).ShouldBe(TurnStart.Running);
        session.View().Running.ShouldBeTrue();
        session.Cancel();
        await Given.UntilEnded(session);
    }

    [Fact]
    public void A_turn_sent_to_a_conversation_forgotten_after_it_was_found_is_refused_as_closed_not_running()
    {
        AssistantCore core = Core(new ScriptedProvider());
        AssistantSession session = Open(core);

        core.Delete(Operator, session.Id).ShouldBeTrue();

        session.Start(Given.Turn("hello")).ShouldBe(TurnStart.Closed);
    }

    [Fact]
    public async Task A_blank_locale_on_a_turn_keeps_the_conversations_own()
    {
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new TextDelta("One."), new RoundEnd(ProviderStop.Done))
            .Round(new TextDelta("Two."), new RoundEnd(ProviderStop.Done));
        AssistantSession session = Open(Core(provider), "en-GB");

        session.TryStart(Given.Turn("one", "   ")).ShouldBeTrue();
        await Given.UntilEnded(session);
        session.TryStart(Given.Turn("two")).ShouldBeTrue();
        await Given.UntilEnded(session);

        provider.Requests.Select(r => r.History.OfType<UserMessage>().Last().Content)
            .ShouldAllBe(text => text.Contains("reply in en-GB."));
    }

    [Fact]
    public async Task A_stream_that_ends_without_its_round_end_is_a_round_cut_short_not_a_whole_reply()
    {
        TaskCompletionSource<TurnSummary> learnt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AssistantCore core = new(
            new ScriptedProvider().Round(new RoundStarted(), new TextDelta("Half a")),
            [],
            ToolPolicy.ReadOnly,
            new PlantedRedactor(),
            "Host rules.",
            new AssistantLimits(),
            TimeProvider.System,
            learnt.SetResult);
        AssistantSession session = Open(core);

        session.TryStart(Given.Turn("hello")).ShouldBeTrue();
        TurnSummary summary = await learnt.Task.WaitAsync(TestContext.Current.CancellationToken);

        summary.Reason.ShouldBe(TurnEndReason.Error);
        summary.Unreported.Output.ShouldBe(new AssistantLimits().MaxOutputTokens, "the round was billed unreported");
        session.History.OfType<AssistantMessage>().Single().Text.ShouldBe("Half a");
    }

    [Fact]
    public async Task A_caller_is_forgotten_whole_or_not_at_all_when_one_of_their_conversations_is_running()
    {
        TaskCompletionSource started = new();
        ScriptedProvider provider = new ScriptedProvider()
            .Round((_, ct) => ScriptedProvider.Hang("…", started, ct))
            .Round(new TextDelta("Two."), new RoundEnd(ProviderStop.Done));
        AssistantCore core = Core(provider);
        AssistantSession quiet = Open(core);
        AssistantSession busy = Open(core);
        AssistantSession idle = Open(Core(new ScriptedProvider()));

        busy.TryStart(Given.Turn("one")).ShouldBeTrue();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The idle one listed first is judged first, and is not closed when the running one refuses the set.
        AssistantSession.TryForgetAll([quiet, busy]).ShouldBeFalse();
        quiet.Start(Given.Turn("two")).ShouldBe(TurnStart.Started);
        await Given.UntilEnded(quiet);
        AssistantSession.TryForgetAll([idle]).ShouldBeTrue();
        idle.Start(Given.Turn("three")).ShouldBe(TurnStart.Closed);

        busy.Cancel();
        await Given.UntilEnded(busy);
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
    public async Task A_tool_result_cut_inside_a_surrogate_pair_keeps_no_half_character()
    {
        ScriptedTools tools = new(("stack_status", (_, _, _) =>
            Task.FromResult(new ToolOutcome(true, new string('x', 99) + "\U0001F600" + new string('y', 50)))));
        ScriptedProvider provider = new ScriptedProvider()
            .Round(new ToolCall("c1", "stack_status", []), new RoundEnd(ProviderStop.Done))
            .Round(new RoundEnd(ProviderStop.Done));
        AssistantSession session =
            Open(Core(provider, new AssistantLimits { MaxToolResultChars = 100 }, sources: tools));

        session.TryStart(Given.Turn("status"));
        await Given.UntilEnded(session);

        string content = provider.Requests[1].History[^1].ShouldBeOfType<ToolResultsMessage>().Results.Single().Content;
        content.ShouldStartWith(new string('x', 99) + "\n[cut to 100 characters]");
        content.Any(char.IsSurrogate).ShouldBeFalse();
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
    public void An_IdleTimeout_of_a_year_still_builds_a_core_on_the_system_clock()
    {
        AssistantCore core = Core(
            new ScriptedProvider(),
            new AssistantLimits { IdleTimeout = TimeSpan.FromDays(365) },
            TimeProvider.System);

        core.Dispose();
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

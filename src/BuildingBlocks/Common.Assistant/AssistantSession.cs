using System.Text;
using System.Text.Json.Nodes;
using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Common.Assistant.Tools;

namespace Common.Assistant;

/// <summary>
/// One conversation and its loop (AI assistant design §7). A turn builds the request in cache order, streams one
/// provider round, runs every tool the round asked for, adds all their results as one message and goes round again
/// until the turn ends.
/// </summary>
public sealed class AssistantSession : IDisposable
{
    private readonly Lock gate = new();
    private readonly AssistantCore core;
    private readonly ToolRegistry registry;
    private readonly IReadOnlyList<CatalogueEntry> catalogue;
    // The history is append-only, and it is always one a provider accepts: a round that asked for tools is followed by
    // a result for every call, an error result where the call never ran (a cancel, the step limit), so the next turn
    // can build on it.
    private readonly List<HistoryMessage> history = [];
    private readonly List<ConversationItem> items = [];
    private readonly Dictionary<string, (ClientToolCallEvent Call, TaskCompletionSource<ToolOutcome> Answer)> pending =
        new(StringComparer.Ordinal);
    private readonly EventLog log;
    private bool closed;
    private CancellationTokenSource? turn;
    private string locale;
    private JsonObject hostOptions;
    private Usage total = Usage.None;
    private long contextTokens;

    internal AssistantSession(
        string id,
        Caller caller,
        OpenConversation opened,
        ToolRegistry registry,
        AssistantCore core)
    {
        Id = id;
        Caller = caller;
        this.core = core;
        this.registry = registry;
        catalogue = opened.Catalogue;
        locale = opened.Locale;
        hostOptions = opened.HostOptions ?? [];
        log = new EventLog(core.Limits.EventRing);
        LastActive = core.Time.GetUtcNow();
    }

    public string Id { get; }

    public Caller Caller { get; }

    public DateTimeOffset LastActive { get; private set; }

    public bool Running
    {
        get
        {
            lock (gate)
            {
                return turn is not null;
            }
        }
    }

    /// <summary>
    /// The history as the provider is sent it, for tests and adapters' recordings; nothing outside the core edits it.
    /// </summary>
    public IReadOnlyList<HistoryMessage> History
    {
        get
        {
            lock (gate)
            {
                return [.. history];
            }
        }
    }

    /// <summary>Starts a turn; false when one is already running, or the conversation is gone.</summary>
    public bool TryStart(StartTurn request)
    {
        lock (gate)
        {
            if (turn is not null || closed)
            {
                return false;
            }

            // The host's redactor runs before anything changes, so one that throws leaves the conversation as it was
            // rather than holding a turn with no loop behind it.
            string turnLocale = request.Locale is { Length: > 0 } changedLocale ? changedLocale : locale;
            UserMessage message = new(
                core.Redactor.Redact(Prompt.Turn(request.Text, request.Context, turnLocale), Caller));

            locale = turnLocale;
            hostOptions = request.HostOptions ?? hostOptions;
            turn = new CancellationTokenSource();
            items.Add(new OperatorItem(request.Text));
            history.Add(message);
            LastActive = core.Time.GetUtcNow();

            CancellationToken token = turn.Token;
            _ = Task.Run(() => RunAsync(token), CancellationToken.None);

            return true;
        }
    }

    /// <summary>Hands a client tool call its result; false when no call of that id is waiting.</summary>
    public bool TryAnswer(ClientToolResult result)
    {
        TaskCompletionSource<ToolOutcome> waiting;

        lock (gate)
        {
            if (!pending.Remove(
                    result.CallId,
                    out (ClientToolCallEvent Call, TaskCompletionSource<ToolOutcome> Answer) call))
            {
                return false;
            }

            waiting = call.Answer;
        }

        return waiting.TrySetResult(new ToolOutcome(result.Ok, result.Content));
    }

    /// <summary>Ends the running turn, if any; a provider stream in flight is cancelled.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            turn?.Cancel();
        }
    }

    public ConversationView View()
    {
        lock (gate)
        {
            LastActive = core.Time.GetUtcNow();

            return new ConversationView(
                Id,
                [.. items],
                turn is not null,
                log.LastId,
                [.. pending.Values.Select(p => p.Call)]);
        }
    }

    public IAsyncEnumerable<(long Id, AssistantEvent Event)> Events(
        long afterId,
        CancellationToken cancellationToken) =>
        log.Follow(afterId, cancellationToken);

    /// <summary>
    /// Forgets the conversation: its turn is cancelled and every stream of it ends. The turn's own token is disposed by
    /// the turn as it ends, since its loop may still be reading it.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            turn?.Cancel();
        }

        log.Close();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // The turn ends when the provider is done, it reaches MaxSteps tool rounds or the context budget, the caller
        // cancels, or the provider refuses. Server calls run concurrently, each with a timeout; client calls are
        // emitted and waited for. A turn is running work, not polling: it goes on with no stream open, until it ends.
        Usage used = Usage.None;
        string reason = TurnEndReason.Done;
        string? category = null;

        try
        {
            for (int step = 0; ; step++)
            {
                if (Interlocked.Read(ref contextTokens) >= core.Limits.ConversationTokenBudget)
                {
                    reason = TurnEndReason.Budget;

                    break;
                }

                Round round = new();

                try
                {
                    await StreamAsync(round, cancellationToken);
                }
                finally
                {
                    used += round.Usage;

                    // A round cut short keeps the text it had; its calls and blocks are dropped with it, since a
                    // provider would refuse a call with no result and a block it never finished.
                    AppendReply(round, complete: round.Stop is not null);
                }

                if (round.Stop == ProviderStop.Refused)
                {
                    reason = TurnEndReason.Refused;
                    category = round.Category;

                    break;
                }

                if (round.Calls.Count == 0)
                {
                    break;
                }

                if (step == core.Limits.MaxSteps)
                {
                    reason = TurnEndReason.MaxSteps;

                    break;
                }

                ToolResult[] results =
                    await Task.WhenAll(round.Calls.Select(call => CallAsync(call, cancellationToken)));

                lock (gate)
                {
                    history.Add(new ToolResultsMessage(results));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reason = TurnEndReason.Cancelled;
        }
        catch (Exception e)
        {
            reason = TurnEndReason.Error;
            Emit(new ErrorEvent("provider-error", "The assistant's provider failed", 502, Detail(e)));
        }

        End(reason, category, used);
    }

    private async Task StreamAsync(Round round, CancellationToken cancellationToken)
    {
        TurnRequest request;

        lock (gate)
        {
            request = new TurnRequest(registry.All, core.System, catalogue, [.. history], core.Limits.MaxOutputTokens);
        }

        await foreach (ProviderEvent value in core.Provider.TurnAsync(request, cancellationToken))
        {
            switch (value)
            {
                case TextDelta delta when delta.Text.Length > 0:
                    lock (gate)
                    {
                        round.Text.Append(delta.Text);

                        if (round.ReplyAt is int at)
                        {
                            items[at] = new ReplyItem(round.Text.ToString());
                        }
                        else
                        {
                            round.ReplyAt = items.Count;
                            items.Add(new ReplyItem(round.Text.ToString()));
                        }

                        log.Append(new TextEvent(delta.Text));
                    }

                    break;
                case ToolCall call:
                    round.Calls.Add(call);

                    break;
                case Opaque block:
                    round.Opaque.Add(block.Payload);

                    break;
                case UsageReport report:
                    round.Usage += report.Usage;
                    Interlocked.Exchange(ref contextTokens, report.Usage.Input + report.Usage.Cached);

                    break;
                case RoundEnd end:
                    round.Stop = end.Stop;
                    round.Category = end.Category;

                    break;
            }
        }

        round.Stop ??= ProviderStop.Done;
    }

    private void AppendReply(Round round, bool complete)
    {
        lock (gate)
        {
            if (complete && (round.Text.Length > 0 || round.Calls.Count > 0 || round.Opaque.Count > 0))
            {
                history.Add(new AssistantMessage(round.Text.ToString(), [.. round.Calls], [.. round.Opaque]));
            }
            else if (round.Text.Length > 0)
            {
                round.Calls.Clear();
                history.Add(new AssistantMessage(round.Text.ToString(), [], []));
            }
        }
    }

    private async Task<ToolResult> CallAsync(ToolCall call, CancellationToken cancellationToken)
    {
        ToolSide? side = registry.SideOf(call.Name);
        int at;

        lock (gate)
        {
            at = items.Count;
            items.Add(new ToolItem(call.Id, call.Name, side ?? ToolSide.Server, LabelArgs(call.Input), null));
            log.Append(new ToolStartedEvent(call.Id, call.Name, side ?? ToolSide.Server, LabelArgs(call.Input)));
        }

        ToolOutcome outcome = side switch
        {
            ToolSide.Server => await ServerAsync(call, cancellationToken),
            ToolSide.Client => await ClientAsync(call, cancellationToken),
            _ => new ToolOutcome(false, $"There is no tool named {call.Name}."),
        };

        string content = Bound(core.Redactor.Redact(outcome.Content, Caller));

        lock (gate)
        {
            items[at] = ((ToolItem)items[at]) with { Ok = outcome.Ok };
            log.Append(new ToolFinishedEvent(call.Id, outcome.Ok));
        }

        return new ToolResult(call.Id, outcome.Ok, content);
    }

    private async Task<ToolOutcome> ServerAsync(ToolCall call, CancellationToken cancellationToken)
    {
        if (!registry.TryServer(call.Name, out IToolSource? source) || source is null)
        {
            return new ToolOutcome(false, $"There is no tool named {call.Name}.");
        }

        using CancellationTokenSource timeout = new(core.Limits.ServerToolTimeout, core.Time);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        ToolContext context;

        lock (gate)
        {
            context = new ToolContext(Caller, Id, hostOptions);
        }

        try
        {
            return await source.InvokeAsync(call.Name, call.Input, context, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ToolOutcome(
                false,
                $"{call.Name} did not answer within {core.Limits.ServerToolTimeout.TotalSeconds:0} s.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ToolOutcome(false, $"{call.Name} failed: {e.Message}");
        }
    }

    private async Task<ToolOutcome> ClientAsync(ToolCall call, CancellationToken cancellationToken)
    {
        TaskCompletionSource<ToolOutcome> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (gate)
        {
            ClientToolCallEvent asked = new(call.Id, call.Name, call.Input);
            pending[call.Id] = (asked, answer);
            log.Append(asked);
        }

        try
        {
            return await answer.Task.WaitAsync(core.Limits.ClientToolTimeout, core.Time, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new ToolOutcome(
                false,
                $"The client did not answer {call.Name} within {core.Limits.ClientToolTimeout.TotalSeconds:0} s.");
        }
        finally
        {
            lock (gate)
            {
                pending.Remove(call.Id);
            }
        }
    }

    private void End(string reason, string? category, Usage used)
    {
        TurnSummary summary;

        lock (gate)
        {
            // A round whose calls have no results yet (the step limit, a refusal, a cancel mid-round) gets one for
            // each, so the next turn's request is one a provider accepts.
            if (history.LastOrDefault() is AssistantMessage { Calls.Count: > 0 } asked)
            {
                history.Add(new ToolResultsMessage(
                    [.. asked.Calls.Select(c => new ToolResult(c.Id, false, $"Not run: the turn ended ({reason})."))]));
            }

            // A call the turn ended in the middle of (a cancel, an error) has no outcome yet: it failed, and the client
            // is told so, or it would show the call pending after the turn has ended.
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is ToolItem { Ok: null } unfinished)
                {
                    items[i] = unfinished with { Ok = false };
                    log.Append(new ToolFinishedEvent(unfinished.CallId, false));
                }
            }

            total += used;
            items.Add(new EndItem(reason, category));
            log.Append(new TurnEndedEvent(reason, category, used, total));
            turn?.Dispose();
            turn = null;
            LastActive = core.Time.GetUtcNow();
            summary = new TurnSummary(core.Provider.Id, Id, reason, used, total);
        }

        core.TurnEnded?.Invoke(summary);
    }

    // The failure's message may hold what a tool or the provider returned, so it is redacted too; a redactor that is
    // itself what failed gets no second call, or the turn would never reach its end.
    private string? Detail(Exception failure)
    {
        try
        {
            return core.Redactor.Redact(failure.Message, Caller);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Emit(AssistantEvent value)
    {
        lock (gate)
        {
            log.Append(value);
        }
    }

    private string Bound(string content) =>
        content.Length <= core.Limits.MaxToolResultChars
            ? content
            : string.Concat(
                content.AsSpan(0, core.Limits.MaxToolResultChars),
                $"\n[cut to {core.Limits.MaxToolResultChars} characters]");

    /// <summary>
    /// The few input values a label shows (design §5.3): top-level strings, numbers and booleans, each short.
    /// </summary>
    private static JsonObject LabelArgs(JsonObject input)
    {
        JsonObject args = [];

        foreach ((string key, JsonNode? value) in input.Take(4))
        {
            if (value is JsonValue scalar)
            {
                string text = scalar.ToString();
                args[key] = text.Length <= 80 ? scalar.DeepClone() : JsonValue.Create(text[..80]);
            }
        }

        return args;
    }

    private sealed class Round
    {
        public StringBuilder Text { get; } = new();

        public List<ToolCall> Calls { get; } = [];

        public List<JsonNode> Opaque { get; } = [];

        public Usage Usage { get; set; } = Usage.None;

        public ProviderStop? Stop { get; set; }

        public string? Category { get; set; }

        public int? ReplyAt { get; set; }
    }
}

/// <summary>What a host logs of a turn (design §10): provider, conversation, end reason, usage; never text.</summary>
public sealed record TurnSummary(string Provider, string ConversationId, string Reason, Usage Usage, Usage Total);

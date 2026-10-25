using Common.Assistant.Protocol;
using Common.Assistant.Providers;
using Common.Assistant.Tools;

namespace Common.Assistant;

/// <summary>
/// The core a host builds once (AI assistant design §4): its provider, its tool sources under its policy, its redactor,
/// its system text and its limits, and the conversations open against them. The host authenticates and passes the
/// <see cref="Caller"/>; the core keys everything by it. It sweeps idle conversations on a timer of
/// <see cref="TimeProvider"/>, so a conversation outlives <see cref="AssistantLimits.IdleTimeout"/> by at most a
/// quarter of it (a second where that is longer), and disposing the core stops the timer.
/// </summary>
public sealed class AssistantCore : IDisposable
{
    private static readonly TimeSpan MinSweepPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxSweepPeriod = TimeSpan.FromDays(1);

    private readonly IReadOnlyList<IToolSource> sources;
    private readonly ToolPolicy policy;
    private readonly ConversationStore store;
    private readonly ITimer sweep;

    /// <exception cref="ToolRefusedException">A host source offers a tool the policy refuses.</exception>
    public AssistantCore(
        IAssistantProvider provider,
        IReadOnlyList<IToolSource> sources,
        ToolPolicy policy,
        IRedactor redactor,
        string hostInstructions,
        AssistantLimits limits,
        TimeProvider time,
        Action<TurnSummary>? turnEnded = null)
    {
        // Every server tool is admitted here, at construction, so a host whose source offers a tool the policy refuses
        // fails to start rather than at a turn.
        _ = ToolRegistry.Build(sources, [], policy);
        Provider = provider;
        this.sources = sources;
        this.policy = policy;
        Redactor = redactor;
        System = hostInstructions.Length == 0 ? Prompt.Core : $"{Prompt.Core}\n{hostInstructions}";
        Limits = limits;
        Time = time;
        TurnEnded = turnEnded;
        store = new ConversationStore(limits, time);

        // Capped, because a timer refuses a period past about 49 days and a long IdleTimeout is valid; a shorter
        // period only tightens the bound.
        TimeSpan period = TimeSpan.FromTicks(
            Math.Clamp(limits.IdleTimeout.Ticks / 4, MinSweepPeriod.Ticks, MaxSweepPeriod.Ticks));
        sweep = time.CreateTimer(_ => store.SweepIdle(), null, period, period);
    }

    /// <summary>Stops the sweep. Conversations already open are left to the process.</summary>
    public void Dispose() => sweep.Dispose();

    public IAssistantProvider Provider { get; }

    public IRedactor Redactor { get; }

    /// <summary>The fixed system text: the core's, then the host's. The catalogue follows it in each request.</summary>
    public string System { get; }

    public AssistantLimits Limits { get; }

    public TimeProvider Time { get; }

    internal Action<TurnSummary>? TurnEnded { get; }

    public async ValueTask<AssistantStatus> StatusAsync(CancellationToken cancellationToken)
    {
        ProviderStatus status = await Provider.StatusAsync(cancellationToken);

        return new AssistantStatus(
            status.Available,
            Provider.Id,
            status.Model,
            status.Available ? null : status.Reason);
    }

    /// <summary>A new conversation, or null when the caller's store is full of running turns.</summary>
    /// <exception cref="ToolRefusedException">
    /// The client offers a tool the policy refuses, or one whose name is taken.
    /// </exception>
    public AssistantSession? Open(Caller caller, OpenConversation request)
    {
        ToolRegistry registry = ToolRegistry.Build(sources, request.ClientTools, policy);

        return store.Add(caller, id => new AssistantSession(id, caller, request, registry, this));
    }

    /// <summary>The caller's conversation of that id; null for an unknown one and for another caller's alike.</summary>
    public AssistantSession? Find(Caller caller, string id) => store.Find(caller, id);

    public bool Delete(Caller caller, string id) => store.Delete(caller, id);
}

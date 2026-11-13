using Common.Assistant;
using Common.Assistant.Anthropic;

namespace Web.Bff.Assistant;

/// <summary>The <c>Assistant</c> section: the provider, the core's limits and this host's per customer.</summary>
/// <remarks>
/// No key here holds a credential, and <see cref="AssistantRuntime"/> refuses one named like it (AI assistant design
/// §9). The core owns each limit's default but <see cref="MaxCallers"/>, whose default is this host's (ADR-204).
/// </remarks>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public const string Off = "off";

    public const string Anthropic = "anthropic";

    /// <summary><see cref="ScriptedReplyProvider"/>: no key, and Development only (ADR-156).</summary>
    public const string Scripted = "scripted";

    private static readonly AssistantLimits Defaults = new();

    /// <summary>
    /// <c>off</c>, <c>anthropic</c> or <c>scripted</c>; unset is off, which builds no provider (design §9.1).
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>The model the Anthropic provider asks; the adapter's constant, which loads nothing of it.</summary>
    public string Model { get; set; } = AnthropicSettings.DefaultModel;

    public string Effort { get; set; } = AnthropicSettings.DefaultEffort;

    /// <summary>The conversations and turns one customer may start a minute: this host's limit.</summary>
    public int TurnsPerMinute { get; set; } = 10;

    /// <summary>The tokens one customer's turns may spend in a UTC day before new work is refused (ADR-204).</summary>
    public long DailyTokensPerCustomer { get; set; } = 1_000_000;

    /// <summary>The tokens every customer's turns together may spend in a UTC day (ADR-204).</summary>
    public long DailyTokensForHost { get; set; } = 50_000_000;

    /// <summary>The event streams one customer may hold open at once (ADR-204).</summary>
    public int StreamsPerCustomer { get; set; } = 8;

    public int MaxSteps { get; set; } = Defaults.MaxSteps;

    public int MaxOutputTokens { get; set; } = Defaults.MaxOutputTokens;

    public long ConversationTokenBudget { get; set; } = Defaults.ConversationTokenBudget;

    public int MaxConversations { get; set; } = Defaults.MaxConversations;

    /// <summary>
    /// The customers one replica holds conversations for: this host's, as the core's suits a console with one caller
    /// (ADR-204).
    /// </summary>
    public int MaxCallers { get; set; } = 256;

    /// <summary>
    /// The characters one customer's conversations may hold together, which with <see cref="MaxCallers"/> bounds what
    /// the replica holds (ADR-208).
    /// </summary>
    public long CallerHistoryChars { get; set; } = Defaults.CallerHistoryChars;

    /// <summary>How long an untouched conversation stays in this replica's memory (docs/personal-data.md).</summary>
    public TimeSpan IdleTimeout { get; set; } = Defaults.IdleTimeout;

    /// <summary>
    /// The provider in effect, or an exception naming the key when the value is not one or is <c>scripted</c> outside
    /// Development, where a fixed reply would answer a real customer (ADR-156).
    /// </summary>
    public string ProviderInEffect(IHostEnvironment environment)
    {
        string? configured = Provider?.Trim();

        return configured?.ToLowerInvariant() switch
        {
            null or "" or Off => Off,
            Anthropic => Anthropic,
            Scripted when environment.IsDevelopment() => Scripted,
            Scripted => throw new InvalidOperationException(
                $"{SectionName}:Provider is '{Scripted}' in {environment.EnvironmentName}; it runs only in " +
                $"{Environments.Development} (ADR-156)."),
            _ => throw new InvalidOperationException(
                $"{SectionName}:Provider is '{configured}'; it takes '{Off}', '{Anthropic}' or '{Scripted}'.")
        };
    }

    /// <summary>The core's limits, or an exception naming the first key that is not positive.</summary>
    public AssistantLimits Limits()
    {
        Positive(TurnsPerMinute, nameof(TurnsPerMinute));
        Positive(DailyTokensPerCustomer, nameof(DailyTokensPerCustomer));
        Positive(DailyTokensForHost, nameof(DailyTokensForHost));
        Positive(StreamsPerCustomer, nameof(StreamsPerCustomer));
        Positive(MaxSteps, nameof(MaxSteps));
        Positive(MaxOutputTokens, nameof(MaxOutputTokens));
        Positive(ConversationTokenBudget, nameof(ConversationTokenBudget));
        Positive(MaxConversations, nameof(MaxConversations));
        Positive(MaxCallers, nameof(MaxCallers));
        Positive(CallerHistoryChars, nameof(CallerHistoryChars));
        Positive(IdleTimeout.Ticks, nameof(IdleTimeout));

        return new AssistantLimits
        {
            MaxSteps = MaxSteps,
            MaxOutputTokens = MaxOutputTokens,
            ConversationTokenBudget = ConversationTokenBudget,
            MaxConversations = MaxConversations,
            MaxCallers = MaxCallers,
            CallerHistoryChars = CallerHistoryChars,
            IdleTimeout = IdleTimeout
        };
    }

    private static void Positive(long value, string key)
    {
        if (value <= 0)
            throw new InvalidOperationException($"{SectionName}:{key} must be positive.");
    }
}

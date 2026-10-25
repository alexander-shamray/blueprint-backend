using Common.Assistant;
using Common.Assistant.Anthropic;

namespace Web.Bff.Assistant;

/// <summary>The <c>Assistant</c> section: the provider, the core's limits and each customer's turn rate.</summary>
/// <remarks>
/// No key here holds a credential, and <see cref="AssistantRuntime"/> refuses one named like it (AI assistant design
/// §9). The core owns each limit's default; a key only moves it.
/// </remarks>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public const string Off = "off";

    public const string Anthropic = "anthropic";

    private static readonly AssistantLimits Defaults = new();

    /// <summary><c>off</c> or <c>anthropic</c>; unset is off, which builds no provider (design §9.1).</summary>
    public string? Provider { get; set; }

    /// <summary>The model the Anthropic provider asks; the adapter's constant, which loads nothing of it.</summary>
    public string Model { get; set; } = AnthropicSettings.DefaultModel;

    public string Effort { get; set; } = AnthropicSettings.DefaultEffort;

    /// <summary>The conversations and turns one customer may start a minute: this host's limit.</summary>
    public int TurnsPerMinute { get; set; } = 10;

    public int MaxSteps { get; set; } = Defaults.MaxSteps;

    public int MaxOutputTokens { get; set; } = Defaults.MaxOutputTokens;

    public long ConversationTokenBudget { get; set; } = Defaults.ConversationTokenBudget;

    public int MaxConversations { get; set; } = Defaults.MaxConversations;

    public int MaxCallers { get; set; } = Defaults.MaxCallers;

    /// <summary>How long an untouched conversation stays in this replica's memory (docs/personal-data.md).</summary>
    public TimeSpan IdleTimeout { get; set; } = Defaults.IdleTimeout;

    /// <summary>The provider in effect, or an exception naming the key when the value is not one.</summary>
    public string ProviderInEffect()
    {
        string? configured = Provider?.Trim();

        return configured?.ToLowerInvariant() switch
        {
            null or "" or Off => Off,
            Anthropic => Anthropic,
            _ => throw new InvalidOperationException(
                $"{SectionName}:Provider is '{configured}'; it takes '{Off}' or '{Anthropic}'.")
        };
    }

    /// <summary>The core's limits, or an exception naming the first key that is not positive.</summary>
    public AssistantLimits Limits()
    {
        Positive(TurnsPerMinute, nameof(TurnsPerMinute));
        Positive(MaxSteps, nameof(MaxSteps));
        Positive(MaxOutputTokens, nameof(MaxOutputTokens));
        Positive(ConversationTokenBudget, nameof(ConversationTokenBudget));
        Positive(MaxConversations, nameof(MaxConversations));
        Positive(MaxCallers, nameof(MaxCallers));
        Positive(IdleTimeout.Ticks, nameof(IdleTimeout));

        return new AssistantLimits
        {
            MaxSteps = MaxSteps,
            MaxOutputTokens = MaxOutputTokens,
            ConversationTokenBudget = ConversationTokenBudget,
            MaxConversations = MaxConversations,
            MaxCallers = MaxCallers,
            IdleTimeout = IdleTimeout
        };
    }

    private static void Positive(long value, string key)
    {
        if (value <= 0)
            throw new InvalidOperationException($"{SectionName}:{key} must be positive.");
    }
}

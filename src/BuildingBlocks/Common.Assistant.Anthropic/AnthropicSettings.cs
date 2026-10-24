namespace Common.Assistant.Anthropic;

/// <summary>
/// What the adapter asks of the model (AI assistant design §8.2): the model's id and the effort level, both the
/// operator's to change in the host's configuration. The client is built by the host, never here.
/// </summary>
public sealed record AnthropicSettings(string Model, string Effort)
{
    public const string DefaultModel = "claude-opus-5-5";
    public const string DefaultEffort = "low";

    /// <summary>
    /// The effort levels the pinned SDK takes (the API also has <c>xhigh</c>, which the SDK's enum lacks); a value
    /// outside them is refused at startup, naming the key.
    /// </summary>
    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "max"];

    public static AnthropicSettings Default { get; } = new(DefaultModel, DefaultEffort);

    /// <summary>
    /// These settings with the effort in the case the API takes, or an exception that names the configuration key to
    /// fix: a host calls this at startup, so a wrong value stops the host there and not at the first question.
    /// </summary>
    public AnthropicSettings Validated(string section)
    {
        if (string.IsNullOrWhiteSpace(Model))
        {
            throw new InvalidOperationException(
                $"{section}:Model is empty; it names the model the assistant asks (for example {DefaultModel}).");
        }

        string effort = Effort.Trim().ToLowerInvariant();

        if (!Efforts.Contains(effort))
        {
            throw new InvalidOperationException(
                $"{section}:Effort is '{Effort}'; it takes {string.Join(", ", Efforts)}.");
        }

        return new AnthropicSettings(Model.Trim(), effort);
    }
}

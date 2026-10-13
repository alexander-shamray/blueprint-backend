namespace Common.Infrastructure.Switching;

/// <summary>The names an operator has switched off, read from configuration that reloads (ADR-112).</summary>
public sealed class OperatorSwitchOptions
{
    public const string SectionName = "Switches";

    /// <summary>Receive-endpoint queue names and worker names; a name listed here is off.</summary>
    public List<string> Off { get; set; } = [];
}

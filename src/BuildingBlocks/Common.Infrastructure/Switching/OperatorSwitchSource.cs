using Microsoft.Extensions.Configuration;

namespace Common.Infrastructure.Switching;

/// <summary>The raw <c>Switches</c> section the host was given, which the bound options cannot describe.</summary>
/// <remarks>A list binds from indexed child keys only, so a scalar at <c>Switches:Off</c> binds as no list at all.
/// Nothing is then off, and nothing throws or logs, while the operator believes a queue is stopped (ADR-113).</remarks>
internal sealed class OperatorSwitchSource(IConfigurationSection section)
{
    /// <summary>Why <c>Off</c> cannot be read, or null when it holds no scalar; an empty value names nothing.</summary>
    public string? Scalar()
    {
        string? value = section.GetSection(nameof(OperatorSwitchOptions.Off)).Value;

        return string.IsNullOrEmpty(value)
            ? null
            : $"{OperatorSwitchOptions.SectionName}:Off holds the single value '{value}', which binds as an empty " +
              $"list and switches nothing off. Write each name as an indexed key, {OperatorSwitchOptions.SectionName}" +
              $":Off:0 and so on (Switches__Off__0 in the environment).";
    }
}

using System.Diagnostics.CodeAnalysis;
using Common.Domain;

namespace Common.Application;

/// <summary>Whether a string a third party supplied may be recorded: bounded, and printable (ADR-084).</summary>
/// <remarks>Here, where both adapters and Notifications' <c>InboundValues.Text</c> reach it; the rule itself is
/// <see cref="VisibleText"/>'s (ADR-216).</remarks>
public static class ThirdPartyText
{
    public static bool Recordable([NotNullWhen(true)] string? value, int maxLength) =>
        VisibleText.Holds(value, maxLength);
}

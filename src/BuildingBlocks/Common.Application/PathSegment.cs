using System.Diagnostics.CodeAnalysis;

namespace Common.Application;

/// <summary>Whether a value, once escaped, stays one segment of the path it is spliced into.</summary>
/// <remarks>
/// <c>Uri.EscapeDataString</c> leaves <c>.</c> and <c>..</c> as they are, and the URI then resolves them away, so the
/// call reaches another route; an empty value leaves no segment at all (ADR-212).
/// </remarks>
public static class PathSegment
{
    public static bool Holds([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && value is not ("." or "..");
}

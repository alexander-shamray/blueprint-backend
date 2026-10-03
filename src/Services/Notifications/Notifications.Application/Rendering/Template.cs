namespace Notifications.Application.Rendering;

/// <summary>One parsed template: a subject naming no placeholder, and a body naming only its key's.</summary>
/// <remarks>The body is split once, at start, so rendering substitutes and never parses (ADR-053 rule 1).</remarks>
public sealed record Template(
    string Key,
    int Version,
    string Language,
    string Subject,
    IReadOnlyList<TemplatePart> Body);

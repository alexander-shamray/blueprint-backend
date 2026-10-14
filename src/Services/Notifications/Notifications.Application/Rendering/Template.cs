using Notifications.Application.Preferences;

namespace Notifications.Application.Rendering;

/// <summary>One parsed template: a subject naming no placeholder, and a body naming only its key's.</summary>
/// <remarks>
/// The body is split once, at start, so rendering substitutes and never parses (ADR-053 rule 1). A push template's
/// subject is the push's title.
/// </remarks>
public sealed record Template(
    string Key,
    int Version,
    string Language,
    string Subject,
    IReadOnlyList<TemplatePart> Body,
    string Channel = ConsentVocabulary.Email);

namespace Notifications.Application.Rendering;

/// <summary>A run of a template's body: literal text, or the name of the placeholder that stands there.</summary>
public sealed record TemplatePart(string Text, bool IsPlaceholder);

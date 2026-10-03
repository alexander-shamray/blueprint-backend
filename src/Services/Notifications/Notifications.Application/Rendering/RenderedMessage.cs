namespace Notifications.Application.Rendering;

/// <summary>A rendered notice, with the version and languages its row stamps before the send (ADR-053).</summary>
public sealed record RenderedMessage(string Subject, string Body, int TemplateVersion, IReadOnlyList<string> Languages)
{
    /// <summary>The languages as the row's <c>Languages</c> column holds them, in rendering order.</summary>
    public string LanguageList => string.Join(',', Languages);
}

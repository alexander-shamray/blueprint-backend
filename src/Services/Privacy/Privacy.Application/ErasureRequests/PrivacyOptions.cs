namespace Privacy.Application.ErasureRequests;

/// <summary>
/// What a request is raised with (ADR-092): the holders that must answer, and how long they have. Nothing here is a
/// constant, because the periods are the adopter's reading of the law (ADR-053).
/// </summary>
public sealed class PrivacyOptions
{
    public const string SectionName = "Privacy";

    /// <summary>
    /// The holders expected to answer, read once when a request is raised and stored on it, so a later change moves
    /// no request already open.
    /// </summary>
    public IReadOnlyList<string> Responders { get; set; } = [];

    /// <summary>How long a request may stay open before it is overdue.</summary>
    public TimeSpan CompletionSlo { get; set; }
}

namespace Notifications.Application.Contacts;

/// <summary>A stored contact and the instant it was fetched, which ADR-052's freshness is read against.</summary>
public sealed record ContactRecord(string Email, string? Locale, DateTimeOffset FetchedAt)
{
    public override string ToString() => nameof(ContactRecord);
}

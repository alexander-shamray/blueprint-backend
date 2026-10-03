namespace Notifications.Application.Delivery;

/// <summary>A stored contact against <c>ContactOptions</c>, which decides whether the owner is asked.</summary>
public enum ContactAge
{
    /// <summary>No row: the owner is asked, and nothing is served if it cannot answer.</summary>
    Absent,

    /// <summary>Younger than the freshness: served, and the owner is not asked (ADR-052).</summary>
    Fresh,

    /// <summary>Inside the ceiling: the owner is asked, and the row is served only while it cannot answer.</summary>
    Stale,

    /// <summary>At or past the ceiling: the owner is asked, and the row is never served.</summary>
    Expired,
}

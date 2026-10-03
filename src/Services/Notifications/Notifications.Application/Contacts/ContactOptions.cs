namespace Notifications.Application.Contacts;

/// <summary>ADR-052's two freshness numbers for a contact row, refused at construction rather than clamped.</summary>
/// <remarks>
/// A registered value in <c>RetentionPolicy</c>'s refusing shape, not a bound section: ADR-052 states both numbers,
/// and a value no environment varies earns no options type (§15.4).
/// </remarks>
public sealed record ContactOptions
{
    /// <summary>Ten years, past which a window is a configuration error, as <c>RetentionPolicy</c>'s are.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(3650);

    /// <summary>ADR-052's numbers: fifteen minutes fresh, a day served stale while the owner cannot answer.</summary>
    public ContactOptions()
        : this(TimeSpan.FromMinutes(15), TimeSpan.FromHours(24))
    {
    }

    public ContactOptions(TimeSpan freshness, TimeSpan staleCeiling)
    {
        Freshness = InRange(freshness, nameof(Freshness));
        StaleCeiling = InRange(staleCeiling, nameof(StaleCeiling));

        if (StaleCeiling < Freshness)
        {
            throw Refused(
                nameof(StaleCeiling),
                staleCeiling,
                $"{nameof(StaleCeiling)} must be at least {nameof(Freshness)}, which is {Freshness}. Below it a " +
                "row would be fresh enough to serve without asking the owner and too stale to serve when the " +
                "owner cannot answer, which is a setting that cannot do what it says (ADR-052).");
        }
    }

    /// <summary>A row younger than this is served with no call to the owner.</summary>
    public TimeSpan Freshness { get; }

    /// <summary>The oldest row served while the owner cannot answer, a security number as well (ADR-052).</summary>
    public TimeSpan StaleCeiling { get; }

    private static TimeSpan InRange(TimeSpan value, string member) =>
        value > TimeSpan.Zero && value <= MaxAge
            ? value
            : throw Refused(
                member,
                value,
                $"{member} must be positive and at most {MaxAge}. A contact window outside that range does " +
                "not fail where it is set — it asks the owner on every read, or never asks it at all.");

    // Names the property, as RetentionPolicy's validators do, which a constructor argument name would not (CA2208).
    private static ArgumentOutOfRangeException Refused(string member, TimeSpan value, string message) =>
        new(member, value, message);
}

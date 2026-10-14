namespace Notifications.Application.Devices;

/// <summary>ADR-117's three numbers for the device rows, refused at construction rather than clamped.</summary>
/// <remarks>
/// A registered value in <c>ContactOptions</c>' refusing shape, not a bound section: ADR-117 states all three, and a
/// value no environment varies earns no options type (§15.4).
/// </remarks>
public sealed record DeviceTokenOptions
{
    /// <summary>The 270 days after which FCM expires an inactive token itself; a longer window keeps dead rows.</summary>
    private static readonly TimeSpan MaximumStaleAfter = TimeSpan.FromDays(270);

    /// <summary>ADR-117's numbers: thirty days unseen, ten rows a subject, a step of a thousand.</summary>
    public DeviceTokenOptions()
        : this(TimeSpan.FromDays(30), 10, 1000)
    {
    }

    public DeviceTokenOptions(TimeSpan staleAfter, int maxPerSubject, long maxSequenceStep)
    {
        StaleAfter = staleAfter > TimeSpan.Zero && staleAfter <= MaximumStaleAfter
            ? staleAfter
            : throw Refused(
                nameof(StaleAfter),
                staleAfter,
                $"{nameof(StaleAfter)} must be positive and at most {MaximumStaleAfter} (ADR-117).");

        MaxPerSubject = maxPerSubject > 0
            ? maxPerSubject
            : throw Refused(nameof(MaxPerSubject), maxPerSubject, "A subject keeps one row at least.");

        MaxSequenceStep = maxSequenceStep > 0
            ? maxSequenceStep
            : throw Refused(
                nameof(MaxSequenceStep),
                maxSequenceStep,
                "A step of zero would refuse every request above a row's own sequence.");
    }

    /// <summary>How long a row may go unseen before the retention pass deletes it; FCM's measure of stale.</summary>
    public TimeSpan StaleAfter { get; }

    /// <summary>The rows a subject may hold; a registration past it clears the one seen least recently.</summary>
    public int MaxPerSubject { get; }

    /// <summary>How far above a row's sequence a request may be, so none puts a row out of an app's reach.</summary>
    public long MaxSequenceStep { get; }

    // Names the property, as ContactOptions does, which a constructor argument name would not (CA2208).
    private static ArgumentOutOfRangeException Refused(string member, object value, string message) =>
        new(member, value, message);
}

using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Devices;

/// <summary>ADR-117's numbers for the device rows, defaulted here and refused at start out of bounds.</summary>
/// <remarks>Not a jurisdiction's: none is statutory, so a deployment overrides them only for its own reasons.</remarks>
public sealed class DeviceTokenOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Devices";

    /// <summary>The 270 days after which FCM expires an inactive token, past which a window keeps dead rows.</summary>
    public const string MaximumStaleAfter = "270.00:00:00";

    /// <summary>How long a row may go unseen before the retention pass deletes it; FCM's measure of stale.</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Range(typeof(TimeSpan), "00:00:01", MaximumStaleAfter, ParseLimitsInInvariantCulture = true)]
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(30);

    /// <summary>The rows a subject may hold; a registration past it drops the one seen least recently.</summary>
    [Range(1, 100)]
    public int MaxPerSubject { get; init; } = 10;

    /// <summary>How far above a row's sequence a request may be, so none puts a row out of an app's reach.</summary>
    [Range(1L, long.MaxValue)]
    public long MaxSequenceStep { get; init; } = 1000;
}

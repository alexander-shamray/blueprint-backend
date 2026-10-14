using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Jurisdiction;

/// <summary>ADR-053's one options class here: the language set, the zone, four windows and duties.</summary>
/// <remarks>
/// Every member is a value the deployment is given and is refused at start rather than clamped (ADR-053 rule 1);
/// <see cref="NotificationsJurisdictionValidator"/> holds the rules no annotation can state.
/// </remarks>
public sealed class NotificationsJurisdictionOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Jurisdiction";

    /// <summary>One second rather than zero, which reads on a values file as "not configured".</summary>
    public const string MinimumWindow = "00:00:01";

    /// <summary>Ten years, past which a value is a typo a purge's subtraction could throw on.</summary>
    public const string MaximumWindow = "3650.00:00:00";

    /// <summary>The languages a customer is owed, in the order a message in all of them shows them.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>The IANA zone dates are rendered in, resolved at start so an unknown one fails the host.</summary>
    [Required]
    public string? TimeZone { get; init; }

    /// <summary>How long a terminal notice must be kept: statutory, as the row is the evidence (ADR-053).</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? LogRetention { get; init; }

    /// <summary>How long an unrefreshed contact row may be kept; never shorter than its stale ceiling.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? ContactRetention { get; init; }

    /// <summary>How long an order record may be kept once no pending notice names its order.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? OrderRetention { get; init; }

    /// <summary>The pairs the service must send, each <c>topic/channel</c>, which take no preference.</summary>
    /// <remarks>
    /// Absent reads as none, since configuration binds no key for an empty list; the charts refuse a values file
    /// that leaves it unstated (ADR-120). Both hosts read it, and <see cref="RequiredPairsRule"/> refuses each pair.
    /// </remarks>
    public IReadOnlyList<string>? Required { get; init; }

    /// <summary>How long a journal entry is kept once no longer current: statutory, as it is evidence.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? ConsentRetention { get; init; }
}

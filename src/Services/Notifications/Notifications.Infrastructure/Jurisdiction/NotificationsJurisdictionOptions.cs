using System.ComponentModel.DataAnnotations;

namespace Notifications.Infrastructure.Jurisdiction;

/// <summary>ADR-053's one options class for this service: the language set, the zone and three windows.</summary>
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

    /// <summary>Ten years, past which a value is a typo the purge's subtraction could throw on.</summary>
    public const string MaximumWindow = "3650.00:00:00";

    /// <summary>The languages a customer is owed, in the order a message in all of them shows them.</summary>
    [Required]
    [MinLength(1)]
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>The IANA zone dates are rendered in, resolved at start so an unknown one fails the host.</summary>
    [Required]
    public string? TimeZone { get; init; }

    /// <summary>A terminal notice's age at deletion: statutory, as the row is the evidence (ADR-053).</summary>
    // RangeAttribute's string limits go through TimeSpanConverter, which reads the current culture.
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? LogRetention { get; init; }

    /// <summary>A contact row not refreshed for this long is deleted; never shorter than its stale ceiling.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? ContactRetention { get; init; }

    /// <summary>An order record this old is deleted, once no pending notice names its order.</summary>
    [Required]
    [Range(typeof(TimeSpan), MinimumWindow, MaximumWindow, ParseLimitsInInvariantCulture = true)]
    public TimeSpan? OrderRetention { get; init; }
}

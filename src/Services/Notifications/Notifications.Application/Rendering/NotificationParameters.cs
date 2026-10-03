namespace Notifications.Application.Rendering;

/// <summary>The values a notification's placeholders take, which with its version reproduce what was sent.</summary>
/// <remarks>
/// ADR-053 rule 4's evidence, so nothing here names a person. Every text member another service wrote arrives
/// checked or absent (<c>InboundValues</c>); absent renders as a mark, and an absent reason as its map's generic phrase.
/// </remarks>
public sealed record NotificationParameters
{
    public required Guid OrderId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public decimal? Amount { get; init; }

    /// <summary>An ISO 4217 code as published, three capital letters.</summary>
    public string? Currency { get; init; }

    public string? TrackingNumber { get; init; }

    /// <summary>A <c>CancelReasons</c> code; a code the map does not know renders its generic phrase.</summary>
    public string? CancelReason { get; init; }
}

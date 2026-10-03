namespace Notifications.Infrastructure.Delivery;

/// <summary>One leased row: what the pass decides on, and the stamp a resend renders again.</summary>
/// <remarks>No mailbox is projected, since the row holds none (ADR-053 rule 4).</remarks>
public sealed record SendWork(
    Guid NotificationId,
    Guid EventId,
    Guid OrderId,
    Guid? CustomerId,
    string TemplateKey,
    string Parameters,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SendStartedAt,
    int? TemplateVersion,
    string? Languages);

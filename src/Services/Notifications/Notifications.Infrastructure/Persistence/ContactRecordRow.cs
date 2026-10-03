namespace Notifications.Infrastructure.Persistence;

/// <summary>Mapped only so <c>migrations add</c> emits <see cref="SqlContactStore"/>'s table.</summary>
internal sealed class ContactRecordRow
{
    public Guid CustomerId { get; set; }
    public string Email { get; set; } = "";
    public string? Locale { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

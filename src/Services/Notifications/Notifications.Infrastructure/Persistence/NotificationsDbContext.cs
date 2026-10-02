using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Notifications.Application.Records;

namespace Notifications.Infrastructure.Persistence;

/// <summary>
/// Notifications's write-side context (§7.2), public because the architecture gates, not the access modifier,
/// keep it inside Infrastructure.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    /// <summary>§3.2's record of every notice owed, and the one table here that is the service's own.</summary>
    public DbSet<Notification> NotificationLog => Set<Notification>();

    /// <summary>§9.5's inbox, declared so this context states its whole model.</summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>§8.5's durable idempotency markers, declared on the same terms.</summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("notifications");

        // §7.2 puts mapping in these classes, never in attributes on domain types.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
    }

    /// <summary>§7.2's global conventions, which govern every row this context maps.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}

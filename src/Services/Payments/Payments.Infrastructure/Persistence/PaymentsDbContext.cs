using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Payments.Domain.Intents;
using Payments.Domain.Refunds;

namespace Payments.Infrastructure.Persistence;

/// <summary>
/// Payments's write-side context (§7.2). Sealed, and an implementation
/// detail of this assembly — §6.3 rejects an <c>IApplicationDbContext</c>
/// exposing <c>DbSet&lt;T&gt;</c>, which puts EF Core in an Application
/// signature while appearing to respect the boundary. Public rather than
/// internal, because the rule is that the context never leaves
/// Infrastructure by reference — enforced by the architecture gates, not by
/// the access modifier.
/// </summary>
public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    /// <summary>
    /// §9.4's outbox, and the first <c>DbSet</c> here that is not an aggregate
    /// root — §9.5's inbox and §8.5's marker followed it, each for a version of
    /// the same reason: the row has to be written by the same context as
    /// the aggregate to enlist in the same transaction, which is the entire
    /// mechanism. §12.4's tests read it through this property.
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// §9.5's inbox. Declared for the same reason as the outbox above and read
    /// by nothing in production: <c>InboxFilter&lt;T&gt;</c> is common code and
    /// reaches the entity through <c>Set&lt;InboxMessage&gt;()</c>, which is
    /// what lets one filter serve every service. The property is here so this
    /// context states its whole model, and so §12.4's tests can read the table
    /// the way they read the other two.
    /// </summary>
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    /// <summary>
    /// §8.5's durable idempotency markers. Declared on the same terms as the
    /// two above and read by nothing in production: <c>EfIdempotencyMarkerStore</c>
    /// is common code and reaches the entity through
    /// <c>Set&lt;IdempotencyMarker&gt;()</c>, which is what lets one store serve
    /// every service. The property is here so this context states its whole
    /// model, and so §12.4's tests can read the table the way they read the
    /// other two.
    /// </summary>
    public DbSet<IdempotencyMarker> IdempotencyMarkers => Set<IdempotencyMarker>();

    /// <summary>§3.2's aggregate, and the first <c>DbSet</c> here that is one (spec, section 7).</summary>
    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();

    /// <summary>§3.2's second aggregate: the money a cancellation voided back (spec, section 7).</summary>
    public DbSet<Refund> Refunds => Set<Refund>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("payments");

        // Assembly scanning, so that adding an entity costs an
        // IEntityTypeConfiguration<T> and nothing in this file. §7.2 puts
        // mapping in these classes and never in attributes on domain types,
        // which would put EF Core in Payments.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PaymentsDbContext).Assembly);
    }

    /// <summary>
    /// §7.2's global conventions. They landed while the model had no properties
    /// at all, and that timing was the argument: an unbounded
    /// <c>NVARCHAR(MAX)</c> is cheap to prevent and expensive to migrate, and a
    /// convention introduced after the first entity silently changes a column
    /// that already exists. They now govern every row this context maps —
    /// named rather than listed, because an inventory here is a second copy of
    /// the <c>DbSet</c>s above and it was already false of three technical
    /// tables before §8.5's marker made it four.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}

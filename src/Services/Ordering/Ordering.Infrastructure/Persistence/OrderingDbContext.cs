using Common.Infrastructure.Idempotency;
using Common.Infrastructure.Inbox;
using Common.Infrastructure.Outbox;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Ordering.Application;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// Ordering's write-side context (§7.2). Sealed, and an implementation detail of
/// this assembly — §6.3 rejects an <c>IApplicationDbContext</c> exposing
/// <c>DbSet&lt;T&gt;</c>, because that puts EF Core types in an Application
/// signature while appearing to respect the boundary.
/// </summary>
/// <remarks>
/// Public rather than internal, and the distinction is worth stating: §6.3's
/// rule is that the context never <em>leaves</em> Infrastructure, which is a
/// rule about references and is enforced by the architecture gates, not by the
/// access modifier. Three callers construct or resolve it by name — the
/// <c>dotnet ef</c> tooling, the migrator host (§7.4) and the Testcontainers
/// fixture (§12.4) — and none of them is Application, which could not name it
/// anyway without the EF Core dependency its gate forbids.
/// </remarks>
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    /// <summary>
    /// The aggregate root (§5.4). One <c>DbSet</c> per root and no set for
    /// <c>OrderLine</c>, which is owned — reaching a line without its order is
    /// exactly the aggregate-boundary breach the model exists to prevent.
    /// </summary>
    public DbSet<Order> Orders => Set<Order>();

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("ordering");

        // Here before its first find, so that the first entity adds an
        // IEntityTypeConfiguration<T> and not also the line that discovers
        // it. §7.2 puts mapping in these classes and never in attributes on
        // domain types, which would put EF Core in Ordering.Domain.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderingDbContext).Assembly);

        // §9.6's transactional outbox (ADR-032), and the one part of this
        // model no IEntityTypeConfiguration<T> above describes.
        //
        // NOT because the scan cannot reach one. ApplyConfigurationsFromAssembly
        // looks for types IMPLEMENTING IEntityTypeConfiguration<> in this
        // assembly and says nothing about where the entity type T is declared,
        // so an IEntityTypeConfiguration<InboxState> written here WOULD be
        // discovered and applied. The reason is ownership: MassTransit's own
        // queries read these three tables, so a configuration of ours would be
        // a second definition of a schema the library has to agree with — and
        // the next version bump moves the library's half with nothing to catch
        // the drift. InboxState, OutboxState and OutboxMessage — singular,
        // where this repository's own tables are plural, so the two sets share
        // the ordering schema without colliding.
        //
        // The callback overloads are deliberately not used. The entity type is
        // MassTransit.EntityFrameworkCoreIntegration.OutboxMessage and this
        // file already imports Common.Infrastructure.Outbox.OutboxMessage, so
        // naming it would be CS0104; the parameterless form needs only the
        // MassTransit namespace the extension methods live in.
        //
        // ConfigureConventions' 400-character string default does not truncate
        // any of these, and the mechanism is worth naming because it is not the
        // one it looks like. MassTransit does not override the convention with
        // an explicit length on every column; its configurator CLEARS the
        // convention first — an internal OptOutOfEntityFrameworkConventions
        // that walks the entity's properties setting max length back to null —
        // and only then sets 256 on the addresses and the content type. The
        // body, headers, properties and message type end up nvarchar(max)
        // because nothing constrains them, not because something chose it.
        //
        // The outcome is right and the guarantee is thinner than "explicit
        // lengths" implies: it rests on an internal helper of the library
        // continuing to run. A test asserts the resulting column types against
        // the built model, because this is precisely the kind of thing that
        // stops being true on a version bump with nothing going red.
        modelBuilder.AddTransactionalOutboxEntities();
    }

    /// <summary>
    /// §7.2's global conventions. They landed with the scaffold, over a model
    /// that then had no properties at all, and that was the argument for
    /// landing them early: an unbounded <c>NVARCHAR(MAX)</c> is cheap to
    /// prevent and expensive to migrate, and a convention introduced after the
    /// first entity silently changes a column that already exists. They now
    /// govern every row this context maps, business and technical alike —
    /// which is the outcome the timing bought, not a change of purpose. Named
    /// rather than listed since §8.5's marker joined them: an inventory here
    /// is a second copy of the <c>DbSet</c>s above.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(OrderAmounts.Precision, OrderAmounts.Scale);
        configurationBuilder.Properties<string>().HaveMaxLength(400);
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("datetimeoffset(7)");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §6.4's local price projection — Catalog's prices, as Ordering last saw
/// them. Not an aggregate and not part of the domain model: a read model,
/// mapped here only so that <c>migrations add</c> emits its table.
/// </summary>
/// <remarks>
/// <b>Mapped by EF, read by Dapper, written by Dapper.</b>
/// <see cref="ProjectedPriceReader"/> queries it (§6.5) and
/// <c>ProductPriceProjection</c> maintains it (§6.6) — EF touches neither
/// path. Configuring it through EF is what makes the schema the migration's
/// business rather than a hand-written <c>CREATE TABLE</c> that would drift
/// from the columns the reader selects.
/// <para>
/// There is no <c>DbSet</c> for it on the context, deliberately: nothing in
/// this service loads a price through EF, and a set would invite a write path
/// that bypasses the projection's idempotent <c>MERGE</c> (§6.6).
/// <c>ApplyConfigurationsFromAssembly</c> finds this class regardless.
/// </para>
/// <para>
/// <c>UpdatedAt</c> is the out-of-order guard — a projection applying an older
/// event after a newer one must be able to tell, and since PR-20 both of
/// §6.6's statements name this column in a predicate. <b>The name is §6.6's,
/// not this file's choice</b>: it shipped as <c>LastSeenAt</c> and PR-20 was
/// specified to copy §6.6's <c>MERGE</c> verbatim, which would have failed on
/// a column that is not there. Naming a table the next PR writes is exactly
/// where a private preference costs somebody else a debugging session. <c>IsAvailable</c> is
/// the reader's filter: a product Catalog unpublishes stops being orderable
/// without its price row being deleted, so the history of what it cost
/// survives.
/// </para>
/// </remarks>
internal sealed class ProductPriceConfiguration : IEntityTypeConfiguration<ProductPrice>
{
    public void Configure(EntityTypeBuilder<ProductPrice> builder)
    {
        builder.ToTable("ProductPrices", "ordering");

        // Composite: one row per product per currency, which is exactly what
        // the reader's WHERE clause seeks on.
        builder.HasKey(p => new { p.ProductId, p.Currency });

        // char(3) and a default, matching §6.6's printed DDL column for
        // column. This is a read model, and §7.4 files those under
        // hand-written DDL precisely because they are shaped for queries
        // rather than for objects — so where the chapter prints a type, the
        // configuration emits that type rather than EF's default for the CLR
        // one. IsFixedLength plus IsUnicode(false) is what turns nvarchar(3)
        // into char(3); a currency code is three ASCII letters by
        // construction (Money.Of enforces it), so neither the Unicode pages
        // nor the variable-length header buys anything.
        builder
            .Property(p => p.Currency)
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(p => p.Amount).HasPrecision(OrderAmounts.Precision, OrderAmounts.Scale);

        // The default is §6.6's, and it is what lets that chapter's MERGE
        // omit the column on the insert branch if it ever wants to.
        builder.Property(p => p.IsAvailable).HasDefaultValue(true);
    }
}

/// <summary>
/// A row of <see cref="ProductPriceConfiguration"/>'s table. Deliberately not
/// a domain type — it lives in Infrastructure because it is a read model, and
/// putting it in <c>Ordering.Domain</c> would make Catalog's pricing part of
/// this service's model rather than a cache of another service's.
/// </summary>
internal sealed class ProductPrice
{
    public Guid ProductId { get; set; }
    public string Currency { get; set; } = null!;
    public decimal Amount { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

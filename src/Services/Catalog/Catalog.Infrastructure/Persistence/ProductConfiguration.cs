using Catalog.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>
/// §7.2's pattern: configuration in a class, never in attributes on the domain
/// type — which would put EF Core in <c>Catalog.Domain</c>, past the gate.
/// Found by <c>CatalogDbContext</c>'s
/// <c>ApplyConfigurationsFromAssembly</c> call.
/// </summary>
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", "catalog");
        builder.HasKey(p => p.Id);

        builder
            .Property(p => p.Id)
            .HasConversion(id => id.Value, value => new ProductId(value))
            .ValueGeneratedNever();

        builder
            .Property(p => p.Name)
            .HasMaxLength(200);

        // ThumbnailUrl takes the 400 default from §7.2's string convention.

        // Value object mapped as a complex type — columns on the same table,
        // no identity, exactly matching the domain semantics (§7.2).
        builder.ComplexProperty(
            p => p.Price,
            price =>
            {
                price.Property(m => m.Amount).HasColumnName("PriceAmount").HasPrecision(19, 4);
                price.Property(m => m.Currency).HasColumnName("PriceCurrency").HasMaxLength(3);
            });

        // Optimistic concurrency — SQL Server maintains this automatically.
        builder.Property(p => p.Version).IsRowVersion();

        // The exact seek §6.5's keyset predicate performs: newest first,
        // id-descending within one instant.
        builder.HasIndex(p => new { p.PublishedAt, p.Id });

        builder.Ignore(p => p.DomainEvents);
    }
}

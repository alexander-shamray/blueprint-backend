using Catalog.Application.Products.PublishProduct;
using Catalog.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Infrastructure.Persistence;

/// <summary>§7.2's pattern: configuration in a class, never in attributes on the domain type.</summary>
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
            .HasMaxLength(PublishProductValidator.MaxNameLength);

        // ThumbnailUrl takes the 400 default from §7.2's string convention.

        // A complex type: columns on the same table and no identity, as a value object has none (§7.2).
        builder.ComplexProperty(
            p => p.Price,
            price =>
            {
                price.Property(m => m.Amount).HasColumnName("PriceAmount").HasPrecision(19, 4);
                price.Property(m => m.Currency).HasColumnName("PriceCurrency").HasMaxLength(3);
            });

        // Optimistic concurrency — SQL Server maintains this automatically.
        builder.Property(p => p.Version).IsRowVersion();

        // The exact seek §6.5's keyset predicate performs, once per ordering ADR-073 admits.
        builder.HasIndex(p => new { p.PublishedAt, p.Id });
        builder.HasIndex(p => new { p.Name, p.Id });

        builder.Ignore(p => p.DomainEvents);
    }
}

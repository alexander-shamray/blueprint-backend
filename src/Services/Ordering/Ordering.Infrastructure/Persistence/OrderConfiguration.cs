using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§7.2's pattern: configuration in a class, never attributes on the domain type.</summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders", "ordering");
        builder.HasKey(o => o.Id);

        builder
            .Property(o => o.Id)
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        builder
            .Property(o => o.CustomerId)
            .HasConversion(id => id.Value, value => new CustomerId(value));

        // §11.4's ownership check and §6.5's history query both filter on it by equality (§7.2).
        builder.HasIndex(o => o.CustomerId);

        // By name, never by number, or inserting a member reinterprets every row (§7.2).
        builder
            .Property(o => o.Status)
            .HasConversion<string>()
            .HasMaxLength(20);

        // A private field, so EF has to be told it exists; every line is validated against it (§7.2).
        builder
            .Property<string>("_currency")
            .HasColumnName("Currency")
            .HasMaxLength(3);

        // Request ids, which erasure empties with the customer's id. A JSON column rather than a table: nothing
        // queries by them, and the order's events carry them unchanged (ADR-099).
        builder
            .Property(o => o.ErasedUnder)
            .HasConversion(
                ids => JsonSerializer.Serialize(ids, (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null)!,
                new ValueComparer<IReadOnlyList<Guid>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (h, id) => HashCode.Combine(h, id)),
                    v => v.ToList()))
            .HasColumnType("nvarchar(max)")
            .HasDefaultValueSql("N'[]'");

        builder.ComplexProperty(
            o => o.ShippingAddress,
            address =>
            {
                address.Property(a => a.Line1).HasColumnName("ShipToLine1").HasMaxLength(200);
                address.Property(a => a.Line2).HasColumnName("ShipToLine2").HasMaxLength(200);
                address.Property(a => a.City).HasColumnName("ShipToCity").HasMaxLength(100);
                address.Property(a => a.PostalCode).HasColumnName("ShipToPostalCode").HasMaxLength(20);
                address.Property(a => a.Country).HasColumnName("ShipToCountry").HasMaxLength(2);
            });

        // Optimistic concurrency — SQL Server maintains this automatically.
        builder.Property(o => o.Version).IsRowVersion();

        // Computed from the lines, not stored.
        builder.Ignore(o => o.Total);
        builder.Ignore(o => o.DomainEvents);

        // A related entity, not an owned collection, so Money maps one way; the boundary is kept by reachability,
        // and IsRequired keeps the schema from admitting an orphan line (§7.2).
        builder
            .HasMany(o => o.Lines)
            .WithOne()
            .HasForeignKey("OrderId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .Navigation(o => o.Lines)
            .HasField("_lines")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

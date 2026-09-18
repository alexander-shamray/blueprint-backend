using System.Text.Json;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Inventory.Infrastructure.Persistence;

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("Reservations", "inventory");

        builder.HasKey(r => r.Id);
        builder
            .Property(r => r.Id)
            .HasColumnName("OrderId")
            .HasConversion(id => id.Value, value => new OrderId(value))
            .ValueGeneratedNever();

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.UpdatedAt).IsRequired();

        // The failed reserve's answer has to be repeatable (ADR-024), so the
        // ids it named are kept with the row. A JSON column rather than a
        // table: nothing queries by them.
        builder
            .Property<List<ProductId>>("_unavailable")
            .HasColumnName("UnavailableProductIds")
            .HasConversion(
                ids => JsonSerializer.Serialize(ids.Select(id => id.Value), (JsonSerializerOptions?)null),
                json => JsonSerializer.Deserialize<List<Guid>>(json, (JsonSerializerOptions?)null)!
                    .Select(value => new ProductId(value)).ToList())
            .HasColumnType("nvarchar(max)");

        builder.Property(r => r.Version).HasColumnName("RowVersion").IsRowVersion();
        builder.Ignore(r => r.DomainEvents);
        builder.Ignore(r => r.UnavailableProductIds);

        builder.OwnsMany(
            r => r.Lines,
            lines =>
            {
                lines.ToTable("ReservationLines", "inventory");
                lines.WithOwner().HasForeignKey("OrderId");
                lines
                    .Property(l => l.ProductId)
                    .HasConversion(id => id.Value, value => new ProductId(value));
                lines.HasKey("OrderId", nameof(ReservationLine.ProductId));
                lines.Property(l => l.Quantity).IsRequired();
            });
        builder.Navigation(r => r.Lines).HasField("_lines").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

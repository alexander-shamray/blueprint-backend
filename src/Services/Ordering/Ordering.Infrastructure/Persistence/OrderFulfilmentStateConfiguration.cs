using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;
using Ordering.Infrastructure.Messaging;

namespace Ordering.Infrastructure.Persistence;

/// <summary>§9.6's saga table, on this context so the instance lives in the service's own database.</summary>
/// <remarks>No <c>RowVersion</c>: the repository's pessimistic mode takes row locks instead (§9.6).</remarks>
internal sealed class OrderFulfilmentStateConfiguration : IEntityTypeConfiguration<OrderFulfilmentState>
{
    public void Configure(EntityTypeBuilder<OrderFulfilmentState> builder)
    {
        builder.ToTable("OrderFulfilmentStates", "ordering");

        builder.HasKey(s => s.CorrelationId);

        // The types §9.6's DDL prints, column for column; varchar, since every value is ASCII.
        builder
            .Property(s => s.CurrentState)
            .HasMaxLength(64)
            .IsUnicode(false)
            .IsRequired();

        builder
            .Property(s => s.Currency)
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false)
            .IsRequired();

        builder.Property(s => s.Total).HasPrecision(OrderAmounts.Precision, OrderAmounts.Scale);

        // A shadow property the machine cannot read, kept only for §7.4's expand/contract (ADR-028): §15.5's
        // canary runs a build that still writes it, and the empty-GUID default names nobody (§9.6).
        builder
            .Property<Guid>("CustomerId")
            .HasDefaultValue(Guid.Empty);

        // Nullable here and not on the instance: a saga that never compensates stores NULL.
        builder
            .Property(s => s.CancelReason)
            .HasMaxLength(32)
            .IsUnicode(false)
            .IsRequired(false);

        // Backs the "unfinalised saga" alert (§13.6), which would otherwise scan the table.
        builder
            .HasIndex(s => s.StartedAt)
            .IncludeProperties(s => s.CurrentState)
            .HasDatabaseName("IX_OrderFulfilmentStates_StartedAt");
    }
}

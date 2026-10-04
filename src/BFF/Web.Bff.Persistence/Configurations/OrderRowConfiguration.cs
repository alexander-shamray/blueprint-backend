using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Web.Bff.Persistence.Configurations;

/// <summary>The <c>Orders</c> table, configured in a class as §7.2 asks; found by the assembly scan.</summary>
internal sealed class OrderRowConfiguration : IEntityTypeConfiguration<OrderRow>
{
    public void Configure(EntityTypeBuilder<OrderRow> builder)
    {
        builder.ToTable("Orders", BffSchema.Name, table =>
        {
            // Each pair is written by one handler in one statement, so half of one is a defect to refuse.
            table.HasCheckConstraint("CK_Orders_Total", Together("Currency", "TotalAmount"));
            table.HasCheckConstraint("CK_Orders_Cancellation", Together("CancelledAt", "CancelOutcome"));
            table.HasCheckConstraint("CK_Orders_Authorisation", Together("AuthorisedAt", "AuthorisedAmount"));
            table.HasCheckConstraint("CK_Orders_Refund", Together("RefundedAt", "RefundedAmount"));

            // An amount is a number the client cannot render without its currency, and the currency is written by
            // the same statement as the first amount, so it is present exactly when either amount is (§10.7).
            table.HasCheckConstraint(
                "CK_Orders_PaymentCurrency",
                "([PaymentCurrency] IS NULL AND [AuthorisedAmount] IS NULL AND [RefundedAmount] IS NULL) OR " +
                "([PaymentCurrency] IS NOT NULL AND ([AuthorisedAmount] IS NOT NULL OR [RefundedAmount] IS NOT NULL))");

            // §10.7's closed vocabulary; a NULL passes, as a CHECK on an unknown does.
            table.HasCheckConstraint(
                "CK_Orders_CancelOutcome",
                $"[CancelOutcome] IN (N'{CancelOutcomes.Cancelled}', N'{CancelOutcomes.OutOfStock}', " +
                $"N'{CancelOutcomes.Declined}')");
        });

        // The order's own id, where every handler's MERGE meets: two first arrivals cannot both insert.
        builder.HasKey(o => o.OrderId);
        builder.Property(o => o.OrderId).ValueGeneratedNever();

        builder.Property(o => o.Currency).HasMaxLength(ProjectionLimits.CurrencyLength);
        builder.Property(o => o.PaymentCurrency).HasMaxLength(ProjectionLimits.CurrencyLength);
        builder.Property(o => o.CancelOutcome).HasMaxLength(ProjectionLimits.CancelOutcomeMaxLength);
        builder.Property(o => o.TrackingNumber).HasMaxLength(ProjectionLimits.TrackingNumberMaxLength);

        // The list's keyset seek (§10.7), filtered so it never reads a row with no owner.
        builder
            .HasIndex(o => new { o.CustomerId, o.FirstSeenAt, o.OrderId })
            .HasDatabaseName("IX_Orders_Owned")
            .IsDescending(false, true, true)
            .HasFilter("[CustomerId] IS NOT NULL");
    }

    private static string Together(string first, string second) =>
        $"([{first}] IS NULL AND [{second}] IS NULL) OR ([{first}] IS NOT NULL AND [{second}] IS NOT NULL)";
}

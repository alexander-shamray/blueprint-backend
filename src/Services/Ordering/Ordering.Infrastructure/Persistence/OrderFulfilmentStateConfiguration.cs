using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Application;
using Ordering.Infrastructure.Messaging;

namespace Ordering.Infrastructure.Persistence;

/// <summary>
/// §9.6's saga table. Mapped on this context deliberately: the saga repository
/// is configured with <c>ExistingDbContext&lt;OrderingDbContext&gt;()</c>, so
/// the instance lives in the service's own database and its migrations travel
/// with the service's.
/// </summary>
/// <remarks>
/// Not for atomicity — the saga's effects reach other services as messages and
/// never share a transaction with them (ADR-002, §9.7). The reasons are
/// operational: one database per service to back up, one migration history, one
/// connection pool, and the saga table sits next to the orders it coordinates
/// when someone is debugging at 03:00.
/// <para>
/// <b>No <c>RowVersion</c>.</b> The repository runs
/// <c>ConcurrencyMode.Pessimistic</c>, which takes row locks rather than
/// comparing a version column — carrying one anyway would imply an optimistic
/// strategy the saga does not use.
/// </para>
/// </remarks>
internal sealed class OrderFulfilmentStateConfiguration : IEntityTypeConfiguration<OrderFulfilmentState>
{
    public void Configure(EntityTypeBuilder<OrderFulfilmentState> builder)
    {
        builder.ToTable("OrderFulfilmentStates", "ordering");

        builder.HasKey(s => s.CorrelationId);

        // The types §9.6's DDL prints, column for column, for the reason
        // ProductPriceConfiguration gives: where the chapter states a type, the
        // configuration emits that type rather than EF's default for the CLR
        // one. varchar rather than nvarchar throughout — every value here is a
        // state name or a code from a closed ASCII vocabulary.
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

        // The expand half of §7.4's expand/contract, and the only reason this
        // column is still mapped at all (ADR-028, #63). The instance no longer
        // declares a CustomerId — that is the point of the change — but the
        // column cannot go in the same release, because §15.5 requires every
        // migration to be backward compatible with the release still serving
        // beside it: migrations run ahead of the deploy, and the old build's
        // saga writes this column on every OrderPlaced.
        //
        // A shadow property is what lets those two facts coexist. Nothing in
        // the machine can read or write it, so the subject cannot find its way
        // back onto a message through the instance; the column survives for
        // the old build, which still can.
        //
        // The default is what makes the direction safe, and it is the
        // conservative value rather than merely a legal one — the same
        // argument AddSagaPaymentVerdictJoin makes for its two columns, one
        // release on. The new build's INSERT does not name this column, so
        // SQL Server supplies the default; the old build materialises a
        // non-nullable Guid from rows the new build wrote, so the column must
        // not be nullable and must not be absent. It reads Guid.Empty, which
        // is nobody — where a nullable column would throw on materialisation
        // and a dropped one would fail the INSERT outright.
        //
        // **That old build is not only a rollback, and framing it as one was
        // this comment's mistake.** §15.5's canary runs both releases at once
        // over the same queues, so the ordinary ladder produces it: a new pod
        // creates the instance with this column defaulted, an old pod picks up
        // the next event for that correlation, materialises Guid.Empty, and
        // sends its four-field AuthorisePayment naming nobody. Reachable on
        // every deploy, not only on the way back from one.
        //
        // **What makes it acceptable here is the same condition §9.2's
        // in-place exception rests on: nothing consumes that command.**
        // Payments is unbuilt, so the legacy message reaches no decision.
        // A platform with a live Payments needs THREE releases rather than
        // two — stop sending the field, then drop the property, then drop the
        // column — which is §7.4's sequence with its "stop writing the old
        // one" step performed rather than skipped. Skipping it is what this
        // release can afford and a live consumer could not.
        //
        // The contract half — DROP COLUMN — is a later release's, once no
        // build that writes it is still running.
        builder
            .Property<Guid>("CustomerId")
            .HasDefaultValue(Guid.Empty);

        // Nullable in the database and non-nullable on the instance, which is
        // the one place those two disagree on purpose: a saga that never
        // compensates stores NULL, and the state machine guarantees the
        // property is written before either stock exit from Compensating reads
        // it. Not "either exit": #124 gave that state three more transitions,
        // and the two that read this are the ones sending CancelOrder.
        builder
            .Property(s => s.CancelReason)
            .HasMaxLength(32)
            .IsUnicode(false)
            .IsRequired(false);

        // Backs the "unfinalised saga" alert (§13.6) and the stuck-saga
        // runbook. Without it that alert is a query with no index — the whole
        // table, scanned, on the schedule an alert runs at.
        builder
            .HasIndex(s => s.StartedAt)
            .IncludeProperties(s => s.CurrentState)
            .HasDatabaseName("IX_OrderFulfilmentStates_StartedAt");
    }
}

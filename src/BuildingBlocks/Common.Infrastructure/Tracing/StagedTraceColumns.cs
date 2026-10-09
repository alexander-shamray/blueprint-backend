using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Common.Infrastructure.Tracing;

/// <summary><see cref="StagedTrace"/> as two shadow columns, for a domain row that names no trace (§7.2).</summary>
/// <remarks>Nullable, so a row the serving version writes claims as before, in a trace of its own (§7.4).</remarks>
public static class StagedTraceColumns
{
    public const string ParentColumn = "TraceParent";

    public const string StateColumn = "TraceState";

    /// <summary>Maps both columns, ASCII by the W3C format.</summary>
    public static void Map<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        builder.Property<string?>(ParentColumn).HasMaxLength(StagedTrace.ParentMaxLength).IsUnicode(false);
        builder.Property<string?>(StateColumn).HasMaxLength(StagedTrace.StateMaxLength).IsUnicode(false);
    }

    /// <summary>Writes the current activity's context on a tracked row, in the row's own transaction.</summary>
    public static void Stamp(EntityEntry entry)
    {
        StagedTrace current = StagedTrace.Current;

        entry.Property(ParentColumn).CurrentValue = current.Parent;
        entry.Property(StateColumn).CurrentValue = current.State;
    }
}

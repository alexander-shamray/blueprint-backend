using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Api.Tests;

/// <summary>
/// The marker the retry test throws on its first attempt. A real transient
/// fault is a SqlException with one of a fixed set of numbers, and those are
/// not constructible; a strategy taught to retry this marker exercises the
/// same path without reflection over provider internals.
/// </summary>
public sealed class FakeTransientException : Exception;

/// <summary>
/// The production strategy plus one retriable exception type. Everything the
/// test proves — the delegate re-runs, the first attempt rolls back, one
/// commit — is the base class's behaviour, not this subclass's.
/// </summary>
public sealed class MarkerRetryingStrategy(ExecutionStrategyDependencies dependencies)
    : SqlServerRetryingExecutionStrategy(dependencies)
{
    protected override bool ShouldRetryOn(Exception exception) =>
        exception is FakeTransientException || base.ShouldRetryOn(exception);
}

/// <summary>
/// A tracked entity over the fixture's probe table, isolating the retry
/// mechanics under test from the service's production entities.
/// </summary>
public sealed class TrackedProbe
{
    public Guid Id { get; set; }

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Adds <see cref="TrackedProbe"/> to the model of the retry tests' own
/// <c>DbContextOptions</c> — and nothing else's. The production model and its
/// migration snapshot never see the entity: a test table mapped in
/// <c>InventoryDbContext</c> itself would make the next <c>migrations add</c>
/// generate DDL for a table only the fixture creates, leaving the migration
/// snapshot describing a schema the running service never has.
/// </summary>
public sealed class ProbeModelCustomizer(ModelCustomizerDependencies dependencies)
    : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.Entity<TrackedProbe>(probe =>
        {
            probe.ToTable("TransactionProbe", "inventory");
            probe.HasKey(p => p.Id);
            probe.Property(p => p.Id).ValueGeneratedNever();
            probe.Property(p => p.Note).HasMaxLength(100);
        });
    }
}

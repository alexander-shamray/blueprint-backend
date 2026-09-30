using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Payments.Api.Tests;

/// <summary>The retry test's first-attempt fault, since a transient <c>SqlException</c> is not constructible.</summary>
public sealed class FakeTransientException : Exception;

/// <summary>The production strategy plus one retriable exception type; everything proven is the base class's.</summary>
public sealed class MarkerRetryingStrategy(ExecutionStrategyDependencies dependencies)
    : SqlServerRetryingExecutionStrategy(dependencies)
{
    protected override bool ShouldRetryOn(Exception exception) =>
        exception is FakeTransientException || base.ShouldRetryOn(exception);
}

/// <summary>A tracked entity over the probe table, so the identity-map half of the retry is assertable.</summary>
public sealed class TrackedProbe
{
    public Guid Id { get; set; }

    public string Note { get; set; } = string.Empty;
}

/// <summary>Adds <see cref="TrackedProbe"/> to the retry tests' own model only, so no migration ever sees it.</summary>
public sealed class ProbeModelCustomizer(ModelCustomizerDependencies dependencies)
    : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        modelBuilder.Entity<TrackedProbe>(probe =>
        {
            probe.ToTable("TransactionProbe", "payments");
            probe.HasKey(p => p.Id);
            probe.Property(p => p.Id).ValueGeneratedNever();
            probe.Property(p => p.Note).HasMaxLength(100);
        });
    }
}

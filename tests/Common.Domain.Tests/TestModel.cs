namespace Common.Domain.Tests;

// Anonymous rather than §5's Ordering sample, so this project compiles whatever domain the platform settles on.

/// <summary>§5.2's typed-identifier form over a version-7 <see cref="Guid"/>.</summary>
internal readonly record struct TestId(Guid Value)
{
    public static TestId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>The argument §5.2 says must not compile where a <see cref="TestId"/> is expected.</summary>
internal readonly record struct OtherTestId(Guid Value)
{
    public static OtherTestId New() => new(Guid.CreateVersion7());
}

/// <summary>Carries a field outside identity, for §5.1's same-ID-same-thing test.</summary>
internal sealed class TestEntity : Entity<TestId>
{
    public TestEntity(TestId id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Label { get; }
}

/// <summary>Never equal to a <see cref="TestEntity"/> holding the same ID.</summary>
internal sealed class OtherTestEntity : Entity<TestId>
{
    public OtherTestEntity(TestId id) => Id = id;
}

/// <summary>Reaches the protected <c>Raise</c> through a named operation, as a real aggregate does.</summary>
internal sealed class TestAggregate : AggregateRoot<TestId>
{
    public TestAggregate(TestId id) => Id = id;

    public void RecordThat(IDomainEvent domainEvent) => Raise(domainEvent);
}

internal sealed record TestDomainEvent(string Name, DateTimeOffset OccurredAt) : IDomainEvent;

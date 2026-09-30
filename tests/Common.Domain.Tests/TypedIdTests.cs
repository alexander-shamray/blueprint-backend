using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

/// <summary>§5.2's pattern is a shape with no base type, so these tests run against <see cref="TestId"/>.</summary>
public class TypedIdTests
{
    [Fact]
    public void A_new_identifier_is_version_7()
    {
        var id = TestId.New();

        id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Every_new_identifier_is_distinct()
    {
        TestId[] ids =
        [
            .. Enumerable
                .Range(0, 1_000)
                .Select(_ => TestId.New())
        ];

        ids.Distinct().Count().ShouldBe(ids.Length);
    }

    [Fact]
    public void Identifiers_wrapping_the_same_value_are_equal()
    {
        var value = Guid.CreateVersion7();

        var one = new TestId(value);
        var other = new TestId(value);

        one.ShouldBe(other);
    }

    [Fact]
    public void An_identifier_prints_as_its_underlying_value()
    {
        var value = Guid.CreateVersion7();

        new TestId(value).ToString().ShouldBe(value.ToString());
    }

    [Fact]
    public void Two_identifier_types_over_the_same_value_are_not_interchangeable()
    {
        var value = Guid.CreateVersion7();

        var id = new TestId(value);
        var other = new OtherTestId(value);

        // The compiler refuses the assignment; this is the runtime half, against a future conversion operator.
        id.Equals(other).ShouldBeFalse();
    }
}

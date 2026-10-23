using Common.TestSupport;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>What <see cref="MemberWalk"/> distinguishes, and what it treats as the same.</summary>
public class MemberWalkTests
{
    private sealed record Amount(decimal Value, string Currency);

    private sealed record Holder(Amount Total, string? Note, IReadOnlyList<Amount> Lines);

    private sealed class Loop
    {
        public Loop Next => this;
    }

    private sealed class WithField
    {
        public int Kept = 1;
    }

    [Fact]
    public void A_rounded_decimal_differs_from_the_original()
    {
        MemberWalk.Of(new Amount(19.99m, "EUR")).ShouldNotBe(MemberWalk.Of(new Amount(20m, "EUR")));
    }

    [Fact]
    public void A_dropped_member_differs_from_the_original()
    {
        Holder sample = new(new Amount(1m, "EUR"), "Flat 4", []);

        MemberWalk.Of(sample).ShouldNotBe(MemberWalk.Of(sample with { Note = null }));
    }

    [Fact]
    public void Lists_of_different_types_with_the_same_elements_are_the_same()
    {
        Amount[] elements = [new Amount(1m, "EUR")];

        MemberWalk.Of(new Holder(elements[0], null, elements)).ShouldBe(
            MemberWalk.Of(new Holder(elements[0], null, [.. elements])));
    }

    [Fact]
    public void A_missing_element_differs_from_the_original()
    {
        Amount one = new(1m, "EUR");

        MemberWalk.Of(new Holder(one, null, [one, one])).ShouldNotBe(MemberWalk.Of(new Holder(one, null, [one])));
    }

    [Fact]
    public void A_timestamp_truncated_to_whole_seconds_differs_from_the_original()
    {
        DateTimeOffset moment = new(2026, 8, 16, 12, 0, 0, 123, TimeSpan.Zero);

        MemberWalk.Of(moment).ShouldNotBe(MemberWalk.Of(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero)));
        MemberWalk.Of(moment).ShouldBe(MemberWalk.Of(new DateTimeOffset(2026, 8, 16, 12, 0, 0, 123, TimeSpan.Zero)));
    }

    [Fact]
    public void A_value_that_reaches_itself_fails_naming_the_path_rather_than_overflowing_the_stack()
    {
        string message = Should.Throw<InvalidOperationException>(() => MemberWalk.Of(new Loop())).Message;

        message.ShouldContain("a cycle");
        message.ShouldContain("$.Next.Next");
    }

    [Fact]
    public void A_public_field_is_walked_as_well_as_a_property()
    {
        MemberWalk.Of(new WithField()).ShouldContain("$.Kept=1");
    }
}

using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

/// <summary>ADR-084's character rule, where an aggregate can read it (ADR-216).</summary>
public sealed class VisibleTextTests
{
    [Theory]
    [InlineData("shiping is a misspelling")]
    [InlineData("KZ-ӘҒҚ-0042")]
    public void A_bounded_printable_string_holds(string value)
    {
        VisibleText.Holds(value, 64).ShouldBeTrue();
    }

    [Theory]
    [InlineData("typo\r\nforged")]
    [InlineData("typo\u0000x")]
    [InlineData("typo\u202Ex")]
    [InlineData("typo\u2029x")]
    [InlineData("typo\u200Bx")]
    [InlineData("   ")]
    [InlineData("")]
    public void A_control_format_separator_or_blank_string_does_not(string value)
    {
        VisibleText.Holds(value, 64).ShouldBeFalse();
    }

    [Fact]
    public void A_null_string_does_not_hold()
    {
        VisibleText.Holds(null, 64).ShouldBeFalse();
    }

    [Fact]
    public void A_string_holds_to_its_bound_and_not_past_it()
    {
        VisibleText.Holds(new string('r', 10), 10).ShouldBeTrue();
        VisibleText.Holds(new string('r', 11), 10).ShouldBeFalse();
    }

    [Fact]
    public void A_lone_surrogate_is_refused_and_a_pair_is_one_character()
    {
        // Built here, as theory data would carry the lone half through serialisation as U+FFFD.
        string half = ((char)0xD800).ToString();

        VisibleText.Holds($"typo{half}x", 64).ShouldBeFalse();
        VisibleText.Holds("typo" + char.ConvertFromUtf32(0x1F4E6), 64).ShouldBeTrue();
    }
}

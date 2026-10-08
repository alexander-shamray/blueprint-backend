using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>ADR-084's second rule: a reference a customer reads links nowhere.</summary>
public sealed class PlainReferenceTests
{
    [Theory]
    [InlineData("1Z999AA10123456784")]
    [InlineData("TRK-SIM-OK")]
    [InlineData("TRK 12 34")]
    [InlineData("KZ_ӘҒҚ_0042")]
    public void A_tracking_number_of_letters_digits_and_single_joiners_is_well_formed(string value)
    {
        PlainReference.IsWellFormed(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://evil.example/track")]
    [InlineData("www.evil.example")]
    [InlineData("TRK@evil.example")]
    [InlineData("TRK:1")]
    [InlineData("TRK/1")]
    [InlineData("TRK  12")]
    [InlineData(" TRK1")]
    [InlineData("TRK1-")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData(null)]
    public void A_tracking_number_a_mail_client_could_link_or_that_ends_on_a_joiner_is_not(string? value)
    {
        PlainReference.IsWellFormed(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData("evil\uA4F8example")]
    [InlineData("TRK\u02D01")]
    [InlineData("TRK\uA4FD1")]
    public void A_modifier_letter_drawn_as_a_dot_or_a_colon_is_not(string value)
    {
        PlainReference.IsWellFormed(value).ShouldBeFalse();
    }

    [Fact]
    public void A_lone_surrogate_is_no_letter()
    {
        // Built here, as theory data would carry the lone half through serialisation as U+FFFD.
        PlainReference.IsWellFormed($"TRK{(char)0xD800}1").ShouldBeFalse();
    }
}

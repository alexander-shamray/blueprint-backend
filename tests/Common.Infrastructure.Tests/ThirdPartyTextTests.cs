using Common.Contracts.Shipping.V1;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>ADR-084's two rules: a third party's string is printable, and a tracking number links nowhere.</summary>
public sealed class ThirdPartyTextTests
{
    [Theory]
    [InlineData("psp_8f2a")]
    [InlineData("KZ-ӘҒҚ-0042")]
    [InlineData("crr 12 / 34")]
    public void A_bounded_printable_string_is_recordable(string value)
    {
        ThirdPartyText.Recordable(value, 64).ShouldBeTrue();
    }

    [Theory]
    [InlineData("psp_\r\nforged")]
    [InlineData("psp_\u0000x")]
    [InlineData("psp_\u202Ex")]
    [InlineData("psp_\u2028x")]
    [InlineData("psp_\u200Bx")]
    [InlineData("   ")]
    [InlineData("")]
    public void A_control_format_separator_or_broken_character_is_not(string value)
    {
        ThirdPartyText.Recordable(value, 64).ShouldBeFalse();
    }

    [Fact]
    public void A_string_is_recordable_to_its_bound_and_not_past_it()
    {
        ThirdPartyText.Recordable(new string('r', 10), 10).ShouldBeTrue();
        ThirdPartyText.Recordable(new string('r', 11), 10).ShouldBeFalse();
    }

    [Fact]
    public void A_lone_surrogate_is_a_broken_character_in_either_rule()
    {
        // Built here, as theory data would carry the lone half through serialisation as U+FFFD.
        string half = ((char)0xD800).ToString();

        ThirdPartyText.Recordable($"psp_{half}x", 64).ShouldBeFalse();
        TrackingNumbers.IsWellFormed($"TRK{half}1").ShouldBeFalse();
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_one_character_not_a_broken_one()
    {
        ThirdPartyText.Recordable("psp_" + char.ConvertFromUtf32(0x1F4E6), 64).ShouldBeTrue();
    }

    [Theory]
    [InlineData("1Z999AA10123456784")]
    [InlineData("TRK-SIM-OK")]
    [InlineData("TRK 12 34")]
    [InlineData("KZ_ӘҒҚ_0042")]
    public void A_tracking_number_of_letters_digits_and_single_joiners_is_well_formed(string value)
    {
        TrackingNumbers.IsWellFormed(value).ShouldBeTrue();
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
        TrackingNumbers.IsWellFormed(value).ShouldBeFalse();
    }
}

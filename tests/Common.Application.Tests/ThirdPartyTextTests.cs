using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>ADR-084's first rule: a string a third party supplies is recorded only when printable.</summary>
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
    public void A_lone_surrogate_is_a_broken_character()
    {
        // Built here, as theory data would carry the lone half through serialisation as U+FFFD.
        string half = ((char)0xD800).ToString();

        ThirdPartyText.Recordable($"psp_{half}x", 64).ShouldBeFalse();
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_one_character_not_a_broken_one()
    {
        ThirdPartyText.Recordable("psp_" + char.ConvertFromUtf32(0x1F4E6), 64).ShouldBeTrue();
    }
}

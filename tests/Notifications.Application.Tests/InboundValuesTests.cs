using Notifications.Application.Intake;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>Every value another service wrote is bounded and renders as itself, or is dropped.</summary>
public class InboundValuesTests
{
    [Theory]
    [InlineData("1Z999AA10123456784")]
    [InlineData("KZ-ӘҒҚ-0042")]
    [InlineData("TRK 12 34")]
    public void A_tracking_number_that_renders_as_itself_is_kept(string value)
    {
        InboundValues.Text(value, InboundValues.MaxTrackingNumberLength).ShouldBe(value);
    }

    /// <summary>Code points that would change what a customer reads without being seen.</summary>
    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x0009)]
    [InlineData(0x000A)]
    [InlineData(0x000D)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x00AD)]
    [InlineData(0x061C)]
    [InlineData(0x200B)]
    [InlineData(0x200D)]
    [InlineData(0x200E)]
    [InlineData(0x200F)]
    [InlineData(0x202A)]
    [InlineData(0x202E)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x2066)]
    [InlineData(0x2069)]
    [InlineData(0x2060)]
    [InlineData(0xFEFF)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    [InlineData(0xE0041)]
    public void A_tracking_number_holding_an_invisible_or_broken_character_is_dropped(int codePoint)
    {
        // ConvertFromUtf32 refuses a lone surrogate, which is the case the cast is kept for.
        string character = codePoint > 0xFFFF ? char.ConvertFromUtf32(codePoint) : ((char)codePoint).ToString();
        string value = $"1Z999{character}AA1";

        InboundValues.Text(value, InboundValues.MaxTrackingNumberLength).ShouldBeNull();
    }

    [Fact]
    public void A_character_outside_the_basic_plane_is_kept_whole()
    {
        // A surrogate pair is one character, so only a lone half is refused.
        string value = "TRK-" + char.ConvertFromUtf32(0x1F4E6);

        InboundValues.Text(value, InboundValues.MaxTrackingNumberLength).ShouldBe(value);
    }

    [Fact]
    public void A_tracking_number_is_kept_to_its_bound_and_dropped_past_it()
    {
        string atBound = new('9', InboundValues.MaxTrackingNumberLength);

        InboundValues.Text(atBound, InboundValues.MaxTrackingNumberLength).ShouldBe(atBound);
        InboundValues.Text(atBound + "9", InboundValues.MaxTrackingNumberLength).ShouldBeNull();
        InboundValues.Text("   ", InboundValues.MaxTrackingNumberLength).ShouldBeNull();
        InboundValues.Text(null, InboundValues.MaxTrackingNumberLength).ShouldBeNull();
    }

    [Theory]
    [InlineData("KZT", "KZT")]
    [InlineData("GBP", "GBP")]
    [InlineData("XTS", "XTS")]
    [InlineData("kzt", null)]
    [InlineData("KZ", null)]
    [InlineData("KZTT", null)]
    [InlineData("К₸T", null)]
    [InlineData("", null)]
    public void A_currency_is_kept_when_it_has_an_iso_4217_code_s_shape(string value, string? kept)
    {
        InboundValues.Currency(value).ShouldBe(kept);
    }

    [Theory]
    [InlineData("payment_declined", "payment_declined")]
    [InlineData("a_reason_ordering_adds_later_2", "a_reason_ordering_adds_later_2")]
    [InlineData("Payment_Declined", null)]
    [InlineData("payment declined", null)]
    [InlineData("payment-declined", null)]
    [InlineData("", null)]
    public void A_code_is_kept_when_it_has_a_wire_code_s_shape(string value, string? kept)
    {
        InboundValues.Code(value).ShouldBe(kept);
    }

    [Fact]
    public void A_code_past_its_column_s_width_is_dropped()
    {
        InboundValues.Code(new string('r', InboundValues.MaxCodeLength)).ShouldNotBeNull();
        InboundValues.Code(new string('r', InboundValues.MaxCodeLength + 1)).ShouldBeNull();
    }
}

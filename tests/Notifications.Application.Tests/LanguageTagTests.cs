using Notifications.Application.Contacts;
using Shouldly;
using Xunit;

namespace Notifications.Application.Tests;

/// <summary>The BCP 47 shape a locale is held to before it is stored (ADR-052).</summary>
public class LanguageTagTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("kk")]
    [InlineData("ru")]
    [InlineData("en-GB")]
    [InlineData("kk-KZ")]
    [InlineData("kk-Cyrl-KZ")]
    [InlineData("zh-Hant-TW")]
    [InlineData("es-419")]
    [InlineData("de-CH-1996")]
    public void A_tag_of_the_shape_is_one(string value)
    {
        LanguageTag.IsOne(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("k")]
    [InlineData("english")]
    [InlineData("en_GB")]
    [InlineData("en-")]
    [InlineData("-en")]
    [InlineData("en--GB")]
    [InlineData("kk\n")]
    [InlineData("kk\r\nBcc: someone@example.test")]
    [InlineData("<script>")]
    [InlineData("en-GB-subtagtoolong")]
    [InlineData("x-private")]
    [InlineData("қаз")]
    [InlineData("e1")]
    public void Anything_else_is_not(string? value)
    {
        LanguageTag.IsOne(value).ShouldBeFalse();
    }

    [Fact]
    public void A_tag_longer_than_the_bound_is_not_one_whatever_its_shape()
    {
        string longest = "en" + string.Concat(Enumerable.Repeat("-abcdefg", 4));
        string past = longest + "-a";

        longest.Length.ShouldBeLessThanOrEqualTo(LanguageTag.MaxLength);
        LanguageTag.IsOne(longest).ShouldBeTrue("the control, so the refusal below is the length and not the shape");

        past.Length.ShouldBeGreaterThan(LanguageTag.MaxLength);
        LanguageTag.IsOne(past).ShouldBeFalse();
    }
}

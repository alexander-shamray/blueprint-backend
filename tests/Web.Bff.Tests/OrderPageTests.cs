using Shouldly;
using Web.Bff.Orders;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>The list's bounds: clamped rather than refused (§10.7), small because a row carries its lines.</summary>
public sealed class OrderPageTests
{
    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(OrderPage.DefaultLimit, OrderPage.DefaultLimit)]
    [InlineData(OrderPage.MaxLimit, OrderPage.MaxLimit)]
    [InlineData(OrderPage.MaxLimit + 1, OrderPage.MaxLimit)]
    [InlineData(int.MaxValue, OrderPage.MaxLimit)]
    public void A_limit_is_clamped_into_the_page_s_range(int requested, int expected) =>
        OrderPage.Clamp(requested).ShouldBe(expected);
}

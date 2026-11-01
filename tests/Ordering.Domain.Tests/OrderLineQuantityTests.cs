using System.Reflection;
using Common.Domain;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;
using Shouldly;
using Xunit;

namespace Ordering.Domain.Tests;

/// <summary>The quantity guard on <c>OrderLine.IncreaseQuantity</c>, which only <c>Order</c> may call.</summary>
public class OrderLineQuantityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static OrderLine ALine() =>
        Order.Place(
                new CustomerId(Guid.CreateVersion7()),
                Address.Of("1 Test Street", null, "Almaty", "050000", "KZ"),
                [(ProductId.New(), 2, Money.Of(10m, "EUR"))],
                "EUR",
                [],
                Now)
            .Lines.Single();

    // The method is internal and Order.AddLine refuses a non-positive quantity first, so reflection is
    // the only way a test reaches the guard it owns.
    private static void IncreaseQuantity(OrderLine line, int by)
    {
        MethodInfo method = typeof(OrderLine).GetMethod(
            "IncreaseQuantity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            method.Invoke(line, [by]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IncreaseQuantity_refuses_zero_and_negative_and_leaves_the_quantity(int by)
    {
        OrderLine line = ALine();

        var ex = Should.Throw<DomainException>(() => IncreaseQuantity(line, by));

        ex.Message.ShouldBe("Quantity must be positive.");
        line.Quantity.ShouldBe(2);
    }

    [Fact]
    public void IncreaseQuantity_adds_a_positive_quantity()
    {
        OrderLine line = ALine();

        IncreaseQuantity(line, 1);

        line.Quantity.ShouldBe(3);
    }
}

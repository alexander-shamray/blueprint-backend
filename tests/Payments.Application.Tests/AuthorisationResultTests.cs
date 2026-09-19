using Payments.Application.Provider;
using Payments.Domain.Orders;
using Shouldly;
using Xunit;

namespace Payments.Application.Tests;

public class AuthorisationResultTests
{
    [Fact]
    public void The_keys_are_the_order_and_differ_between_the_two_acts()
    {
        OrderId order = OrderId.New();

        string authorise = new AuthorisationRequest(order, Guid.CreateVersion7(), 1m, "EUR").IdempotencyKey;
        string @void = new VoidRequest(order, "psp_x").IdempotencyKey;

        authorise.ShouldBe($"authorise:{order.Value}");
        @void.ShouldBe($"void:{order.Value}");
        authorise.ShouldNotBe(@void, "a void replayed under the authorisation's key would return the authorisation");
    }

    [Fact]
    public void The_two_results_are_the_only_two()
    {
        typeof(AuthorisationResult).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(AuthorisationResult)))
            .Select(t => t.Name)
            .ShouldBe(["Authorised", "Declined"], ignoreOrder: true,
                "a transient fault is an exception, so it can never reach the saga as a decline");
    }
}

using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class ErrorTests
{
    [Fact]
    public void A_not_found_error_asks_for_a_404()
    {
        Error error = Error.NotFound("order.not_found", "No order with that id.");

        error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public void A_rule_error_asks_for_a_422()
    {
        Error error = Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");

        error.Type.ShouldBe(ErrorType.Rule);
    }

    [Fact]
    public void An_unavailable_error_asks_for_a_503()
    {
        Error error = Error.Unavailable("pricing.unreachable", "The price list could not be read.");

        error.Type.ShouldBe(ErrorType.Unavailable);
    }

    [Fact]
    public void An_error_keeps_the_code_and_description_it_was_given()
    {
        Error error = Error.NotFound("order.not_found", "No order with that id.");

        error.Code.ShouldBe("order.not_found");
        error.Description.ShouldBe("No order with that id.");
    }

    [Fact]
    public void Two_errors_with_the_same_parts_are_the_same_error()
    {
        Error one = Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");
        Error other = Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");

        one.ShouldBe(other);
    }

    [Fact]
    public void There_are_exactly_three_error_types()
    {
        // §10.5: no Validation member, since ValidationBehavior rejects before any handler runs.
        Enum.GetValues<ErrorType>().ShouldBe([ErrorType.NotFound, ErrorType.Rule, ErrorType.Unavailable]);
    }
}

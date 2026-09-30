using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class ResultTests
{
    private static readonly Error NotFound =
        Error.NotFound("order.not_found", "No order with that id.");

    [Fact]
    public void A_success_is_not_a_failure()
    {
        Result result = Result.Success();

        result.IsSuccess.ShouldBeTrue();
        result.IsFailure.ShouldBeFalse();
    }

    [Fact]
    public void A_failure_is_not_a_success()
    {
        Result result = Result.Failure(NotFound);

        result.IsFailure.ShouldBeTrue();
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void A_failure_carries_the_error_it_was_given()
    {
        Result result = Result.Failure(NotFound);

        result.Error.ShouldBe(NotFound);
    }

    [Fact]
    public void A_success_has_no_error_to_read()
    {
        Result result = Result.Success();

        Should.Throw<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void A_success_carries_its_value()
    {
        Result<Guid> result = Result.Success(Guid.CreateVersion7());

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_failed_result_has_no_value_to_read()
    {
        Result<Guid> result = Result.Failure<Guid>(NotFound);

        Should.Throw<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void A_failed_result_carries_the_error_it_was_given()
    {
        Result<Guid> result = Result.Failure<Guid>(NotFound);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(NotFound);
    }

    [Fact]
    public void A_result_with_a_value_is_still_a_result()
    {
        // §6.3 tests any command's outcome with one pattern because Result<T> derives from Result.
        object outcome = Result.Failure<Guid>(NotFound);

        (outcome is Result { IsFailure: true }).ShouldBeTrue();
    }

    [Fact]
    public void A_failure_needs_an_error()
    {
        Should.Throw<ArgumentNullException>(() => Result.Failure(null!));
        Should.Throw<ArgumentNullException>(() => Result.Failure<Guid>(null!));
    }
}

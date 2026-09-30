using Common.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class ResultExtensionsTests
{
    [Theory]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Rule, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(ErrorType.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public void An_error_type_selects_its_status(ErrorType type, int expected)
    {
        // §10.5's table: Rule is 422, since the concurrency case owns 409.
        IResult result = Result.Failure(ErrorOf(type)).ToHttpResult();

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(expected);
    }

    [Theory]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Rule, StatusCodes.Status422UnprocessableEntity)]
    [InlineData(ErrorType.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public void A_value_result_fails_with_the_same_status_as_a_void_one(ErrorType type, int expected)
    {
        IResult result = Result.Failure<Guid>(ErrorOf(type)).ToHttpResult();

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(expected);
    }

    [Fact]
    public void A_failure_carries_its_code_as_a_problem_extension()
    {
        // Code goes in an extension member, leaving RFC 9457's title as the status phrase.
        IResult result = Result.Failure(ErrorOf(ErrorType.Rule)).ToHttpResult();

        result.ShouldBeOfType<ProblemHttpResult>()
            .ProblemDetails.Extensions["code"].ShouldBe("test.rule");
    }

    [Fact]
    public void A_failure_carries_its_description_as_the_detail()
    {
        // Description may vary, unlike the code, which is a metric dimension (§9.8).
        IResult result = Result.Failure(ErrorOf(ErrorType.Rule)).ToHttpResult();

        result.ShouldBeOfType<ProblemHttpResult>()
            .ProblemDetails.Detail.ShouldBe("A description written for a person.");
    }

    [Fact]
    public void A_void_success_has_no_body_to_return()
    {
        IResult result = Result.Success().ToHttpResult();

        result.ShouldBeOfType<NoContent>();
    }

    [Fact]
    public void A_value_success_carries_its_value()
    {
        Guid value = Guid.CreateVersion7();

        IResult result = Result.Success(value).ToHttpResult();

        result.ShouldBeOfType<Ok<Guid>>().Value.ShouldBe(value);
    }

    [Fact]
    public void A_value_result_selects_the_overload_that_returns_its_value()
    {
        // Result<T> derives from Result (§6.3), so only the identity conversion picks the generic overload.
        Result<Guid> result = Result.Success(Guid.CreateVersion7());

        result.ToHttpResult().ShouldBeOfType<Ok<Guid>>();
    }

    private static Error ErrorOf(ErrorType type) => type switch
    {
        ErrorType.NotFound => Error.NotFound("test.not_found", "A description written for a person."),
        ErrorType.Rule => Error.Rule("test.rule", "A description written for a person."),
        _ => Error.Unavailable("test.unavailable", "A description written for a person.")
    };
}

using Common.Application;
using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>The one place §10.5's status-code table is executed rather than remembered.</summary>
public static class ResultExtensions
{
    /// <summary>204 on success, and the problem response its <see cref="Error"/> selects otherwise.</summary>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? Results.NoContent() : Problem(result.Error);

    /// <summary>200 carrying the value, and the same problem response on failure.</summary>
    /// <remarks>A value result typed as <see cref="Result"/> binds the overload above and loses its payload.</remarks>
    public static IResult ToHttpResult<TValue>(this Result<TValue> result) =>
        result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error);

    private static IResult Problem(Error error) =>
        Results.Problem(
            detail: error.Description,
            statusCode: StatusFor(error.Type),
            extensions: new Dictionary<string, object?> { ["code"] = error.Code });

    // Code goes into an extension rather than the title, because it is what a client switches on (§10.5).
    private static int StatusFor(ErrorType type) => type switch
    {
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Rule => StatusCodes.Status422UnprocessableEntity,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No status is mapped for this type.")
    };
}

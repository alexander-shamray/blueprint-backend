namespace Common.Application;

/// <summary>The outcome of a command that returns nothing; <see cref="Result{TValue}"/> derives from it.</summary>
/// <remarks>One base, so §6.3 tests any command's outcome with one pattern and no reflection.</remarks>
public class Result
{
    private readonly Error? _error;

    private protected Result(Error? error)
    {
        _error = error;
    }

    public bool IsSuccess => _error is null;

    public bool IsFailure => _error is not null;

    /// <summary>Throws on a success, so a call site behind <c>IsFailure</c> needs no null check.</summary>
    public Error Error => _error ?? throw new InvalidOperationException("A successful result carries no error.");

    public static Result Success() => new(null);

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static Result<TValue> Success<TValue>(TValue value) => new(value, null);

    public static Result<TValue> Failure<TValue>(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(default, error);
    }
}

/// <summary>Built only through <see cref="Result"/>'s factories, so a failure never carries a value.</summary>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, Error? error) : base(error)
    {
        _value = value;
    }

    public TValue Value =>
        IsSuccess ? _value! : throw new InvalidOperationException("A failed result carries no value.");
}

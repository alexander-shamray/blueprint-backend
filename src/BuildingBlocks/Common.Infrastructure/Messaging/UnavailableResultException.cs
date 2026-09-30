using Common.Application;

namespace Common.Infrastructure.Messaging;

/// <summary>Thrown for an <see cref="ErrorType.Unavailable"/> result, so §9.8's retry policy sees it.</summary>
/// <remarks>Over HTTP a 503 lets the caller retry (§10.5); a message-borne command has no such caller.</remarks>
public sealed class UnavailableResultException : Exception
{
    public UnavailableResultException()
    {
    }

    public UnavailableResultException(string message)
        : base(message)
    {
    }

    public UnavailableResultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UnavailableResultException(Error error)
        : base($"A handler returned {error.Code}: {error.Description}. " +
            "ErrorType.Unavailable is a transient condition, so the message is faulted " +
            "rather than acked and §9.8's policy retries it.") =>
        Error = error;

    /// <summary>The failure the handler returned, for a log that wants the code.</summary>
    public Error? Error { get; }
}

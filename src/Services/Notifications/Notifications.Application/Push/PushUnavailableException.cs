namespace Notifications.Application.Push;

/// <summary>A push that met a fault rather than an answer: the row backs off, and nothing reaches a queue.</summary>
public sealed class PushUnavailableException : Exception
{
    public PushUnavailableException()
    {
    }

    public PushUnavailableException(string message)
        : base(message)
    {
    }

    public PushUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The adapter's form, with no inner exception, as a provider's own can quote the token.</summary>
    public PushUnavailableException(string message, PushFault cause, int? httpStatus)
        : base(message)
    {
        Cause = cause;
        HttpStatus = httpStatus;
    }

    public PushFault Cause { get; }

    /// <summary>The provider's status code, where it gave one; its words are never kept.</summary>
    public int? HttpStatus { get; }
}

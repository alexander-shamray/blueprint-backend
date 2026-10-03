namespace Notifications.Application.Rendering;

/// <summary>A row's parameters are a version this code does not read, or not the shape of the one it does.</summary>
public sealed class UnreadableParametersException : Exception
{
    public UnreadableParametersException()
    {
    }

    public UnreadableParametersException(string message)
        : base(message)
    {
    }

    public UnreadableParametersException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace Notifications.Application.Rendering;

/// <summary>The shipped templates cannot serve this deployment; each refusal names the file or value to fix.</summary>
public sealed class TemplateSetException : Exception
{
    public TemplateSetException()
    {
        Refusals = [];
    }

    public TemplateSetException(string message)
        : base(message)
    {
        Refusals = [message];
    }

    public TemplateSetException(string message, Exception innerException)
        : base(message, innerException)
    {
        Refusals = [message];
    }

    public TemplateSetException(IReadOnlyList<string> refusals)
        : base(string.Join(Environment.NewLine, refusals))
    {
        Refusals = refusals;
    }

    /// <summary>Every refusal found, so one start names them all rather than one per deploy.</summary>
    public IReadOnlyList<string> Refusals { get; }
}

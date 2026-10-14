namespace Notifications.Application.Push;

/// <summary>A provider's answer to an <see cref="OutboundPush"/>.</summary>
public abstract record PushResult
{
    private PushResult() { }

    public sealed record Accepted : PushResult;

    public sealed record Refused(PushRefusal Reason) : PushResult;
}

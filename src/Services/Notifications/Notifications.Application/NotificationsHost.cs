namespace Notifications.Application;

/// <summary>The service's two hosts, which compose the Application and Infrastructure layers differently (ADR-118).</summary>
public enum NotificationsHost
{
    /// <summary>The consumers, the send pass, the retention passes and the outbound hops.</summary>
    Worker,

    /// <summary>The synchronous half the BFF calls, on the same database and nothing else of the worker's.</summary>
    Api
}

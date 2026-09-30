namespace Common.Application;

/// <summary>Which after-commit destination a staged row is for; one table serves both (§9.4).</summary>
public enum OutboxLane
{
    /// <summary>Published to the message broker as a public contract.</summary>
    Broker,

    /// <summary>Dispatched in-process to <see cref="IProjectionHandler{TEvent}"/>; never leaves the service.</summary>
    Local
}

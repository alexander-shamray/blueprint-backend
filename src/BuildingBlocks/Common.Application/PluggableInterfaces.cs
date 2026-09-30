namespace Common.Application;

/// <summary>Every open generic the container discovers by convention, read by the scan and its guard (§6.2).</summary>
public static class PluggableInterfaces
{
    public static readonly IReadOnlyList<Type> All =
    [
        typeof(ICommandHandler<,>),          // §6.2 — HTTP and message-borne
        typeof(IQueryHandler<,>),            // §6.5
        typeof(IProjectionHandler<>),        // §7.5 — the local outbox lane
        typeof(IIntegrationEventHandler<>),  // §9.4 — another service's events
        typeof(ICommandMessageMapper<,>)     // §9.4 — wire contract → command

        // IPipelineBehavior<,> is absent: registration order is pipeline order (§6.3), and a scan has none.
    ];
}

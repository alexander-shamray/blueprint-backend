namespace Common.Application;

/// <summary>Reacts to another service's integration event, behind §9.5's inbox filter (§9.4).</summary>
/// <remarks>Invariant, because the container matches only the closed type (§9.4).</remarks>
public interface IIntegrationEventHandler<TEvent>
    where TEvent : class
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken ct);
}

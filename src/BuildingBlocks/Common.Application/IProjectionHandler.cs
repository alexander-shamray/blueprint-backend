namespace Common.Application;

/// <summary>Reacts to this service's own events after commit, on the <c>Local</c> outbox lane (§9.4).</summary>
/// <remarks>Invariant, because the container matches only the closed type (§9.4).</remarks>
public interface IProjectionHandler<TEvent>
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}

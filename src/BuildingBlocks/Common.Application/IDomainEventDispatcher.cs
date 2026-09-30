namespace Common.Application;

public interface IDomainEventDispatcher
{
    /// <summary>Stages outbox rows for raised events and runs no handler, inside the transaction (§7.5).</summary>
    Task DispatchAsync(CancellationToken ct);
}

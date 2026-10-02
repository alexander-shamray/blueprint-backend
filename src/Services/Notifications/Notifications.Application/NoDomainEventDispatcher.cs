using Common.Application;

namespace Notifications.Application;

/// <summary>§7.5's dispatcher for a service §4.1 gives no Domain project, where no aggregate raises an event.</summary>
internal sealed class NoDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct) => Task.CompletedTask;
}

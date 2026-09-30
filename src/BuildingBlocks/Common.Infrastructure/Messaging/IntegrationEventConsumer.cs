using Common.Application;
using Common.Contracts;
using MassTransit;

namespace Common.Infrastructure.Messaging;

/// <summary>§9.4's bridge from the broker to <see cref="IIntegrationEventHandler{TEvent}"/> (ADR-014).</summary>
public sealed class IntegrationEventConsumer<TEvent>(
    IEnumerable<IIntegrationEventHandler<TEvent>> handlers,
    MessagingMetrics metrics,
    TimeProvider clock)
    : IConsumer<TEvent>
    where TEvent : class, IIntegrationEvent
{
    public async Task Consume(ConsumeContext<TEvent> context)
    {
        // Publish-to-consumer-start lag (§13.3), so recorded before the handlers run.
        metrics.Delivered(typeof(TEvent).Name, clock.GetUtcNow() - context.Message.OccurredAt);

        // Zero handlers throws, since an ack would let the inbox drop the message for good (§9.4, §9.5).
        // Materialised, because counting and then iterating a lazy enumerable would resolve twice.
        IIntegrationEventHandler<TEvent>[] resolved = [.. handlers];

        if (resolved.Length == 0)
        {
            throw new InvalidOperationException(
                $"No IIntegrationEventHandler<{typeof(TEvent).Name}> is registered, " +
                $"but {typeof(TEvent).Name} is bound on this endpoint. Check the §6.2 scan.");
        }

        // Sequential: two handlers writing one read table in parallel can deadlock.
        foreach (IIntegrationEventHandler<TEvent> handler in resolved)
            await handler.HandleAsync(context.Message, context.CancellationToken);
    }
}

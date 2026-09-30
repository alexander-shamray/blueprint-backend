using System.Collections.Concurrent;
using Common.Application;
using Common.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Infrastructure.Outbox;

/// <summary>Calls the projection handlers for a runtime type, with §6.2's cached-delegate approach.</summary>
internal static class ProjectionInvoker
{
    private static readonly ConcurrentDictionary<Type, Invoker> Cache = new();

    public static Task InvokeAllAsync(
        IServiceProvider sp,
        object payload,
        Type eventType,
        DateTimeOffset occurredAt,
        CancellationToken ct) =>
        Cache
            .GetOrAdd(eventType, static t => (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(t))!)
            .InvokeAllAsync(sp, payload, occurredAt, ct);

    private abstract class Invoker
    {
        public abstract Task InvokeAllAsync(
            IServiceProvider sp,
            object payload,
            DateTimeOffset occurredAt,
            CancellationToken ct);
    }

    private sealed class Invoker<TEvent> : Invoker
    {
        public override async Task InvokeAllAsync(
            IServiceProvider sp,
            object payload,
            DateTimeOffset occurredAt,
            CancellationToken ct)
        {
            IProjectionHandler<TEvent>[] handlers = [.. sp.GetServices<IProjectionHandler<TEvent>>()];

            // A Local row is staged only when a handler was found (§7.5), so none here is unregistered.
            if (handlers.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No IProjectionHandler<{typeof(TEvent).Name}> is registered, " +
                    "but a Local outbox row was staged for it. Check the §6.2 scan.");
            }

            // Sequential: two projections writing one read table in parallel can deadlock.
            foreach (IProjectionHandler<TEvent> handler in handlers)
                await handler.HandleAsync((TEvent)payload, ct);

            // Raised-to-applied (§13.7), so recorded after the handlers; resolved from sp, as this type is static.
            sp.GetRequiredService<MessagingMetrics>().Projected(
                typeof(TEvent).Name,
                sp.GetRequiredService<TimeProvider>().GetUtcNow() - occurredAt);
        }
    }
}

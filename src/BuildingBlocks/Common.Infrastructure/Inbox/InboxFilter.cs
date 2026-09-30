using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Inbox;

/// <summary>§9.5's suppression: a recorded id is dropped, else the consumer runs and then it is recorded.</summary>
/// <remarks>
/// The <c>DbContext</c> is the service's alias, never a second context, so the row can share the handler's transaction.
/// The key is the publisher's choice, so the filter is only as trustworthy as who may publish (§9.5, ADR-036).
/// </remarks>
public sealed class InboxFilter<T>(
    DbContext db,
    TimeProvider clock,
    MessagingMetrics metrics,
    ILogger<InboxFilter<T>> log)
    : IFilter<ConsumeContext<T>>
    where T : class
{
    // The type arguments bind to the template by position, not by name.
    private static readonly Action<ILogger, string, Guid, string, Exception?> Suppressed =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Debug,
            new EventId(1, nameof(Suppressed)),
            "Inbox dropped {MessageType} {MessageId} on {Endpoint}: already recorded as handled.");

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        // One GUID for transport, envelope and outbox row (§9.1); without one there is nothing to deduplicate on.
        Guid messageId = context.MessageId ??
            throw new InvalidOperationException("Message has no MessageId.");

        // The same type on a different endpoint is a different unit of work.
        string endpoint = context.ReceiveContext.InputAddress.AbsolutePath.TrimStart('/');

        bool alreadyHandled = await db
            .Set<InboxMessage>()
            .AnyAsync(
                m => m.MessageId == messageId && m.Endpoint == endpoint,
                context.CancellationToken);

        if (alreadyHandled)
        {
            // Dropped, but counted and logged: the MessageId goes on the log because it is unbounded.
            metrics.Suppressed(typeof(T).Name, endpoint);
            Suppressed(log, typeof(T).Name, messageId, endpoint, null);
            return;
        }

        // The handler runs first: recording before would drop a message never handled.
        await next.Send(context);

        // Staged after the consumer, because §6.3's retry clears the tracker and would take a staged row with it.
        db.Set<InboxMessage>().Add(new InboxMessage(messageId, endpoint, clock.GetUtcNow()));

        // The registered clock, which RetentionPurgeService's cutoff also reads.
        await db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>MassTransit's diagnostic probe; the scope name identifies this filter in its probe result.</summary>
    public void Probe(ProbeContext context) => context.CreateFilterScope("inbox");
}

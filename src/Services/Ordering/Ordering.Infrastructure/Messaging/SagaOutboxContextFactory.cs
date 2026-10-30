using System.Diagnostics;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using MassTransit.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Ordering.Infrastructure.Persistence;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// The saga endpoint's outbox in two transactions a message where MassTransit's own factory takes four (ADR-174):
/// the row's insert with the consume, and the delivery with the removal.
/// </summary>
/// <remarks>Modelled on MassTransit 8.5.12's <c>EntityFrameworkOutboxContextFactory</c>, Apache 2.0, ADR-174.</remarks>
internal sealed class SagaOutboxContextFactory(
    OrderingDbContext db,
    IServiceProvider provider,
    IOptions<EntityFrameworkOutboxOptions<OrderingDbContext>> outboxOptions) : IOutboxContextFactory<OrderingDbContext>
{
    private string? _lockStatement;

    public async Task Send<T>(
        ConsumeContext<T> context,
        OutboxConsumeOptions options,
        IPipe<OutboxConsumeContext<T>> next)
        where T : class
    {
        Guid messageId = context.GetOriginalMessageId()
            ?? throw new MessageException(typeof(T), "MessageId required to use the outbox");

        _lockStatement ??= outboxOptions.Value.LockStatementProvider.GetRowLockStatement<InboxState>(
            db,
            nameof(InboxState.MessageId),
            nameof(InboxState.ConsumerId));

        bool continueProcessing = true;
        while (continueProcessing)
        {
            IExecutionStrategy strategy = db.Database.CreateExecutionStrategy();
            continueProcessing = strategy is ExecutionStrategy
                ? await strategy.ExecuteAsync(() => ExecuteAsync(context, options, next, messageId))
                : await ExecuteAsync(context, options, next, messageId);
        }
    }

    public void Probe(ProbeContext context) =>
        context.CreateFilterScope("outboxContextFactory").Add("provider", "entityFrameworkCore");

    private async Task<bool> ExecuteAsync<T>(
        ConsumeContext<T> context,
        OutboxConsumeOptions options,
        IPipe<OutboxConsumeContext<T>> next,
        Guid messageId)
        where T : class
    {
        Stopwatch timer = Stopwatch.StartNew();

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(outboxOptions.Value.IsolationLevel, context.CancellationToken);

        try
        {
            InboxState? inboxState = (await db.Set<InboxState>()
                .FromSqlRaw(_lockStatement!, messageId, options.ConsumerId)
                .AsTracking()
                .ToListAsync(context.CancellationToken)).SingleOrDefault();

            // MassTransit commits a new row before consuming; inserted here, its key lock holds a duplicate delivery
            // off until this commit, at its read or at its own insert, which then fails into the endpoint's retry.
            if (inboxState is null)
            {
                inboxState = new InboxState
                {
                    MessageId = messageId,
                    ConsumerId = options.ConsumerId,
                    Received = DateTime.UtcNow,
                    LockId = NewId.NextGuid(),
                    ReceiveCount = 0
                };

                await db.AddAsync(inboxState, context.CancellationToken);
                await db.SaveChangesAsync(context.CancellationToken);
            }
            else
            {
                // Saved with the pass's own write rather than in an UPDATE of their own; the lock read holds the row.
                inboxState.LockId = NewId.NextGuid();
                inboxState.ReceiveCount++;
            }

            DbContextOutboxConsumeContext<OrderingDbContext, T> outboxContext =
                new(context, options, provider, db, transaction, inboxState);

            bool delivering = outboxContext.IsMessageConsumed && !outboxContext.IsOutboxDelivered;

            await next.Send(outboxContext);

            // A pass that has just delivered removes in the same commit; one that consumed must commit before the
            // broker sees a message, which is the outbox's guarantee (ADR-032).
            if (delivering && outboxContext.IsOutboxDelivered && outboxContext.ContinueProcessing)
                await next.Send(outboxContext);

            try
            {
                await db.SaveChangesAsync(context.CancellationToken);
                await transaction.CommitAsync(context.CancellationToken);
            }
            catch (Exception exception)
            {
                await context.NotifyFaulted(timer.Elapsed, TypeCache<T>.ShortName, exception);

                throw;
            }

            return outboxContext.ContinueProcessing;
        }
        catch (Exception)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // The original exception is the one worth raising; a failed rollback ends with the connection.
            }

            throw;
        }
    }
}

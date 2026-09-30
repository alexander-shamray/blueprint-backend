namespace Common.Application;

public sealed class TransactionBehavior<TCommand, TResult>(
    IUnitOfWork unitOfWork,
    IDomainEventDispatcher domainEvents,
    IIdempotencyMarkerStore markers,
    IdempotencyContext idempotency)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Already inside a transaction (nested dispatch) — do not open another.
        if (unitOfWork.HasActiveTransaction)
            return await next();

        // Read once, before the unit, so a nested dispatch that overwrites the context marks nothing here.
        string? key = idempotency.Key;

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                // Before the handler, so a duplicate whose commit landed costs a lookup (ADR-037).
                if (key is not null && await markers.ExistsAsync(key, token))
                    throw new CommandAlreadyCommittedException(key);

                TResult result = await next();

                // A rejected command stages nothing, saves nothing and writes no marker.
                if (result is Result { IsFailure: true })
                    return result;

                // Stages outbox rows only; reactions run after commit (§7.5).
                await domainEvents.DispatchAsync(token);

                // Principle 3 (§2.3), asserted at run time rather than by an architecture test (§6.3).
                if (unitOfWork.ModifiedAggregateCount > 1)
                {
                    throw new InvariantViolationException(
                        $"{typeof(TCommand).Name} modified {unitOfWork.ModifiedAggregateCount} " +
                        "aggregate roots. One transaction, one aggregate (§2.3 principle 3) — " +
                        "the second aggregate should react to a domain event after commit (§7.5).");
                }

                // After the count check, so a command about to be refused leaves no marker to refuse its retry.
                if (key is not null)
                    await markers.MarkAsync(key, token);

                await unitOfWork.SaveChangesAsync(token);

                return result;
            },
            ct);
    }
}

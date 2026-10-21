using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.TestSupport.Outbox;

/// <summary>A command that writes staged rows and then fails, so they can be seen to roll back.</summary>
/// <remarks>
/// <c>TransactionBehavior</c> throws on a second modified root (§2.3, principle 3; §6.3) before its own save, so
/// the first two roots are staged and saved here, where only the transaction's rollback can undo them.
/// </remarks>
public sealed record StageThenFailCommand(string Name) : ICommand<Result>;

public sealed class StageThenFailHandler(
    IProductRepository products,
    IDomainEventDispatcher domainEvents,
    IUnitOfWork unitOfWork,
    TimeProvider clock)
    : ICommandHandler<StageThenFailCommand, Result>
{
    public async Task<Result> HandleAsync(StageThenFailCommand command, CancellationToken ct)
    {
        products.Add(Product.Publish(command.Name, null, Money.Of(1m, "EUR"), clock.GetUtcNow()));
        products.Add(Product.Publish(command.Name, null, Money.Of(2m, "EUR"), clock.GetUtcNow()));
        await domainEvents.DispatchAsync(ct);
        await unitOfWork.SaveChangesAsync(ct);

        // Two more roots, so the assertion fires after the rows above have reached the database.
        products.Add(Product.Publish(command.Name, null, Money.Of(3m, "EUR"), clock.GetUtcNow()));
        products.Add(Product.Publish(command.Name, null, Money.Of(4m, "EUR"), clock.GetUtcNow()));

        return Result.Success();
    }
}

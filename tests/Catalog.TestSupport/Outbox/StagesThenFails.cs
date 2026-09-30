using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.TestSupport.Outbox;

/// <summary>A command that reaches the outbox and then fails, so a staged row can be seen to roll back.</summary>
/// <remarks>
/// <c>TransactionBehavior</c> stages, then throws on a second modified root (§2.3, principle 3; §6.3), where an
/// earlier failure would stage nothing.
/// </remarks>
public sealed record StageThenFailCommand(string Name) : ICommand<Result>;

public sealed class StageThenFailHandler(IProductRepository products, TimeProvider clock)
    : ICommandHandler<StageThenFailCommand, Result>
{
    public Task<Result> HandleAsync(StageThenFailCommand command, CancellationToken ct)
    {
        // Two roots, so the assertion fires after the dispatcher has staged an event for each.
        products.Add(Product.Publish(command.Name, null, Money.Of(1m, "EUR"), clock.GetUtcNow()));
        products.Add(Product.Publish(command.Name, null, Money.Of(2m, "EUR"), clock.GetUtcNow()));

        return Task.FromResult(Result.Success());
    }
}

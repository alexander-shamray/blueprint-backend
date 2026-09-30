using Common.Application;
using Ordering.Domain.Common;
using Ordering.Domain.Orders;

namespace Ordering.Application.Orders.PlaceOrder;

/// <summary>§6.4's handler: thin, with every business rule in <see cref="Order"/>.</summary>
/// <remarks><see cref="ICurrentUser"/> is the only source of the subject, on an HTTP-only path (§11.4).</remarks>
public sealed class PlaceOrderHandler(
    IOrderRepository orders,
    IProductPriceReader prices,
    ICurrentUser currentUser,
    TimeProvider clock)
    : ICommandHandler<PlaceOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(PlaceOrderCommand command, CancellationToken ct)
    {
        // Distinct, since each id is a SQL parameter and Order.AddLine merges a repeated product anyway.
        ProductId[] productIds = [.. command.Items.Select(i => new ProductId(i.ProductId)).Distinct()];
        IReadOnlyDictionary<ProductId, Money> priceList =
            await prices.GetAsync(productIds, command.Currency, ct);

        ProductId[] missing = [.. productIds.Where(id => !priceList.ContainsKey(id))];
        if (missing.Length > 0)
            return Result.Failure<Guid>(OrderErrors.ProductsUnavailable(missing));

        // The last point before the order exists: past the ceiling, consumers recording the total would fail later.
        decimal total = command.Items.Sum(i => priceList[new ProductId(i.ProductId)].Amount * i.Quantity);
        if (total >= OrderAmounts.Ceiling)
            return Result.Failure<Guid>(OrderErrors.TotalBeyondCeiling);

        IEnumerable<(ProductId Product, int Quantity, Money UnitPrice)> items =
            command.Items.Select(i =>
            {
                var id = new ProductId(i.ProductId);
                return (id, i.Quantity, priceList[id]);
            });

        var order = Order.Place(
            new CustomerId(currentUser.Id),
            command.ShippingAddress.ToDomain(),
            items,
            command.Currency,
            clock.GetUtcNow());

        orders.Add(order);

        // No metric here: a count inside the transaction is repeated by a replay; the projection records it (§13.3).
        return Result.Success(order.Id.Value);
    }
}

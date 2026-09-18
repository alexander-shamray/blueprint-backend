using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Inventory.Application;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Domain.Reservations;
using Inventory.Domain.Stock;

namespace Inventory.Infrastructure.Messaging;

public sealed class ReserveStockMapper : ICommandMessageMapper<ReserveStock, ReserveStockCommand>
{
    public ReserveStockCommand Map(ReserveStock message)
    {
        if (message.Lines is null || message.Lines.Count == 0)
            throw new ContractMappingException($"No lines on {nameof(ReserveStock)}.");

        // A null element is a malformed payload, and reading its members
        // would throw NullReferenceException — a fault the endpoint retries
        // where this exception is the one it does not.
        if (message.Lines.Any(l => l is null))
            throw new ContractMappingException($"A null line on {nameof(ReserveStock)}.");

        if (message.Lines.Any(l => l.Quantity < OrderLimits.MinQuantity || l.Quantity > OrderLimits.MaxQuantity))
        {
            throw new ContractMappingException(
                $"A quantity outside the contract's bounds on {nameof(ReserveStock)}.");
        }

        // An empty product id is a malformed payload, not an unknown product:
        // let through, it would reach the ledger, affect no row, and be
        // published as an out-of-stock decision about a product that is not one.
        if (message.OrderId == Guid.Empty || message.Lines.Any(l => l.ProductId == Guid.Empty))
            throw new ContractMappingException($"An empty identifier on {nameof(ReserveStock)}.");

        // Every line is a statement under a row lock in one transaction, so a
        // payload longer than any order can be is refused before it holds a
        // lock rather than starving the queue while it runs them.
        if (message.Lines.Count > OrderLimits.MaxLines)
            throw new ContractMappingException($"More lines than an order can carry on {nameof(ReserveStock)}.");

        if (message.Lines.Select(l => l.ProductId).Distinct().Count() != message.Lines.Count)
            throw new ContractMappingException($"A repeated product on {nameof(ReserveStock)}.");

        return new ReserveStockCommand(
            message.OrderId,
            [.. message.Lines.Select(l => new ReservationLine(new ProductId(l.ProductId), l.Quantity))]);
    }
}

public sealed class ReleaseStockMapper : ICommandMessageMapper<ReleaseStock, ReleaseStockCommand>
{
    public ReleaseStockCommand Map(ReleaseStock message)
    {
        // A malformed payload, the same refusal ReserveStockMapper makes for
        // an empty identifier: on this path a validator failure propagates
        // out of CommandConsumer as a fault, and RetryPolicy.Standard spends
        // five exponential attempts on it before the error queue, where a
        // domain rejection is acked, counted and logged on the first.
        if (message.OrderId == Guid.Empty)
            throw new ContractMappingException($"An empty identifier on {nameof(ReleaseStock)}.");

        return new(message.OrderId, CommandOrigin.System);
    }
}

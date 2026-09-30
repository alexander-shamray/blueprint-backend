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

        // Refused here, since reading a null line's members would throw a fault the endpoint retries.
        if (message.Lines.Any(l => l is null))
            throw new ContractMappingException($"A null line on {nameof(ReserveStock)}.");

        if (message.Lines.Any(l => l.Quantity < OrderLimits.MinQuantity || l.Quantity > OrderLimits.MaxQuantity))
        {
            throw new ContractMappingException(
                $"A quantity outside the contract's bounds on {nameof(ReserveStock)}.");
        }

        // Malformed, not unknown: let through, an empty product id would be published as out of stock.
        if (message.OrderId == Guid.Empty || message.Lines.Any(l => l.ProductId == Guid.Empty))
            throw new ContractMappingException($"An empty identifier on {nameof(ReserveStock)}.");

        // Each line is a statement under a row lock, so an oversized payload is refused before it holds one.
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
        // ReserveStockMapper's refusal: a validator failure here would fault and spend RetryPolicy's whole ladder,
        // where a domain rejection is acked on the first attempt (§9.8).
        if (message.OrderId == Guid.Empty)
            throw new ContractMappingException($"An empty identifier on {nameof(ReleaseStock)}.");

        return new(message.OrderId, CommandOrigin.System);
    }
}

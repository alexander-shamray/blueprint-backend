using Common.Application;

namespace Inventory.Application.Stock.GetAvailability;

public sealed record GetAvailabilityQuery(IReadOnlyList<Guid> ProductIds)
    : IQuery<IReadOnlyList<ProductAvailabilityDto>>;

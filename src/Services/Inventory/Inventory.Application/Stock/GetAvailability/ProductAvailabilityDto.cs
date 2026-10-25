namespace Inventory.Application.Stock.GetAvailability;

/// <summary>Whether a product can be bought now; the count behind it stays in this service (ADR-154).</summary>
public sealed record ProductAvailabilityDto(Guid ProductId, bool InStock);

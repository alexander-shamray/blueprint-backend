namespace Catalog.Application.Products.GetPrices;

/// <summary>Exactly the shape the pricing hop needs (§6.5), with the price as its two column values.</summary>
public sealed record ProductPriceDto(Guid ProductId, string Name, decimal Amount, string Currency);

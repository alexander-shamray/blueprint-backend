using Common.Contracts.Catalog.V1;
using static Platform.IntegrationTests.Contracts.SampleValues;

namespace Platform.IntegrationTests.Contracts.Catalog;

/// <summary>Catalog's samples, which publish events and accept no command.</summary>
internal sealed class CatalogContracts : ContractContext
{
    public override string Service => "Catalog";

    public override IReadOnlyDictionary<Type, Func<object>> Samples { get; } = new Dictionary<Type, Func<object>>
    {
        [typeof(ProductPublished)] = () => new ProductPublished
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product,
            Name = "Espresso machine",
            ThumbnailUrl = "https://cdn.example.test/espresso.png",
            Amount = 499.99m,
            Currency = "EUR"
        },
        [typeof(PriceChanged)] = () => new PriceChanged
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product,
            Amount = 449.50m,
            Currency = "EUR"
        },
        [typeof(ProductDiscontinued)] = () => new ProductDiscontinued
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product
        }
    };
}

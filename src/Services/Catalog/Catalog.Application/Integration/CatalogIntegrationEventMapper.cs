using Catalog.Domain.Products;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Domain;

namespace Catalog.Application.Integration;

/// <summary>
/// §9.3's allow-list for Catalog. §5.5 states the principle — never publish a
/// domain event to the bus — and one absent from <see cref="Registry"/> never reaches it.
/// </summary>
internal sealed class CatalogIntegrationEventMapper : IIntegrationEventMapper
{
    // The allow-list. Catalog's last fact of §3.2, ProductDiscontinued, joins
    // it with the domain operation that raises it; an entry here with no
    // domain event behind it would not compile, which is the property that
    // keeps this list honest.
    private static readonly Dictionary<Type, Func<IDomainEvent, object>> Registry = new()
    {
        // Domain type in, contract type out. The suffix (§5.5) is what makes
        // that visible — with one name for both, this reads as identity, and
        // §12.4's "the domain type never reaches the broker" would have
        // nothing to assert against.
        [typeof(ProductPublishedDomainEvent)] = e => ToContract((ProductPublishedDomainEvent)e),
        [typeof(PriceChangedDomainEvent)] = e => ToContract((PriceChangedDomainEvent)e)
    };

    public IReadOnlyList<object> Map(IReadOnlyList<IDomainEvent> domainEvents)
    {
        List<object> mapped = [];

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            if (!Registry.TryGetValue(domainEvent.GetType(), out Func<IDomainEvent, object>? map))
                continue;                       // Unregistered → local-only. Not an error.

            mapped.Add(map(domainEvent));       // Registered and throwing → fails the command.
        }

        return mapped;
    }

    // V1.ProductPublished, not ProductPublishedDomainEvent: Money is
    // decomposed into a decimal and an ISO code, because a contract may not
    // carry domain types (§9.1).
    private static ProductPublished ToContract(ProductPublishedDomainEvent e) => new()
    {
        // Minted here and nowhere else. Stage copies both onto the row and
        // DeliverAsync copies them onto the transport, so the body, the row,
        // the broker header and the inbox key are one GUID (§9.1).
        MessageId = Guid.CreateVersion7(),
        // The product, not an ambient request id: a business correlation is
        // what a support tool follows across services, and §9.3 sets it from
        // the aggregate for exactly that reason.
        CorrelationId = e.ProductId.Value,
        OccurredAt = e.OccurredAt,
        ProductId = e.ProductId.Value,
        Name = e.Name,
        ThumbnailUrl = e.ThumbnailUrl,
        Amount = e.Price.Amount,
        Currency = e.Price.Currency
    };

    // The same envelope as ProductPublished's, for the same reasons.
    private static PriceChanged ToContract(PriceChangedDomainEvent e) => new()
    {
        MessageId = Guid.CreateVersion7(),
        CorrelationId = e.ProductId.Value,
        OccurredAt = e.OccurredAt,
        ProductId = e.ProductId.Value,
        Amount = e.Price.Amount,
        Currency = e.Price.Currency
    };
}

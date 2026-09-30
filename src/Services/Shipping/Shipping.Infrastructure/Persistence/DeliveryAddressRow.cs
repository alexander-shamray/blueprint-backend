namespace Shipping.Infrastructure.Persistence;

/// <summary>Mapped only so <c>migrations add</c> emits <see cref="SqlDeliveryAddressStore"/>'s table.</summary>
internal sealed class DeliveryAddressRow
{
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string City { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
    public DateTimeOffset FetchedAt { get; set; }
}

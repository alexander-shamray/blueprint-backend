namespace Shipping.Infrastructure.Persistence;

/// <summary>
/// The shape of <c>shipping.DeliveryAddresses</c>, mapped only so that
/// <c>migrations add</c> emits the table. Nothing loads or saves it through
/// EF: <see cref="SqlDeliveryAddressStore"/> is the only reader and writer.
/// </summary>
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

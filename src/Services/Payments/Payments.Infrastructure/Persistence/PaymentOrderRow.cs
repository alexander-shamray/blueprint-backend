namespace Payments.Infrastructure.Persistence;

/// <summary>
/// The shape of <c>payments.PaymentOrders</c>, mapped only so that
/// <c>migrations add</c> emits the table. Nothing loads or saves it through EF:
/// <see cref="SqlPaymentOrderStore"/> is the only reader and writer.
/// </summary>
internal sealed class PaymentOrderRow
{
    public Guid OrderId { get; set; }
    public Guid? CustomerId { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }
    public DateTimeOffset? PlacedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

namespace Payments.Infrastructure.Persistence;

/// <summary>Mapped only so <c>migrations add</c> emits <see cref="SqlPaymentOrderStore"/>'s table.</summary>
internal sealed class PaymentOrderRow
{
    public Guid OrderId { get; set; }
    public Guid? CustomerId { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? Currency { get; set; }
    public DateTimeOffset? PlacedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

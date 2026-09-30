using Common.Contracts.Catalog.V1;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Common.Contracts.Payments.V1;
using Common.Contracts.Shipping.V1;

namespace Platform.IntegrationTests;

/// <summary>One sample per contract, by hand, so a contract with none fails rather than skips (§12.6).</summary>
/// <remarks>Every member distinct and non-default, or a zeroed sample hides a dropped member (§12.6).</remarks>
internal static class ContractSamples
{
    private static readonly Guid Message = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Correlation = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Order = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Customer = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Product = new("55555555-5555-5555-5555-555555555555");

    // A non-zero offset, so an options change that normalises the offset away is caught.
    private static readonly DateTimeOffset Occurred =
        new(2026, 8, 11, 9, 30, 0, TimeSpan.FromHours(2));

    private static readonly Dictionary<Type, Func<object>> Registry = new()
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
        },
        [typeof(OrderPlaced)] = () => new OrderPlaced
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            TotalAmount = 999.98m,
            Currency = "EUR",
            Lines = [new PlacedLine(Product, 2, 499.99m)]
        },
        [typeof(PlacedLine)] = () => new PlacedLine(Product, 2, 499.99m),
        [typeof(OrderConfirmed)] = () => new OrderConfirmed
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            TotalAmount = 999.98m,
            Currency = "EUR",
            Lines = [new ConfirmedLine(Product, 2, 499.99m)]
        },
        [typeof(ConfirmedLine)] = () => new ConfirmedLine(Product, 2, 499.99m),
        [typeof(OrderCancelled)] = () => new OrderCancelled
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            Reason = CancelReasons.PaymentTimeout,
            Origin = CancelOrigins.Workflow
        },
        [typeof(CancelOrder)] = () => new CancelOrder(Order, CancelReasons.OutOfStock),
        [typeof(ConfirmOrder)] = () => new ConfirmOrder(Order, "psp_ref_9f21"),
        [typeof(MarkOrderShipped)] = () => new MarkOrderShipped(Order, "TRK-99182"),
        [typeof(FlagOrderForReview)] = () =>
            new FlagOrderForReview(Order, ReviewReasons.NotDespatched),
        [typeof(StockReserved)] = () => new StockReserved
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order
        },
        [typeof(StockReservationFailed)] = () => new StockReservationFailed
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            UnavailableProductIds = [Product]
        },
        [typeof(StockReleased)] = () => new StockReleased
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order
        },
        [typeof(StockLevelChanged)] = () => new StockLevelChanged
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            ProductId = Product,
            QuantityAvailable = 17
        },
        [typeof(ReserveStock)] = () => new ReserveStock(Order, [new StockLine(Product, 2)]),
        [typeof(ReleaseStock)] = () => new ReleaseStock(Order),
        [typeof(StockLine)] = () => new StockLine(Product, 2),
        [typeof(PaymentAuthorised)] = () => new PaymentAuthorised
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reference = "psp_ref_9f21",
            Amount = 999.98m,
            Currency = "EUR"
        },
        [typeof(PaymentDeclined)] = () => new PaymentDeclined
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reason = "insufficient_funds"
        },
        [typeof(PaymentRefunded)] = () => new PaymentRefunded
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            Reference = "psp_ref_9f21",
            Amount = 999.98m,
            Currency = "EUR"
        },
        [typeof(AuthorisePayment)] = () => new AuthorisePayment(Order, 999.98m, "EUR"),
        [typeof(ShipmentDispatched)] = () => new ShipmentDispatched
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            TrackingNumber = "TRK-99182"
        },
        [typeof(ShipmentDelivered)] = () => new ShipmentDelivered
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            TrackingNumber = "TRK-99182"
        }
    };

    /// <summary>The sample for one contract, or a failure naming it, never a null the suite could skip.</summary>
    public static object Create(Type contract) =>
        Registry.TryGetValue(contract, out Func<object>? sample) ? sample()
            : throw new InvalidOperationException(
                $"No sample for the contract '{contract.FullName}'. A V1 contract's members are " +
                "required apart from any additive member §12.6 lists, so nothing can construct " +
                "one by reflection — " +
                "add an entry to ContractSamples.");

    /// <summary>The types a sample exists for, so a sample outliving its contract is caught too.</summary>
    public static IReadOnlyCollection<Type> Sampled => Registry.Keys;
}

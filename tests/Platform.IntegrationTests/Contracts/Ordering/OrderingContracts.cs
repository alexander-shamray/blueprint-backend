using Common.Contracts.Ordering.V1;
using static Platform.IntegrationTests.Contracts.SampleValues;

namespace Platform.IntegrationTests.Contracts.Ordering;

/// <summary>Ordering's samples, the commands it accepts, and the members added to live contracts (ADR-028, ADR-099).</summary>
internal sealed class OrderingContracts : ContractContext
{
    public override string Service => "Ordering";

    public override IReadOnlyDictionary<Type, Func<object>> Samples { get; } = new Dictionary<Type, Func<object>>
    {
        [typeof(OrderPlaced)] = () => new OrderPlaced
        {
            MessageId = Message,
            CorrelationId = Correlation,
            OccurredAt = Occurred,
            OrderId = Order,
            CustomerId = Customer,
            TotalAmount = 999.98m,
            Currency = "EUR",
            Lines = [new PlacedLine(Product, 2, 499.99m)],
            ErasedUnder = [Request]
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
            Lines = [new ConfirmedLine(Product, 2, 499.99m)],
            ErasedUnder = [Request]
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
            Origin = CancelOrigins.Workflow,
            ErasedUnder = [Request]
        },
        [typeof(CancelOrder)] = () => new CancelOrder(Order, CancelReasons.OutOfStock),
        [typeof(ConfirmOrder)] = () => new ConfirmOrder(Order, "psp_ref_9f21"),
        [typeof(MarkOrderShipped)] = () => new MarkOrderShipped(Order, "TRK-99182"),
        [typeof(FlagOrderForReview)] = () =>
            new FlagOrderForReview(Order, ReviewReasons.NotDespatched)
    };

    public override string[] AdditiveMembers =>
    [
        // Absent means published before the field existed, which §9.6's saga discards.
        "Common.Contracts.Ordering.V1.OrderCancelled.Origin",

        // Absent means published before the field existed, which a holder reads as erased under none (ADR-099).
        "Common.Contracts.Ordering.V1.OrderPlaced.ErasedUnder",
        "Common.Contracts.Ordering.V1.OrderConfirmed.ErasedUnder",
        "Common.Contracts.Ordering.V1.OrderCancelled.ErasedUnder"
    ];

    public override Type[] CommandRoots =>
    [
        typeof(CancelOrder),
        typeof(ConfirmOrder),
        typeof(MarkOrderShipped),
        typeof(FlagOrderForReview)
    ];

    public override Type[] EventPayloads =>
    [
        typeof(PlacedLine),         // OrderPlaced — exempt, only an event reaches it
        typeof(ConfirmedLine)       // OrderConfirmed — the same
    ];

    public override (Type Contract, string Member)[] ApprovedCommandMembers =>
    [
        (typeof(CancelOrder), "OrderId"),
        (typeof(CancelOrder), "Reason"),
        (typeof(ConfirmOrder), "OrderId"),
        (typeof(ConfirmOrder), "PaymentReference"),
        (typeof(MarkOrderShipped), "OrderId"),
        (typeof(MarkOrderShipped), "TrackingNumber"),
        (typeof(FlagOrderForReview), "OrderId"),
        (typeof(FlagOrderForReview), "Reason")
    ];
}

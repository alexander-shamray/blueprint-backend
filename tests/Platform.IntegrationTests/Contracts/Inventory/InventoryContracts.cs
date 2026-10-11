using Common.Contracts.Inventory.V1;
using static Platform.IntegrationTests.Contracts.SampleValues;

namespace Platform.IntegrationTests.Contracts.Inventory;

/// <summary>Inventory's samples, and the stock commands it accepts (ADR-028).</summary>
internal sealed class InventoryContracts : ContractContext
{
    public override string Service => "Inventory";

    public override IReadOnlyDictionary<Type, Func<object>> Samples { get; } = new Dictionary<Type, Func<object>>
    {
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
        [typeof(StockLine)] = () => new StockLine(Product, 2)
    };

    public override Type[] CommandRoots => [typeof(ReserveStock), typeof(ReleaseStock)];

    public override Type[] JudgedPayloads => [typeof(StockLine)];   // ReserveStock reaches it

    public override (Type Contract, string Member)[] ApprovedCommandMembers =>
    [
        (typeof(ReserveStock), "OrderId"),
        (typeof(ReserveStock), "Lines"),
        (typeof(ReleaseStock), "OrderId"),
        (typeof(StockLine), "ProductId"),
        (typeof(StockLine), "Quantity")
    ];
}

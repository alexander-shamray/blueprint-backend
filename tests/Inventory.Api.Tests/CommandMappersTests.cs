using Common.Application;
using Common.Contracts.Inventory.V1;
using Common.Contracts.Ordering.V1;
using Inventory.Application;
using Inventory.Application.Reservations.ReleaseStock;
using Inventory.Application.Reservations.ReserveStock;
using Inventory.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Inventory.Api.Tests;

/// <summary>
/// §9.4's wire-to-command boundary for §3.2's two Inventory commands,
/// constructed directly rather than over containers: every refusal below is
/// decided in the mapper itself, before a message ever reaches
/// <c>ValidationBehavior</c> or a dispatcher, so nothing here needs a broker,
/// a database, or <see cref="IntegrationCollection"/>.
/// </summary>
/// <remarks>
/// <b>This is the half <see cref="InventoryCommandEndpointTests"/> cannot be.</b>
/// Three of that suite's four malformed-reserve cases and its
/// <c>ReleaseStock</c> sibling are also refused by
/// <c>ReserveStockValidator</c>/<c>ReleaseStockValidator</c> — a
/// <c>ValidationException</c> from <c>ValidationBehavior</c> is a fault
/// <c>CommandConsumer</c> retries and error-queues, and it writes no row
/// either, so "no row" over the broker cannot say which of the two threw.
/// Only a test that calls the mapper on its own, past the point where a
/// validator would ever see the value, can.
/// </remarks>
public sealed class CommandMappersTests
{
    private static readonly Guid Order = Guid.CreateVersion7();
    private static readonly Guid Product = Guid.CreateVersion7();

    [Fact]
    public void A_well_formed_reserve_maps_to_a_command_with_the_same_order_and_lines()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [new StockLine(Product, 2)];

        ReserveStockCommand command = mapper.Map(new ReserveStock(Order, wire));

        command.OrderId.ShouldBe(Order);
        command.Lines.ShouldHaveSingleItem();
        command.Lines[0].ProductId.Value.ShouldBe(Product);
        command.Lines[0].Quantity.ShouldBe(2);
    }

    [Fact]
    public void Null_lines_are_refused()
    {
        ReserveStockMapper mapper = new();

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, null!)));
    }

    [Fact]
    public void Empty_lines_are_refused()
    {
        ReserveStockMapper mapper = new();

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, [])));
    }

    [Fact]
    public void A_null_line_is_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [null!];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, wire)));
    }

    [Fact]
    public void A_quantity_below_the_contract_floor_is_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [new StockLine(Product, OrderLimits.MinQuantity - 1)];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, wire)));
    }

    [Fact]
    public void A_quantity_above_the_contract_ceiling_is_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [new StockLine(Product, OrderLimits.MaxQuantity + 1)];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, wire)));
    }

    [Fact]
    public void An_empty_order_id_on_a_reserve_is_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [new StockLine(Product, 1)];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Guid.Empty, wire)));
    }

    [Fact]
    public void An_empty_product_id_is_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [new StockLine(Guid.Empty, 1)];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, wire)));
    }

    [Fact]
    public void More_lines_than_an_order_can_carry_are_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire =
        [
            .. Enumerable.Range(0, OrderLimits.MaxLines + 1)
                .Select(_ => new StockLine(Guid.CreateVersion7(), 1))
        ];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, wire)));
    }

    [Fact]
    public void A_repeated_product_is_refused()
    {
        ReserveStockMapper mapper = new();
        StockLine[] wire = [new StockLine(Product, 1), new StockLine(Product, 1)];

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReserveStock(Order, wire)));
    }

    [Fact]
    public void A_well_formed_release_maps_to_a_command_with_the_same_order_id()
    {
        ReleaseStockMapper mapper = new();

        ReleaseStockCommand command = mapper.Map(new ReleaseStock(Order));

        command.OrderId.ShouldBe(Order);
        command.Origin.ShouldBe(CommandOrigin.System);
    }

    [Fact]
    public void An_empty_order_id_on_a_release_is_refused()
    {
        ReleaseStockMapper mapper = new();

        Should.Throw<ContractMappingException>(() => mapper.Map(new ReleaseStock(Guid.Empty)));
    }
}

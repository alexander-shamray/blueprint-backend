using Common.Infrastructure.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;
using Web.Bff.Persistence;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>ADR-051's schema as the design-time model holds it, which builds with no server to reach.</summary>
public sealed class ProjectionModelTests
{
    [Fact]
    public void Every_table_is_in_the_bff_schema()
    {
        using BffDbContext db = Context();
        IModel model = db.GetService<IDesignTimeModel>().Model;

        string[] tables = [.. model.GetEntityTypes().Select(e => $"{e.GetSchema()}.{e.GetTableName()}")];

        tables.ShouldBe(["bff.InboxMessages", "bff.OrderLines", "bff.Orders", "bff.Products"], ignoreOrder: true);
    }

    [Fact]
    public void Any_order_event_can_create_an_order_row_so_only_the_key_and_the_two_instants_are_required()
    {
        using BffDbContext db = Context();
        IEntityType orders = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!;

        string[] required = [.. orders.GetProperties().Where(p => !p.IsNullable).Select(p => p.Name)];

        required.ShouldBe([nameof(OrderRow.OrderId), nameof(OrderRow.FirstSeenAt), nameof(OrderRow.AsOf)],
            ignoreOrder: true);
    }

    [Fact]
    public void The_reads_index_seeks_owned_rows_newest_first()
    {
        using BffDbContext db = Context();
        IIndex owned = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_Orders_Owned");

        owned.Properties.Select(p => p.Name).ShouldBe(
            [nameof(OrderRow.CustomerId), nameof(OrderRow.FirstSeenAt), nameof(OrderRow.OrderId)]);
        owned.IsDescending.ShouldBe([false, true, true]);
        owned.GetFilter().ShouldBe("[CustomerId] IS NOT NULL");
    }

    [Fact]
    public void The_pairs_one_handler_writes_together_the_cancel_member_and_the_payment_currency_are_constrained()
    {
        using BffDbContext db = Context();
        IEntityType orders = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!;

        orders.GetCheckConstraints().Select(c => c.ModelName).ShouldBe(
            [
                "CK_Orders_Authorisation", "CK_Orders_CancelOutcome", "CK_Orders_Cancellation",
                "CK_Orders_PaymentCurrency", "CK_Orders_Refund", "CK_Orders_Total"
            ],
            ignoreOrder: true);
    }

    [Fact]
    public void Both_currencies_are_bounded_to_an_iso_code()
    {
        using BffDbContext db = Context();
        IEntityType orders = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderRow))!;

        orders.FindProperty(nameof(OrderRow.Currency))!.GetMaxLength().ShouldBe(ProjectionLimits.CurrencyLength);
        orders.FindProperty(nameof(OrderRow.PaymentCurrency))!.GetMaxLength()
            .ShouldBe(ProjectionLimits.CurrencyLength);
    }

    [Fact]
    public void A_line_is_keyed_by_its_position_and_goes_with_its_order()
    {
        using BffDbContext db = Context();
        IEntityType lines = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(OrderLineRow))!;

        lines.FindPrimaryKey()!.Properties.Select(p => p.Name).ShouldBe(
            [nameof(OrderLineRow.OrderId), nameof(OrderLineRow.LineNumber)]);

        IForeignKey order = lines.GetForeignKeys().Single();
        order.PrincipalEntityType.ClrType.ShouldBe(typeof(OrderRow));
        order.DeleteBehavior.ShouldBe(DeleteBehavior.Cascade);
    }

    [Fact]
    public void The_inbox_is_keyed_on_message_and_endpoint()
    {
        using BffDbContext db = Context();
        IEntityType inbox = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(InboxMessage))!;

        inbox.FindPrimaryKey()!.Properties.Select(p => p.Name).ShouldBe(
            [nameof(InboxMessage.MessageId), nameof(InboxMessage.Endpoint)]);
    }

    // The provider is named so the model is SQL Server's; nothing connects to build it.
    private static BffDbContext Context() =>
        new(new DbContextOptionsBuilder<BffDbContext>().UseSqlServer("Server=model-only.invalid").Options);
}

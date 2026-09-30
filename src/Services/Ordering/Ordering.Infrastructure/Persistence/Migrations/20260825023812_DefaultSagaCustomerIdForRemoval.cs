using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The expand half of removing the saga's <c>CustomerId</c> (ADR-028): the column stays, defaulted.</summary>
/// <remarks>
/// Not <c>DROP COLUMN</c>, since the release still serving writes it (§7.4, §15.5); NOT NULL with the empty GUID,
/// which names nobody, survives both directions (§9.6).
/// </remarks>
public partial class DefaultSagaCustomerIdForRemoval : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "CustomerId",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "uniqueidentifier",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uniqueidentifier");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<Guid>(
            name: "CustomerId",
            schema: "ordering",
            table: "OrderFulfilmentStates",
            type: "uniqueidentifier",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uniqueidentifier",
            oldDefaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
    }
}

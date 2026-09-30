using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>§3.2's aggregate, generated from <see cref="ShipmentConfiguration"/> and its tracking events'.</summary>
public partial class AddShipments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Shipments",
            schema: "shipping",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                CarrierReference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                TrackingNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                UnfulfillableReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                CancellationRequestedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                CancellationRefusedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                TerminalAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                Attempts = table.Column<int>(type: "int", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                NextPollAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Shipments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "TrackingEvents",
            schema: "shipping",
            columns: table => new
            {
                ShipmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CarrierEventId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_BIN2"),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TrackingEvents", x => new { x.ShipmentId, x.CarrierEventId });
                table.ForeignKey(
                    name: "FK_TrackingEvents_Shipments_ShipmentId",
                    column: x => x.ShipmentId,
                    principalSchema: "shipping",
                    principalTable: "Shipments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Shipments_OrderId",
            schema: "shipping",
            table: "Shipments",
            column: "OrderId",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TrackingEvents",
            schema: "shipping");

        migrationBuilder.DropTable(
            name: "Shipments",
            schema: "shipping");
    }
}

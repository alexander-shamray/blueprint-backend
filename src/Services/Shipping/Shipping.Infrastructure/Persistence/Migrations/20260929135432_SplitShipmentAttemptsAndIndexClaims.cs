using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>
/// ADR-054's <c>PollAttempts</c> and one filtered index per claim, generated
/// from <see cref="ShipmentConfiguration"/>, which argues both; only this
/// file's dress and the one statement below are hand-authored, and the
/// <c>.Designer.cs</c> and snapshot are untouched.
/// </summary>
public partial class SplitShipmentAttemptsAndIndexClaims : Migration
{
    // Fields for CA1861, as AddOutbox argues.
    private static readonly string[] FulfilmentIncluded = ["Status", "CancellationRequestedAt", "LockedUntil"];
    private static readonly string[] TrackingIncluded = ["Status", "LockedUntil"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PollAttempts",
            schema: "shipping",
            table: "Shipments",
            type: "int",
            nullable: false,
            defaultValue: 0);

        // A row the carrier voided kept its poll schedule until
        // Shipment.CarrierCancelled cleared it, so the tracking index would
        // start out holding rows its claim never takes.
        migrationBuilder.Sql(
            "UPDATE shipping.Shipments SET NextPollAt = NULL " +
            "WHERE NextPollAt IS NOT NULL AND Status NOT IN ('Booked', 'Dispatched');");

        migrationBuilder.CreateIndex(
            name: "IX_Shipments_FulfilmentClaim",
            schema: "shipping",
            table: "Shipments",
            column: "NextAttemptAt",
            filter: "[Status] IN ('Pending', 'Booked') AND [CancellationRefusedAt] IS NULL")
            .Annotation("SqlServer:Include", FulfilmentIncluded);

        migrationBuilder.CreateIndex(
            name: "IX_Shipments_TrackingClaim",
            schema: "shipping",
            table: "Shipments",
            column: "NextPollAt",
            filter: "[NextPollAt] IS NOT NULL")
            .Annotation("SqlServer:Include", TrackingIncluded);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Shipments_FulfilmentClaim",
            schema: "shipping",
            table: "Shipments");

        migrationBuilder.DropIndex(
            name: "IX_Shipments_TrackingClaim",
            schema: "shipping",
            table: "Shipments");

        migrationBuilder.DropColumn(
            name: "PollAttempts",
            schema: "shipping",
            table: "Shipments");
    }
}

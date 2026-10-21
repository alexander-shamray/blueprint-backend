using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>An audit row keeps when Privacy closed its request, which the erasure check is bounded by
/// (ADR-139).</summary>
public partial class AddPersonalDataErasureClosedAt : Migration
{
    private static readonly string[] IncludedColumns = ["SubjectHash"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ClosedAt",
            schema: "notifications",
            table: "PersonalDataErasures",
            type: "datetimeoffset(7)",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_PersonalDataErasures_ClosedAt",
            schema: "notifications",
            table: "PersonalDataErasures",
            column: "ClosedAt")
            .Annotation("SqlServer:Include", IncludedColumns);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_PersonalDataErasures_ClosedAt",
            schema: "notifications",
            table: "PersonalDataErasures");

        migrationBuilder.DropColumn(
            name: "ClosedAt",
            schema: "notifications",
            table: "PersonalDataErasures");
    }
}

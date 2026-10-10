using Microsoft.EntityFrameworkCore.Migrations;

namespace Privacy.Infrastructure.Persistence.Migrations;

/// <summary>The holders' answers, and the columns a request needs to close, go overdue and be reissued (ADR-092).</summary>
public partial class AddErasureCompletions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ClosedAt",
            schema: "privacy",
            table: "ErasureRequests",
            type: "datetimeoffset(7)",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "OverdueAt",
            schema: "privacy",
            table: "ErasureRequests",
            type: "datetimeoffset(7)",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "Reissues",
            schema: "privacy",
            table: "ErasureRequests",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<string>(
            name: "SubjectHash",
            schema: "privacy",
            table: "ErasureRequests",
            type: "char(64)",
            unicode: false,
            fixedLength: true,
            maxLength: 64,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ErasureCompletions",
            schema: "privacy",
            columns: table => new
            {
                Responder = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Count = table.Column<int>(type: "int", nullable: false),
                Counted = table.Column<bool>(type: "bit", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ErasureCompletions", x => new { x.RequestId, x.Responder });
                table.ForeignKey(
                    name: "FK_ErasureCompletions_ErasureRequests_RequestId",
                    column: x => x.RequestId,
                    principalSchema: "privacy",
                    principalTable: "ErasureRequests",
                    principalColumn: "RequestId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ErasureRequests_Open",
            schema: "privacy",
            table: "ErasureRequests",
            column: "DueAt",
            filter: "[Status] = 'Open'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ErasureCompletions",
            schema: "privacy");

        migrationBuilder.DropIndex(
            name: "IX_ErasureRequests_Open",
            schema: "privacy",
            table: "ErasureRequests");

        migrationBuilder.DropColumn(
            name: "ClosedAt",
            schema: "privacy",
            table: "ErasureRequests");

        migrationBuilder.DropColumn(
            name: "OverdueAt",
            schema: "privacy",
            table: "ErasureRequests");

        migrationBuilder.DropColumn(
            name: "Reissues",
            schema: "privacy",
            table: "ErasureRequests");

        migrationBuilder.DropColumn(
            name: "SubjectHash",
            schema: "privacy",
            table: "ErasureRequests");
    }
}

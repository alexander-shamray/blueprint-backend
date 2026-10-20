using Microsoft.EntityFrameworkCore.Migrations;

namespace Privacy.Infrastructure.Persistence.Migrations;

/// <summary>An operator's changes to a request's responder set, kept with the request (ADR-136).</summary>
public partial class AddErasureAmendments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ErasureAmendments",
            schema: "privacy",
            columns: table => new
            {
                Sequence = table.Column<int>(type: "int", nullable: false),
                RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Added = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                Removed = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                AmendedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                AmendedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ErasureAmendments", x => new { x.RequestId, x.Sequence });
                table.ForeignKey(
                    name: "FK_ErasureAmendments_ErasureRequests_RequestId",
                    column: x => x.RequestId,
                    principalSchema: "privacy",
                    principalTable: "ErasureRequests",
                    principalColumn: "RequestId",
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ErasureAmendments",
            schema: "privacy");
    }
}

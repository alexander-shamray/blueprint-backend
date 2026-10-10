using Microsoft.EntityFrameworkCore.Migrations;

namespace Privacy.Infrastructure.Persistence.Migrations;

/// <summary>ADR-092's one aggregate: a subject's request, filtered-unique on the subject while it carries one.</summary>
public partial class AddErasureRequests : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ErasureRequests",
            schema: "privacy",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                RespondersCsv = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ErasureRequests", x => x.RequestId);
            });

        migrationBuilder.CreateIndex(
            name: "UX_ErasureRequests_Subject",
            schema: "privacy",
            table: "ErasureRequests",
            column: "SubjectId",
            unique: true,
            filter: "[SubjectId] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ErasureRequests",
            schema: "privacy");
    }
}

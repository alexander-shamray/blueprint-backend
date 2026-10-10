using Microsoft.EntityFrameworkCore.Migrations;

namespace Shipping.Infrastructure.Persistence.Migrations;

/// <summary>§11.7's audit table: a request, a hash, a count and a time, holding no personal data (ADR-092).</summary>
public partial class AddPersonalDataErasures : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "PersonalDataErasures",
            schema: "shipping",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SubjectHash = table.Column<string>(
                    type: "char(64)",
                    unicode: false,
                    fixedLength: true,
                    maxLength: 64,
                    nullable: false),
                Count = table.Column<int>(type: "int", nullable: false),
                ErasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonalDataErasures", x => x.RequestId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "PersonalDataErasures",
            schema: "shipping");
    }
}

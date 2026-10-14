using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>ADR-120's consent journal, and the entry each notice was decided under.</summary>
/// <remarks>
/// Additive, so a worker of the version before reads a log whose new column it never names (§7.4); its rows keep a
/// null entry, which is what a default or a requirement leaves.
/// </remarks>
public partial class AddConsentEntries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ConsentEntryId",
            schema: "notifications",
            table: "NotificationLog",
            type: "uniqueidentifier",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "ConsentEntries",
            schema: "notifications",
            columns: table => new
            {
                EntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Topic = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                Channel = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                OptedIn = table.Column<bool>(type: "bit", nullable: false),
                WrittenAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                Client = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                SupersedesEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Position = table.Column<int>(type: "int", nullable: false),
                AnonymisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConsentEntries", x => x.EntryId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_NotificationLog_ConsentEntry",
            schema: "notifications",
            table: "NotificationLog",
            column: "ConsentEntryId",
            filter: "[ConsentEntryId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ConsentEntries_SubjectPair",
            schema: "notifications",
            table: "ConsentEntries",
            columns: ["SubjectId", "Topic", "Channel", "Position"]);

        migrationBuilder.CreateIndex(
            name: "UX_ConsentEntries_FirstEntry",
            schema: "notifications",
            table: "ConsentEntries",
            columns: ["SubjectId", "Topic", "Channel"],
            unique: true,
            filter: "[SupersedesEntryId] IS NULL AND [SubjectId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "UX_ConsentEntries_Supersedes",
            schema: "notifications",
            table: "ConsentEntries",
            column: "SupersedesEntryId",
            unique: true,
            filter: "[SupersedesEntryId] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ConsentEntries",
            schema: "notifications");

        migrationBuilder.DropIndex(
            name: "IX_NotificationLog_ConsentEntry",
            schema: "notifications",
            table: "NotificationLog");

        migrationBuilder.DropColumn(
            name: "ConsentEntryId",
            schema: "notifications",
            table: "NotificationLog");
    }
}

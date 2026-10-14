using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

/// <summary>A device's push token, one row per installation of the app, which the API host writes (ADR-117).</summary>
public partial class AddDeviceInstallations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DeviceInstallations",
            schema: "notifications",
            columns: table => new
            {
                InstallationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Platform = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: true),
                AppId = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                Token = table.Column<string>(type: "varchar(1024)", unicode: false, maxLength: 1024, nullable: true),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                RegisteredAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: true),
                LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeviceInstallations", x => x.InstallationId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DeviceInstallations_LastSeenAt",
            schema: "notifications",
            table: "DeviceInstallations",
            column: "LastSeenAt");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceInstallations_Platform_AppId_Token",
            schema: "notifications",
            table: "DeviceInstallations",
            columns: ["Platform", "AppId", "Token"],
            unique: true,
            filter: "[Token] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_DeviceInstallations_SubjectId",
            schema: "notifications",
            table: "DeviceInstallations",
            column: "SubjectId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DeviceInstallations",
            schema: "notifications");
    }
}

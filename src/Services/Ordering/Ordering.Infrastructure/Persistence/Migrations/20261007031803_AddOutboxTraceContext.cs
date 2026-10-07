using Microsoft.EntityFrameworkCore.Migrations;

namespace Ordering.Infrastructure.Persistence.Migrations;

/// <summary>The outbox's staging trace (§9.4), nullable so the serving version writes rows without it (§7.4).</summary>
public partial class AddOutboxTraceContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "TraceParent",
            schema: "ordering",
            table: "OutboxMessages",
            type: "varchar(55)",
            unicode: false,
            maxLength: 55,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TraceState",
            schema: "ordering",
            table: "OutboxMessages",
            type: "varchar(512)",
            unicode: false,
            maxLength: 512,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TraceParent",
            schema: "ordering",
            table: "OutboxMessages");

        migrationBuilder.DropColumn(
            name: "TraceState",
            schema: "ordering",
            table: "OutboxMessages");
    }
}

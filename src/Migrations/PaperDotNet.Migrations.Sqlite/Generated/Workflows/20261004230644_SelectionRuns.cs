using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class SelectionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_selection",
                table: "automation_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "automation_run_items",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_run_items", x => new { x.run_id, x.item_id });
                    table.ForeignKey(
                        name: "fk_automation_run_items_automation_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "automation_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_run_items_run_id_position",
                table: "automation_run_items",
                columns: new[] { "run_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_run_items_tenant_id",
                table: "automation_run_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_run_items_tenant_id_item_id",
                table: "automation_run_items",
                columns: new[] { "tenant_id", "item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_run_items");

            migrationBuilder.DropColumn(
                name: "is_selection",
                table: "automation_runs");
        }
    }
}

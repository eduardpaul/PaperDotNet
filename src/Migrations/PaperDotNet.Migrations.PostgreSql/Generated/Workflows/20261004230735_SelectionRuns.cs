using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class SelectionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_selection",
                schema: "automation",
                table: "runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "run_items",
                schema: "automation",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_items", x => new { x.run_id, x.item_id });
                    table.ForeignKey(
                        name: "fk_run_items_runs_run_id",
                        column: x => x.run_id,
                        principalSchema: "automation",
                        principalTable: "runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_run_items_run_id_position",
                schema: "automation",
                table: "run_items",
                columns: new[] { "run_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_run_items_tenant_id",
                schema: "automation",
                table: "run_items",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_items_tenant_id_item_id",
                schema: "automation",
                table: "run_items",
                columns: new[] { "tenant_id", "item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_items",
                schema: "automation");

            migrationBuilder.DropColumn(
                name: "is_selection",
                schema: "automation",
                table: "runs");
        }
    }
}

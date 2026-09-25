using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class ItemChangeLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lists_item_changes",
                columns: table => new
                {
                    sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    scope_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_item_changes", x => x.sequence);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_changes_list_id_sequence",
                table: "lists_item_changes",
                columns: new[] { "list_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_changes_tenant_id",
                table: "lists_item_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_changes_tenant_id_at",
                table: "lists_item_changes",
                columns: new[] { "tenant_id", "at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_item_changes");
        }
    }
}

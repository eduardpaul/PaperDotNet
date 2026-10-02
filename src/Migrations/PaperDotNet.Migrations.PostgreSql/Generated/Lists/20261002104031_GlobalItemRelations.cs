using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class GlobalItemRelations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "item_relations",
                schema: "lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    second_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_relations", x => x.id);
                    table.ForeignKey(
                        name: "fk_item_relations_items_first_item_id",
                        column: x => x.first_item_id,
                        principalSchema: "lists",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_item_relations_items_second_item_id",
                        column: x => x.second_item_id,
                        principalSchema: "lists",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_first_item_id",
                schema: "lists",
                table: "item_relations",
                column: "first_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_second_item_id",
                schema: "lists",
                table: "item_relations",
                column: "second_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_tenant_id",
                schema: "lists",
                table: "item_relations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_tenant_id_first_item_id_second_item_id",
                schema: "lists",
                table: "item_relations",
                columns: new[] { "tenant_id", "first_item_id", "second_item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_tenant_id_second_item_id",
                schema: "lists",
                table: "item_relations",
                columns: new[] { "tenant_id", "second_item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_relations",
                schema: "lists");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class TypedItemRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lists_item_relations_tenant_id_first_item_id_second_item_id",
                table: "lists_item_relations");

            migrationBuilder.AddColumn<bool>(
                name: "directed",
                table: "lists_item_relations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "type_id",
                table: "lists_item_relations",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "lists_item_relationship_types",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    directed = table.Column<bool>(type: "INTEGER", nullable: false),
                    inverse_label = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    max_incoming = table.Column<int>(type: "INTEGER", nullable: true),
                    max_outgoing = table.Column<int>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lists_item_relationship_types", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_relations_tenant_id_first_item_id_second_item_id_type_id_directed",
                table: "lists_item_relations",
                columns: new[] { "tenant_id", "first_item_id", "second_item_id", "type_id", "directed" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_relationship_types_tenant_id",
                table: "lists_item_relationship_types",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lists_item_relationship_types");

            migrationBuilder.DropIndex(
                name: "ix_lists_item_relations_tenant_id_first_item_id_second_item_id_type_id_directed",
                table: "lists_item_relations");

            migrationBuilder.DropColumn(
                name: "directed",
                table: "lists_item_relations");

            migrationBuilder.DropColumn(
                name: "type_id",
                table: "lists_item_relations");

            migrationBuilder.CreateIndex(
                name: "ix_lists_item_relations_tenant_id_first_item_id_second_item_id",
                table: "lists_item_relations",
                columns: new[] { "tenant_id", "first_item_id", "second_item_id" },
                unique: true);
        }
    }
}

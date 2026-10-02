using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class TypedItemRelationships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_item_relations_tenant_id_first_item_id_second_item_id",
                schema: "lists",
                table: "item_relations");

            migrationBuilder.AddColumn<bool>(
                name: "directed",
                schema: "lists",
                table: "item_relations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "type_id",
                schema: "lists",
                table: "item_relations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "item_relationship_types",
                schema: "lists",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    directed = table.Column<bool>(type: "boolean", nullable: false),
                    inverse_label = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    max_incoming = table.Column<int>(type: "integer", nullable: true),
                    max_outgoing = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_item_relationship_types", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_tenant_id_first_item_id_second_item_id_type_",
                schema: "lists",
                table: "item_relations",
                columns: new[] { "tenant_id", "first_item_id", "second_item_id", "type_id", "directed" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_item_relationship_types_tenant_id",
                schema: "lists",
                table: "item_relationship_types",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "item_relationship_types",
                schema: "lists");

            migrationBuilder.DropIndex(
                name: "ix_item_relations_tenant_id_first_item_id_second_item_id_type_",
                schema: "lists",
                table: "item_relations");

            migrationBuilder.DropColumn(
                name: "directed",
                schema: "lists",
                table: "item_relations");

            migrationBuilder.DropColumn(
                name: "type_id",
                schema: "lists",
                table: "item_relations");

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_tenant_id_first_item_id_second_item_id",
                schema: "lists",
                table: "item_relations",
                columns: new[] { "tenant_id", "first_item_id", "second_item_id" },
                unique: true);
        }
    }
}

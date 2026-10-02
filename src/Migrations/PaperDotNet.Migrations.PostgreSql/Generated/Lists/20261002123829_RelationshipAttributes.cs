using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class RelationshipAttributes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "attributes",
                schema: "lists",
                table: "item_relations",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<long>(
                name: "version",
                schema: "lists",
                table: "item_relations",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateIndex(
                name: "ix_item_relations_attributes",
                schema: "lists",
                table: "item_relations",
                column: "attributes")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "jsonb_path_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_item_relations_attributes",
                schema: "lists",
                table: "item_relations");

            migrationBuilder.DropColumn(
                name: "attributes",
                schema: "lists",
                table: "item_relations");

            migrationBuilder.DropColumn(
                name: "version",
                schema: "lists",
                table: "item_relations");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Lists
{
    /// <inheritdoc />
    public partial class TemplatesAndSearchWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_key",
                schema: "lists",
                table: "lists",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "extension_id",
                schema: "lists",
                table: "content_types",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "key",
                schema: "lists",
                table: "content_types",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_content_types_tenant_id_key",
                schema: "lists",
                table: "content_types",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_content_types_tenant_id_key",
                schema: "lists",
                table: "content_types");

            migrationBuilder.DropColumn(
                name: "template_key",
                schema: "lists",
                table: "lists");

            migrationBuilder.DropColumn(
                name: "extension_id",
                schema: "lists",
                table: "content_types");

            migrationBuilder.DropColumn(
                name: "key",
                schema: "lists",
                table: "content_types");
        }
    }
}

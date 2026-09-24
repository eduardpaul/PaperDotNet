using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Lists
{
    /// <inheritdoc />
    public partial class TemplatesAndSearchWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "template_key",
                table: "lists_lists",
                type: "TEXT",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "extension_id",
                table: "lists_content_types",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "key",
                table: "lists_content_types",
                type: "TEXT",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_lists_content_types_tenant_id_key",
                table: "lists_content_types",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_lists_content_types_tenant_id_key",
                table: "lists_content_types");

            migrationBuilder.DropColumn(
                name: "template_key",
                table: "lists_lists");

            migrationBuilder.DropColumn(
                name: "extension_id",
                table: "lists_content_types");

            migrationBuilder.DropColumn(
                name: "key",
                table: "lists_content_types");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Taxonomy
{
    /// <inheritdoc />
    public partial class KeywordPromotionAndTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "available_as_keyword",
                schema: "taxonomy",
                table: "terms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "extension_id",
                schema: "taxonomy",
                table: "term_sets",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "key",
                schema: "taxonomy",
                table: "term_sets",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_term_sets_tenant_id_key",
                schema: "taxonomy",
                table: "term_sets",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_term_sets_tenant_id_key",
                schema: "taxonomy",
                table: "term_sets");

            migrationBuilder.DropColumn(
                name: "available_as_keyword",
                schema: "taxonomy",
                table: "terms");

            migrationBuilder.DropColumn(
                name: "extension_id",
                schema: "taxonomy",
                table: "term_sets");

            migrationBuilder.DropColumn(
                name: "key",
                schema: "taxonomy",
                table: "term_sets");
        }
    }
}

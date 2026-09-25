using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Taxonomy
{
    /// <inheritdoc />
    public partial class KeywordPromotionAndTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "available_as_keyword",
                table: "taxonomy_terms",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "extension_id",
                table: "taxonomy_term_sets",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "key",
                table: "taxonomy_term_sets",
                type: "TEXT",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_term_sets_tenant_id_key",
                table: "taxonomy_term_sets",
                columns: new[] { "tenant_id", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_taxonomy_term_sets_tenant_id_key",
                table: "taxonomy_term_sets");

            migrationBuilder.DropColumn(
                name: "available_as_keyword",
                table: "taxonomy_terms");

            migrationBuilder.DropColumn(
                name: "extension_id",
                table: "taxonomy_term_sets");

            migrationBuilder.DropColumn(
                name: "key",
                table: "taxonomy_term_sets");
        }
    }
}

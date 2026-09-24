using Microsoft.EntityFrameworkCore.Migrations;
using PaperDotNet.Persistence.Sqlite;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "search_documents",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            // SRC-05: English stemming for the FTS5 index (SQLite has no per-row languages).
            migrationBuilder.Sql(SqliteFullTextSearch.DropIndexSql("search_documents"));
            migrationBuilder.Sql(SqliteFullTextSearch.CreateIndexSql("search_documents", stemming: true, "title", "keywords", "body"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqliteFullTextSearch.DropIndexSql("search_documents"));
            migrationBuilder.Sql(SqliteFullTextSearch.CreateIndexSql("search_documents", "title", "keywords", "body"));

            migrationBuilder.DropColumn(
                name: "language",
                table: "search_documents");
        }
    }
}

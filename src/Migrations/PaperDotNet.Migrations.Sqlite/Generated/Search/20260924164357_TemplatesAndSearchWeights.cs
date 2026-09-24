using Microsoft.EntityFrameworkCore.Migrations;
using PaperDotNet.Persistence.Sqlite;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class TemplatesAndSearchWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The FTS5 table gets the new high-weight column: recreate it (and its triggers).
            migrationBuilder.Sql(SqliteFullTextSearch.DropIndexSql("search_documents"));
            migrationBuilder.AddColumn<string>(
                name: "keywords",
                table: "search_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
            migrationBuilder.Sql(SqliteFullTextSearch.CreateIndexSql("search_documents", "title", "keywords", "body"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqliteFullTextSearch.DropIndexSql("search_documents"));
            migrationBuilder.DropColumn(
                name: "keywords",
                table: "search_documents");
            migrationBuilder.Sql(SqliteFullTextSearch.CreateIndexSql("search_documents", "title", "body"));
        }
    }
}

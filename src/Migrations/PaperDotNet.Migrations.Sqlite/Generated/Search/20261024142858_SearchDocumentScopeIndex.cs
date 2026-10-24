using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchDocumentScopeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_documents_scope_id_id",
                table: "search_documents");

            migrationBuilder.CreateIndex(
                name: "ix_search_documents_scope_id",
                table: "search_documents",
                column: "scope_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_documents_scope_id",
                table: "search_documents");

            migrationBuilder.CreateIndex(
                name: "ix_search_documents_scope_id_id",
                table: "search_documents",
                columns: new[] { "scope_id", "id" });
        }
    }
}

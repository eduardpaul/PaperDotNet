using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchDocumentScopeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documents_scope_id_id",
                schema: "search",
                table: "documents");

            migrationBuilder.CreateIndex(
                name: "ix_documents_scope_id",
                schema: "search",
                table: "documents",
                column: "scope_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documents_scope_id",
                schema: "search",
                table: "documents");

            migrationBuilder.CreateIndex(
                name: "ix_documents_scope_id_id",
                schema: "search",
                table: "documents",
                columns: new[] { "scope_id", "id" });
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PaperDotNet.Persistence.Sqlite;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchPassages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_passages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    page = table.Column<int>(type: "INTEGER", nullable: true),
                    text = table.Column<string>(type: "TEXT", nullable: false),
                    language = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    content_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    embedding = table.Column<byte[]>(type: "BLOB", nullable: true),
                    embedding_model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    vector_stamp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_passages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_passages_tenant_id",
                table: "search_passages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_passages_tenant_id_document_id",
                table: "search_passages",
                columns: new[] { "tenant_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_passages_tenant_id_embedding_model_vector_stamp",
                table: "search_passages",
                columns: new[] { "tenant_id", "embedding_model", "vector_stamp" });

            migrationBuilder.Sql(SqliteFullTextSearch.CreateIndexSql("search_passages", stemming: true, "text"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqliteFullTextSearch.DropIndexSql("search_passages"));

            migrationBuilder.DropTable(
                name: "search_passages");
        }
    }
}

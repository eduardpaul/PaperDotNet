using System;
using Microsoft.EntityFrameworkCore.Migrations;
using PaperDotNet.Persistence.Sqlite;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_document_principals",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_document_principals", x => new { x.document_id, x.principal });
                });

            migrationBuilder.CreateTable(
                name: "search_document_tags",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    term_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_document_tags", x => new { x.document_id, x.term_id });
                });

            migrationBuilder.CreateTable(
                name: "search_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    container_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    content_type_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    body = table.Column<string>(type: "TEXT", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_full_text_match",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    rank = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_audit_log_entity_id",
                table: "search_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_audit_log_tenant_id",
                table: "search_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_audit_log_tenant_id_at",
                table: "search_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_search_document_principals_principal_document_id",
                table: "search_document_principals",
                columns: new[] { "principal", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_document_principals_tenant_id",
                table: "search_document_principals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_document_tags_tenant_id",
                table: "search_document_tags",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_document_tags_term_id_document_id",
                table: "search_document_tags",
                columns: new[] { "term_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_documents_tenant_id",
                table: "search_documents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_documents_tenant_id_container_id",
                table: "search_documents",
                columns: new[] { "tenant_id", "container_id" });

            // Full-text index (FTS5) over title and body, kept in sync by triggers.
            migrationBuilder.Sql(SqliteFullTextSearch.CreateIndexSql("search_documents", "title", "body"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqliteFullTextSearch.DropIndexSql("search_documents"));

            migrationBuilder.DropTable(
                name: "search_audit_log");

            migrationBuilder.DropTable(
                name: "search_document_principals");

            migrationBuilder.DropTable(
                name: "search_document_tags");

            migrationBuilder.DropTable(
                name: "search_documents");

            migrationBuilder.DropTable(
                name: "search_full_text_match");
        }
    }
}

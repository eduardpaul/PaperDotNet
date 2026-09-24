using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "search");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    properties = table.Column<List<string>>(type: "text[]", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "document_principals",
                schema: "search",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_principals", x => new { x.document_id, x.principal });
                });

            migrationBuilder.CreateTable(
                name: "document_tags",
                schema: "search",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_tags", x => new { x.document_id, x.term_id });
                });

            migrationBuilder.CreateTable(
                name: "documents",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_id = table.Column<Guid>(type: "uuid", nullable: true),
                    content_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    title = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('simple', regexp_replace(coalesce(\"title\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"body\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'B')", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "full_text_match",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    rank = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "search",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "search",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "search",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_document_principals_principal_document_id",
                schema: "search",
                table: "document_principals",
                columns: new[] { "principal", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_principals_tenant_id",
                schema: "search",
                table: "document_principals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_tags_tenant_id",
                schema: "search",
                table: "document_tags",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_tags_term_id_document_id",
                schema: "search",
                table: "document_tags",
                columns: new[] { "term_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_search_vector",
                schema: "search",
                table: "documents",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_documents_tenant_id",
                schema: "search",
                table: "documents",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_tenant_id_container_id",
                schema: "search",
                table: "documents",
                columns: new[] { "tenant_id", "container_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "search");

            migrationBuilder.DropTable(
                name: "document_principals",
                schema: "search");

            migrationBuilder.DropTable(
                name: "document_tags",
                schema: "search");

            migrationBuilder.DropTable(
                name: "documents",
                schema: "search");

            migrationBuilder.DropTable(
                name: "full_text_match",
                schema: "search");
        }
    }
}

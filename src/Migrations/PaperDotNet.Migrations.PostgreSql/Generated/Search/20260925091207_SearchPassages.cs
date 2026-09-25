using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchPassages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "passages",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    page = table.Column<int>(type: "integer", nullable: true),
                    text = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    embedding = table.Column<byte[]>(type: "bytea", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    vector_stamp = table.Column<long>(type: "bigint", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "setweight(to_tsvector('simple', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') || (CASE \"language\" WHEN 'danish' THEN setweight(to_tsvector('danish', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'dutch' THEN setweight(to_tsvector('dutch', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'english' THEN setweight(to_tsvector('english', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'finnish' THEN setweight(to_tsvector('finnish', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'french' THEN setweight(to_tsvector('french', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'german' THEN setweight(to_tsvector('german', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'hungarian' THEN setweight(to_tsvector('hungarian', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'italian' THEN setweight(to_tsvector('italian', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'norwegian' THEN setweight(to_tsvector('norwegian', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'portuguese' THEN setweight(to_tsvector('portuguese', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'romanian' THEN setweight(to_tsvector('romanian', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'russian' THEN setweight(to_tsvector('russian', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'spanish' THEN setweight(to_tsvector('spanish', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'swedish' THEN setweight(to_tsvector('swedish', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') WHEN 'turkish' THEN setweight(to_tsvector('turkish', regexp_replace(coalesce(\"text\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') ELSE ''::tsvector END)", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_passages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_passages_search_vector",
                schema: "search",
                table: "passages",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "ix_passages_tenant_id",
                schema: "search",
                table: "passages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_passages_tenant_id_document_id",
                schema: "search",
                table: "passages",
                columns: new[] { "tenant_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_passages_tenant_id_embedding_model_vector_stamp",
                schema: "search",
                table: "passages",
                columns: new[] { "tenant_id", "embedding_model", "vector_stamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "passages",
                schema: "search");
        }
    }
}

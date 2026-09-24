using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class TemplatesAndSearchWeights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "keywords",
                schema: "search",
                table: "documents",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "search",
                table: "documents",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('simple', regexp_replace(coalesce(\"title\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"keywords\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'B') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"body\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'C')",
                stored: true,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldNullable: true,
                oldComputedColumnSql: "setweight(to_tsvector('simple', regexp_replace(coalesce(\"title\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"body\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'B')",
                oldStored: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "keywords",
                schema: "search",
                table: "documents");

            migrationBuilder.AlterColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "search",
                table: "documents",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "setweight(to_tsvector('simple', regexp_replace(coalesce(\"title\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"body\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'B')",
                stored: true,
                oldClrType: typeof(NpgsqlTsVector),
                oldType: "tsvector",
                oldNullable: true,
                oldComputedColumnSql: "setweight(to_tsvector('simple', regexp_replace(coalesce(\"title\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'A') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"keywords\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'B') || setweight(to_tsvector('simple', regexp_replace(coalesce(\"body\", ''), '[[:punct:][:space:]]+', ' ', 'g')), 'C')",
                oldStored: true);
        }
    }
}

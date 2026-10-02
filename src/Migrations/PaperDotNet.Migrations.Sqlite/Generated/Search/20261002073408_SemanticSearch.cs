using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search;

/// <inheritdoc />
public partial class _20261002073408_SemanticSearch : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "Embedding",
            table: "search_passages",
            type: "BLOB",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "EmbeddingModel",
            table: "search_passages",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "VectorStamp",
            table: "search_passages",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0L);

        migrationBuilder.CreateIndex(
            name: "IX_search_passages_TenantId_EmbeddingModel_VectorStamp",
            table: "search_passages",
            columns: new[] { "TenantId", "EmbeddingModel", "VectorStamp" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_search_passages_TenantId_EmbeddingModel_VectorStamp",
            table: "search_passages");

        migrationBuilder.DropColumn(
            name: "Embedding",
            table: "search_passages");

        migrationBuilder.DropColumn(
            name: "EmbeddingModel",
            table: "search_passages");

        migrationBuilder.DropColumn(
            name: "VectorStamp",
            table: "search_passages");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search;

/// <inheritdoc />
public partial class _20261001101035_Search : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "search_documents",
            columns: table => new
            {
                Key = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                SourceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ContainerId = table.Column<Guid>(type: "TEXT", nullable: true),
                ContentTypeId = table.Column<Guid>(type: "TEXT", nullable: true),
                ScopeId = table.Column<Guid>(type: "TEXT", nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                Keywords = table.Column<string>(type: "TEXT", nullable: false),
                Body = table.Column<string>(type: "TEXT", nullable: false),
                Language = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_search_documents", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "search_passages",
            columns: table => new
            {
                Key = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                Ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                Page = table.Column<int>(type: "INTEGER", nullable: true),
                Text = table.Column<string>(type: "TEXT", nullable: false),
                ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_search_passages", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "search_tags",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                TermId = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_search_tags", x => new { x.DocumentId, x.TermId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_search_documents_Id",
            table: "search_documents",
            column: "Id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_search_documents_TenantId_ContainerId",
            table: "search_documents",
            columns: new[] { "TenantId", "ContainerId" });

        migrationBuilder.CreateIndex(
            name: "IX_search_documents_TenantId_ScopeId",
            table: "search_documents",
            columns: new[] { "TenantId", "ScopeId" });

        migrationBuilder.CreateIndex(
            name: "IX_search_documents_TenantId_SourceType",
            table: "search_documents",
            columns: new[] { "TenantId", "SourceType" });

        migrationBuilder.CreateIndex(
            name: "IX_search_passages_Id",
            table: "search_passages",
            column: "Id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_search_passages_TenantId_DocumentId",
            table: "search_passages",
            columns: new[] { "TenantId", "DocumentId" });

        migrationBuilder.CreateIndex(
            name: "IX_search_tags_TenantId_TermId",
            table: "search_tags",
            columns: new[] { "TenantId", "TermId" });

        // Full-text indexes (FTS5) on the integer keys, kept by triggers.
        migrationBuilder.Sql(FullTextIndex.Create("search_documents", "Key", "Title", "Keywords", "Body"));
        migrationBuilder.Sql(FullTextIndex.Create("search_passages", "Key", "Text"));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FullTextIndex.Drop("search_documents"));
        migrationBuilder.Sql(FullTextIndex.Drop("search_passages"));

        migrationBuilder.DropTable(
            name: "search_documents");

        migrationBuilder.DropTable(
            name: "search_passages");

        migrationBuilder.DropTable(
            name: "search_tags");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Taxonomy;

/// <inheritdoc />
public partial class _20261001074616_Taxonomy : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "term_groups",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_term_groups", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "term_sets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                IsOpen = table.Column<bool>(type: "INTEGER", nullable: false),
                IsKeywords = table.Column<bool>(type: "INTEGER", nullable: false),
                Key = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true),
                ExtensionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_term_sets", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "terms",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                TermSetId = table.Column<Guid>(type: "TEXT", nullable: false),
                ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                NormalizedName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Path = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                Description = table.Column<string>(type: "TEXT", nullable: true),
                Color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                Labels = table.Column<string>(type: "TEXT", nullable: false),
                Synonyms = table.Column<string>(type: "TEXT", nullable: false),
                SearchText = table.Column<string>(type: "TEXT", nullable: false),
                SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                IsDeprecated = table.Column<bool>(type: "INTEGER", nullable: false),
                MergedIntoId = table.Column<Guid>(type: "TEXT", nullable: true),
                AvailableAsKeyword = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_terms", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_term_groups_TenantId_Name",
            table: "term_groups",
            columns: new[] { "TenantId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_term_sets_TenantId_GroupId_Name",
            table: "term_sets",
            columns: new[] { "TenantId", "GroupId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_term_sets_TenantId_Key",
            table: "term_sets",
            columns: new[] { "TenantId", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_terms_TenantId_Path",
            table: "terms",
            columns: new[] { "TenantId", "Path" });

        migrationBuilder.CreateIndex(
            name: "IX_terms_TenantId_TermSetId_ParentId_NormalizedName",
            table: "terms",
            columns: new[] { "TenantId", "TermSetId", "ParentId", "NormalizedName" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "term_groups");

        migrationBuilder.DropTable(
            name: "term_sets");

        migrationBuilder.DropTable(
            name: "terms");
    }
}

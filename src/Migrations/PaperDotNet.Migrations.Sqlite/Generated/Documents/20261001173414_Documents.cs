using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Documents;

/// <inheritdoc />
public partial class _20261001173414_Documents : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "file_versions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Number = table.Column<int>(type: "INTEGER", nullable: false),
                IsCurrent = table.Column<bool>(type: "INTEGER", nullable: false),
                StoredFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                MediaType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                PageCount = table.Column<int>(type: "INTEGER", nullable: true),
                TextLanguage = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                Languages = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_file_versions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "group_inboxes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                GroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_group_inboxes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "library_settings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                DuplicatePolicy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                OcrLanguages = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_library_settings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "stored_file_pages",
            columns: table => new
            {
                StoredFileId = table.Column<Guid>(type: "TEXT", nullable: false),
                PageNumber = table.Column<int>(type: "INTEGER", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Text = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stored_file_pages", x => new { x.StoredFileId, x.PageNumber });
            });

        migrationBuilder.CreateTable(
            name: "stored_files",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                MediaType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastUsedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stored_files", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_file_versions_ItemId_Number",
            table: "file_versions",
            columns: new[] { "ItemId", "Number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_file_versions_TenantId_ItemId_IsCurrent",
            table: "file_versions",
            columns: new[] { "TenantId", "ItemId", "IsCurrent" });

        migrationBuilder.CreateIndex(
            name: "IX_file_versions_TenantId_Sha256_IsCurrent",
            table: "file_versions",
            columns: new[] { "TenantId", "Sha256", "IsCurrent" });

        migrationBuilder.CreateIndex(
            name: "IX_file_versions_TenantId_StoredFileId",
            table: "file_versions",
            columns: new[] { "TenantId", "StoredFileId" });

        migrationBuilder.CreateIndex(
            name: "IX_group_inboxes_TenantId_GroupId",
            table: "group_inboxes",
            columns: new[] { "TenantId", "GroupId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_library_settings_TenantId_ListId",
            table: "library_settings",
            columns: new[] { "TenantId", "ListId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_stored_file_pages_TenantId_StoredFileId",
            table: "stored_file_pages",
            columns: new[] { "TenantId", "StoredFileId" });

        migrationBuilder.CreateIndex(
            name: "IX_stored_files_TenantId_LastUsedAtUnixMs",
            table: "stored_files",
            columns: new[] { "TenantId", "LastUsedAtUnixMs" });

        migrationBuilder.CreateIndex(
            name: "IX_stored_files_TenantId_Sha256",
            table: "stored_files",
            columns: new[] { "TenantId", "Sha256" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "file_versions");

        migrationBuilder.DropTable(
            name: "group_inboxes");

        migrationBuilder.DropTable(
            name: "library_settings");

        migrationBuilder.DropTable(
            name: "stored_file_pages");

        migrationBuilder.DropTable(
            name: "stored_files");
    }
}

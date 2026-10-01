using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Calendar;

/// <inheritdoc />
public partial class _20261001072500_Calendar : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "calendar_feeds",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_calendar_feeds", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "event_occurrence_changes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                MasterItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                OriginalStartUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                OverrideItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_event_occurrence_changes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "event_recurrences",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Rule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                TimeZone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_event_recurrences", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "event_sources",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                Uid = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_event_sources", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_calendar_feeds_TenantId_SecretHash",
            table: "calendar_feeds",
            columns: new[] { "TenantId", "SecretHash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_calendar_feeds_TenantId_UserId",
            table: "calendar_feeds",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_event_occurrence_changes_TenantId_ListId",
            table: "event_occurrence_changes",
            columns: new[] { "TenantId", "ListId" });

        migrationBuilder.CreateIndex(
            name: "IX_event_occurrence_changes_TenantId_MasterItemId_OriginalStartUnixMs",
            table: "event_occurrence_changes",
            columns: new[] { "TenantId", "MasterItemId", "OriginalStartUnixMs" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_event_occurrence_changes_TenantId_OverrideItemId",
            table: "event_occurrence_changes",
            columns: new[] { "TenantId", "OverrideItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_event_recurrences_TenantId_ItemId",
            table: "event_recurrences",
            columns: new[] { "TenantId", "ItemId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_event_recurrences_TenantId_ListId",
            table: "event_recurrences",
            columns: new[] { "TenantId", "ListId" });

        migrationBuilder.CreateIndex(
            name: "IX_event_sources_TenantId_ItemId",
            table: "event_sources",
            columns: new[] { "TenantId", "ItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_event_sources_TenantId_ListId_Uid",
            table: "event_sources",
            columns: new[] { "TenantId", "ListId", "Uid" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "calendar_feeds");

        migrationBuilder.DropTable(
            name: "event_occurrence_changes");

        migrationBuilder.DropTable(
            name: "event_recurrences");

        migrationBuilder.DropTable(
            name: "event_sources");
    }
}

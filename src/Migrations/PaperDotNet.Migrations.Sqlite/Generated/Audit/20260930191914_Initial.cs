using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Audit;

/// <inheritdoc />
public partial class _20260930191914_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_entries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                Summary = table.Column<string>(type: "TEXT", nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_entries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_entries_TenantId_Id",
            table: "audit_entries",
            columns: new[] { "TenantId", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_entries_TenantId_TargetId",
            table: "audit_entries",
            columns: new[] { "TenantId", "TargetId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_entries");
    }
}

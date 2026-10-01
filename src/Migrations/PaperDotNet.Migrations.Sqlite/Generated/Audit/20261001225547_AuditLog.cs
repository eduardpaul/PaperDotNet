using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Audit;

/// <inheritdoc />
public partial class _20261001225547_AuditLog : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "audit_log",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                AtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                Action = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                EntityType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                EntityId = table.Column<Guid>(type: "TEXT", nullable: true),
                Properties = table.Column<string>(type: "TEXT", nullable: true),
                TraceId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_log", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_audit_log_TenantId_EntityId",
            table: "audit_log",
            columns: new[] { "TenantId", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_log_TenantId_Id",
            table: "audit_log",
            columns: new[] { "TenantId", "Id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_log");
    }
}

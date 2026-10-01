using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.ExtensionHost;

/// <inheritdoc />
public partial class _20260930231302_Extensions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_extensions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ExtensionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                Settings = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_extensions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tenant_extensions_TenantId_ExtensionId",
            table: "tenant_extensions",
            columns: new[] { "TenantId", "ExtensionId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenant_extensions");
    }
}

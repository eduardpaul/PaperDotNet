using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Provisioning;

/// <inheritdoc />
public partial class _20261001114358_Portability : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "portability_packages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                BlobKey = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_portability_packages", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_portability_packages_TenantId_CreatedBy_CreatedAtUnixMs",
            table: "portability_packages",
            columns: new[] { "TenantId", "CreatedBy", "CreatedAtUnixMs" });

        migrationBuilder.CreateIndex(
            name: "IX_portability_packages_TenantId_ExpiresAtUnixMs",
            table: "portability_packages",
            columns: new[] { "TenantId", "ExpiresAtUnixMs" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "portability_packages");
    }
}

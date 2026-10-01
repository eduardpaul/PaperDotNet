using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity;

/// <inheritdoc />
public partial class _20261001232459_TenantHosts : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_hosts",
            columns: table => new
            {
                Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_hosts", x => x.Host);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tenant_hosts_TenantId",
            table: "tenant_hosts",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenant_hosts");
    }
}

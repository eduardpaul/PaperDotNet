using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity;

/// <inheritdoc />
public partial class _20260930191905_Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenants",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Identifier = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", x => x.Id);
                table.ForeignKey(
                    name: "FK_users_tenants_TenantId",
                    column: x => x.TenantId,
                    principalTable: "tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tenants_Identifier",
            table: "tenants",
            column: "Identifier",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_users_TenantId_NormalizedUserName",
            table: "users",
            columns: new[] { "TenantId", "NormalizedUserName" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "users");

        migrationBuilder.DropTable(
            name: "tenants");
    }
}

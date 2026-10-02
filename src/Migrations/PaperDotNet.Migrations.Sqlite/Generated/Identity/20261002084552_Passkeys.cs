using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity;

/// <inheritdoc />
public partial class _20261002084552_Passkeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "user_passkeys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                CredentialId = table.Column<string>(type: "TEXT", maxLength: 1400, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                SignCount = table.Column<long>(type: "INTEGER", nullable: false),
                Transports = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                IsUserVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                IsBackupEligible = table.Column<bool>(type: "INTEGER", nullable: false),
                IsBackedUp = table.Column<bool>(type: "INTEGER", nullable: false),
                AttestationObject = table.Column<byte[]>(type: "BLOB", nullable: false),
                ClientDataJson = table.Column<byte[]>(type: "BLOB", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user_passkeys", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_user_passkeys_TenantId_CredentialId",
            table: "user_passkeys",
            columns: new[] { "TenantId", "CredentialId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_passkeys_TenantId_UserId",
            table: "user_passkeys",
            columns: new[] { "TenantId", "UserId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "user_passkeys");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity;

/// <inheritdoc />
public partial class _20261002083047_OAuthServer : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsServiceAccount",
            table: "users",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "oauth_clients",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                ClientId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ClientType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                GrantTypes = table.Column<string>(type: "TEXT", nullable: false),
                Scopes = table.Column<string>(type: "TEXT", nullable: false),
                RedirectUris = table.Column<string>(type: "TEXT", nullable: false),
                PostLogoutRedirectUris = table.Column<string>(type: "TEXT", nullable: false),
                ServiceUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_oauth_clients", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "oauth_codes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ClientId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                RedirectUri = table.Column<string>(type: "TEXT", nullable: false),
                CodeChallenge = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Scope = table.Column<string>(type: "TEXT", nullable: false),
                Nonce = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                SecurityStamp = table.Column<string>(type: "TEXT", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_oauth_codes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "server_keys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                Use = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                ProtectedKey = table.Column<string>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_server_keys", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_oauth_clients_TenantId_ClientId",
            table: "oauth_clients",
            columns: new[] { "TenantId", "ClientId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_oauth_codes_CodeHash",
            table: "oauth_codes",
            column: "CodeHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_oauth_codes_ExpiresAt",
            table: "oauth_codes",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_server_keys_Use",
            table: "server_keys",
            column: "Use");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "oauth_clients");

        migrationBuilder.DropTable(
            name: "oauth_codes");

        migrationBuilder.DropTable(
            name: "server_keys");

        migrationBuilder.DropColumn(
            name: "IsServiceAccount",
            table: "users");
    }
}

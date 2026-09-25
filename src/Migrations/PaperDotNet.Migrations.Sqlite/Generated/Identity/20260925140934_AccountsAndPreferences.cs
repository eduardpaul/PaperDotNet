using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity
{
    /// <inheritdoc />
    public partial class AccountsAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "deleted_at",
                table: "identity_users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "identity_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    language = table.Column<string>(type: "TEXT", maxLength: 35, nullable: true),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    date_format = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    time_format = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    number_format = table.Column<string>(type: "TEXT", maxLength: 35, nullable: true),
                    theme = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    document_languages = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_preferences", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_preferences_tenant_id",
                table: "identity_preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_preferences_tenant_id_user_id",
                table: "identity_preferences",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_preferences");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "identity_users");
        }
    }
}

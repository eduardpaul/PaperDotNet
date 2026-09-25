using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Identity
{
    /// <inheritdoc />
    public partial class AccountsAndPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "preferences",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    date_format = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    time_format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    number_format = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    theme = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    document_languages = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_preferences", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_preferences_tenant_id",
                schema: "identity",
                table: "preferences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_preferences_tenant_id_user_id",
                schema: "identity",
                table: "preferences",
                columns: new[] { "tenant_id", "user_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "preferences",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                schema: "identity",
                table: "users");
        }
    }
}

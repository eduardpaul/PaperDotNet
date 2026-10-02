using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Calendar
{
    /// <inheritdoc />
    public partial class CalendarSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_calendar_sources_list_id_uid",
                table: "calendar_sources");

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                table: "calendar_sources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "calendar_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    protected_url = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    url_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    paused = table.Column<bool>(type: "INTEGER", nullable: false),
                    last_success = table.Column<long>(type: "INTEGER", nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    created = table.Column<int>(type: "INTEGER", nullable: false),
                    updated = table.Column<int>(type: "INTEGER", nullable: false),
                    removed = table.Column<int>(type: "INTEGER", nullable: false),
                    http_e_tag = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    http_last_modified = table.Column<long>(type: "INTEGER", nullable: true),
                    lease_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    lease_until = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_list_id_uid",
                table: "calendar_sources",
                columns: new[] { "list_id", "uid" },
                unique: true,
                filter: "\"subscription_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_subscription_id_uid",
                table: "calendar_sources",
                columns: new[] { "subscription_id", "uid" },
                unique: true,
                filter: "\"subscription_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_subscriptions_list_id_url_hash",
                table: "calendar_subscriptions",
                columns: new[] { "list_id", "url_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_subscriptions_tenant_id",
                table: "calendar_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_subscriptions_workspace_id",
                table: "calendar_subscriptions",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_calendar_sources_list_id_uid",
                table: "calendar_sources");

            migrationBuilder.DropIndex(
                name: "ix_calendar_sources_subscription_id_uid",
                table: "calendar_sources");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                table: "calendar_sources");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_list_id_uid",
                table: "calendar_sources",
                columns: new[] { "list_id", "uid" },
                unique: true);
        }
    }
}

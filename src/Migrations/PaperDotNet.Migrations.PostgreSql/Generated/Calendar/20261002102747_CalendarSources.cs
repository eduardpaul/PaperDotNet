using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Calendar
{
    /// <inheritdoc />
    public partial class CalendarSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_sources_list_id_uid",
                schema: "calendar",
                table: "sources");

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                schema: "calendar",
                table: "sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "calendar",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    protected_url = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    url_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    paused = table.Column<bool>(type: "boolean", nullable: false),
                    last_success = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    removed = table.Column<int>(type: "integer", nullable: false),
                    http_e_tag = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    http_last_modified = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sources_list_id_uid",
                schema: "calendar",
                table: "sources",
                columns: new[] { "list_id", "uid" },
                unique: true,
                filter: "\"subscription_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sources_subscription_id_uid",
                schema: "calendar",
                table: "sources",
                columns: new[] { "subscription_id", "uid" },
                unique: true,
                filter: "\"subscription_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_list_id_url_hash",
                schema: "calendar",
                table: "subscriptions",
                columns: new[] { "list_id", "url_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tenant_id",
                schema: "calendar",
                table: "subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_workspace_id",
                schema: "calendar",
                table: "subscriptions",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "calendar");

            migrationBuilder.DropIndex(
                name: "ix_sources_list_id_uid",
                schema: "calendar",
                table: "sources");

            migrationBuilder.DropIndex(
                name: "ix_sources_subscription_id_uid",
                schema: "calendar",
                table: "sources");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                schema: "calendar",
                table: "sources");

            migrationBuilder.CreateIndex(
                name: "ix_sources_list_id_uid",
                schema: "calendar",
                table: "sources",
                columns: new[] { "list_id", "uid" },
                unique: true);
        }
    }
}

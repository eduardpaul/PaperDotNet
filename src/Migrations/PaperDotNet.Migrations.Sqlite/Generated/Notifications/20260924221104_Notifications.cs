using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Notifications
{
    /// <inheritdoc />
    public partial class Notifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications_digest_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    change = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_digest_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    body = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    deduplication_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    in_inbox = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    read_at = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_notifications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    channels = table.Column<string>(type: "TEXT", nullable: false),
                    webhook_url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    webhook_secret = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    quiet_hours_start = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    quiet_hours_end = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    digest_hour = table.Column<int>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_subscriptions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications_webhook_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    notification_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: false),
                    delivered_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_webhook_deliveries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_audit_log_entity_id",
                table: "notifications_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_audit_log_tenant_id",
                table: "notifications_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_audit_log_tenant_id_at",
                table: "notifications_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_digest_entries_tenant_id",
                table: "notifications_digest_entries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_digest_entries_user_id",
                table: "notifications_digest_entries",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_notifications_tenant_id",
                table: "notifications_notifications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_notifications_user_id_deduplication_key",
                table: "notifications_notifications",
                columns: new[] { "user_id", "deduplication_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_notifications_user_id_in_inbox_read_at",
                table: "notifications_notifications",
                columns: new[] { "user_id", "in_inbox", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_settings_tenant_id",
                table: "notifications_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_settings_user_id",
                table: "notifications_settings",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_subscriptions_list_id_item_id",
                table: "notifications_subscriptions",
                columns: new[] { "list_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_subscriptions_tenant_id",
                table: "notifications_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_subscriptions_user_id_list_id_item_id",
                table: "notifications_subscriptions",
                columns: new[] { "user_id", "list_id", "item_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_webhook_deliveries_status_next_attempt_at",
                table: "notifications_webhook_deliveries",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_webhook_deliveries_tenant_id",
                table: "notifications_webhook_deliveries",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications_audit_log");

            migrationBuilder.DropTable(
                name: "notifications_digest_entries");

            migrationBuilder.DropTable(
                name: "notifications_notifications");

            migrationBuilder.DropTable(
                name: "notifications_settings");

            migrationBuilder.DropTable(
                name: "notifications_subscriptions");

            migrationBuilder.DropTable(
                name: "notifications_webhook_deliveries");
        }
    }
}

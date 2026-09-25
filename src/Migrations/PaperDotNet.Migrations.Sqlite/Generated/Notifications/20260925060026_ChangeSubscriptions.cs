using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Notifications
{
    /// <inheritdoc />
    public partial class ChangeSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications_change_deliveries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    subscription_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    change_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    occurred_at = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    next_attempt_at = table.Column<long>(type: "INTEGER", nullable: false),
                    delivered_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_change_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notifications_change_subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    change_types = table.Column<string>(type: "TEXT", nullable: false),
                    notification_url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    client_state = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    secret = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications_change_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_change_deliveries_subscription_id_event_id",
                table: "notifications_change_deliveries",
                columns: new[] { "subscription_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_change_deliveries_tenant_id",
                table: "notifications_change_deliveries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_change_deliveries_tenant_id_status_next_attempt_at",
                table: "notifications_change_deliveries",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_change_subscriptions_tenant_id",
                table: "notifications_change_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_change_subscriptions_tenant_id_list_id_expires_at",
                table: "notifications_change_subscriptions",
                columns: new[] { "tenant_id", "list_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_change_subscriptions_tenant_id_user_id",
                table: "notifications_change_subscriptions",
                columns: new[] { "tenant_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications_change_deliveries");

            migrationBuilder.DropTable(
                name: "notifications_change_subscriptions");
        }
    }
}

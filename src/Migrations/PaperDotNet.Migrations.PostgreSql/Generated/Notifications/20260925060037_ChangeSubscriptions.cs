using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Notifications
{
    /// <inheritdoc />
    public partial class ChangeSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_deliveries",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    change_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_deliveries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "change_subscriptions",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    change_types = table.Column<List<string>>(type: "text[]", nullable: false),
                    notification_url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    client_state = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    secret = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_change_subscriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_change_deliveries_subscription_id_event_id",
                schema: "notifications",
                table: "change_deliveries",
                columns: new[] { "subscription_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_change_deliveries_tenant_id",
                schema: "notifications",
                table: "change_deliveries",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_deliveries_tenant_id_status_next_attempt_at",
                schema: "notifications",
                table: "change_deliveries",
                columns: new[] { "tenant_id", "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_subscriptions_tenant_id",
                schema: "notifications",
                table: "change_subscriptions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_change_subscriptions_tenant_id_list_id_expires_at",
                schema: "notifications",
                table: "change_subscriptions",
                columns: new[] { "tenant_id", "list_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_change_subscriptions_tenant_id_user_id",
                schema: "notifications",
                table: "change_subscriptions",
                columns: new[] { "tenant_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_deliveries",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "change_subscriptions",
                schema: "notifications");
        }
    }
}

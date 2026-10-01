using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Notifications;

/// <inheritdoc />
public partial class _20261001053040_Notifications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "change_deliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                SubscriptionId = table.Column<Guid>(type: "TEXT", nullable: false),
                EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                ChangeType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_change_deliveries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "change_subscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                ChangeTypes = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                NotificationUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                ClientState = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                Secret = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_change_subscriptions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "digest_entries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                Change = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_digest_entries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "follows",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: false),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_follows", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "notification_settings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Channels = table.Column<string>(type: "TEXT", nullable: false),
                WebhookUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                WebhookSecret = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                QuietHoursStart = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                QuietHoursEnd = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                DigestHour = table.Column<int>(type: "INTEGER", nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_settings", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "notifications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                Body = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: true),
                ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                DeduplicationKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                InInbox = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                ReadAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notifications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "webhook_deliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                NotificationId = table.Column<Guid>(type: "TEXT", nullable: false),
                UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_change_deliveries_TenantId_Status",
            table: "change_deliveries",
            columns: new[] { "TenantId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_change_deliveries_TenantId_SubscriptionId_EventId",
            table: "change_deliveries",
            columns: new[] { "TenantId", "SubscriptionId", "EventId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_change_subscriptions_TenantId_ListId",
            table: "change_subscriptions",
            columns: new[] { "TenantId", "ListId" });

        migrationBuilder.CreateIndex(
            name: "IX_change_subscriptions_TenantId_UserId",
            table: "change_subscriptions",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_digest_entries_TenantId_UserId",
            table: "digest_entries",
            columns: new[] { "TenantId", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_follows_TenantId_ListId_ItemId",
            table: "follows",
            columns: new[] { "TenantId", "ListId", "ItemId" });

        migrationBuilder.CreateIndex(
            name: "IX_follows_TenantId_UserId_ListId_ItemId",
            table: "follows",
            columns: new[] { "TenantId", "UserId", "ListId", "ItemId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_notification_settings_TenantId_UserId",
            table: "notification_settings",
            columns: new[] { "TenantId", "UserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_notifications_TenantId_UserId_DeduplicationKey",
            table: "notifications",
            columns: new[] { "TenantId", "UserId", "DeduplicationKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_notifications_TenantId_UserId_InInbox_ReadAt",
            table: "notifications",
            columns: new[] { "TenantId", "UserId", "InInbox", "ReadAt" });

        migrationBuilder.CreateIndex(
            name: "IX_webhook_deliveries_TenantId_NotificationId",
            table: "webhook_deliveries",
            columns: new[] { "TenantId", "NotificationId" });

        migrationBuilder.CreateIndex(
            name: "IX_webhook_deliveries_TenantId_Status",
            table: "webhook_deliveries",
            columns: new[] { "TenantId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "change_deliveries");

        migrationBuilder.DropTable(
            name: "change_subscriptions");

        migrationBuilder.DropTable(
            name: "digest_entries");

        migrationBuilder.DropTable(
            name: "follows");

        migrationBuilder.DropTable(
            name: "notification_settings");

        migrationBuilder.DropTable(
            name: "notifications");

        migrationBuilder.DropTable(
            name: "webhook_deliveries");
    }
}

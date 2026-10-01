CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "change_deliveries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_change_deliveries" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "SubscriptionId" TEXT NOT NULL,
    "EventId" TEXT NOT NULL,
    "ChangeType" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "OccurredAt" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "Attempts" INTEGER NOT NULL,
    "NextAttemptAt" TEXT NOT NULL,
    "DeliveredAt" TEXT NULL,
    "LastError" TEXT NULL
);

CREATE TABLE "change_subscriptions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_change_subscriptions" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NULL,
    "ChangeTypes" TEXT NOT NULL,
    "NotificationUrl" TEXT NOT NULL,
    "ClientState" TEXT NULL,
    "Secret" TEXT NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "digest_entries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_digest_entries" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "Change" TEXT NOT NULL,
    "Title" TEXT NULL,
    "At" TEXT NOT NULL
);

CREATE TABLE "follows" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_follows" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NULL,
    "Frequency" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "notification_settings" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_notification_settings" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Channels" TEXT NOT NULL,
    "WebhookUrl" TEXT NULL,
    "WebhookSecret" TEXT NULL,
    "QuietHoursStart" TEXT NULL,
    "QuietHoursEnd" TEXT NULL,
    "DigestHour" INTEGER NOT NULL,
    "Version" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "notifications" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_notifications" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Body" TEXT NULL,
    "WorkspaceId" TEXT NULL,
    "ListId" TEXT NULL,
    "ItemId" TEXT NULL,
    "DeduplicationKey" TEXT NULL,
    "InInbox" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ReadAt" TEXT NULL
);

CREATE TABLE "webhook_deliveries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_webhook_deliveries" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "NotificationId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "Attempts" INTEGER NOT NULL,
    "NextAttemptAt" TEXT NOT NULL,
    "DeliveredAt" TEXT NULL,
    "LastError" TEXT NULL
);

CREATE INDEX "IX_change_deliveries_TenantId_Status" ON "change_deliveries" ("TenantId", "Status");

CREATE UNIQUE INDEX "IX_change_deliveries_TenantId_SubscriptionId_EventId" ON "change_deliveries" ("TenantId", "SubscriptionId", "EventId");

CREATE INDEX "IX_change_subscriptions_TenantId_ListId" ON "change_subscriptions" ("TenantId", "ListId");

CREATE INDEX "IX_change_subscriptions_TenantId_UserId" ON "change_subscriptions" ("TenantId", "UserId");

CREATE INDEX "IX_digest_entries_TenantId_UserId" ON "digest_entries" ("TenantId", "UserId");

CREATE INDEX "IX_follows_TenantId_ListId_ItemId" ON "follows" ("TenantId", "ListId", "ItemId");

CREATE UNIQUE INDEX "IX_follows_TenantId_UserId_ListId_ItemId" ON "follows" ("TenantId", "UserId", "ListId", "ItemId");

CREATE UNIQUE INDEX "IX_notification_settings_TenantId_UserId" ON "notification_settings" ("TenantId", "UserId");

CREATE UNIQUE INDEX "IX_notifications_TenantId_UserId_DeduplicationKey" ON "notifications" ("TenantId", "UserId", "DeduplicationKey");

CREATE INDEX "IX_notifications_TenantId_UserId_InInbox_ReadAt" ON "notifications" ("TenantId", "UserId", "InInbox", "ReadAt");

CREATE INDEX "IX_webhook_deliveries_TenantId_NotificationId" ON "webhook_deliveries" ("TenantId", "NotificationId");

CREATE INDEX "IX_webhook_deliveries_TenantId_Status" ON "webhook_deliveries" ("TenantId", "Status");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001053040_Notifications', '11.0.0-rc.1.26425.128');


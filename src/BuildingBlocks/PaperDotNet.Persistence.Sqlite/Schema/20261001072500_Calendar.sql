CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "calendar_feeds" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_calendar_feeds" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "WorkspaceId" TEXT NULL,
    "ListId" TEXT NULL,
    "SecretHash" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "event_occurrence_changes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_event_occurrence_changes" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "MasterItemId" TEXT NOT NULL,
    "OriginalStartUnixMs" INTEGER NOT NULL,
    "OverrideItemId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "event_recurrences" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_event_recurrences" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Rule" TEXT NOT NULL,
    "TimeZone" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "event_sources" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_event_sources" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Uid" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL
);

CREATE UNIQUE INDEX "IX_calendar_feeds_TenantId_SecretHash" ON "calendar_feeds" ("TenantId", "SecretHash");

CREATE INDEX "IX_calendar_feeds_TenantId_UserId" ON "calendar_feeds" ("TenantId", "UserId");

CREATE INDEX "IX_event_occurrence_changes_TenantId_ListId" ON "event_occurrence_changes" ("TenantId", "ListId");

CREATE UNIQUE INDEX "IX_event_occurrence_changes_TenantId_MasterItemId_OriginalStartUnixMs" ON "event_occurrence_changes" ("TenantId", "MasterItemId", "OriginalStartUnixMs");

CREATE INDEX "IX_event_occurrence_changes_TenantId_OverrideItemId" ON "event_occurrence_changes" ("TenantId", "OverrideItemId");

CREATE UNIQUE INDEX "IX_event_recurrences_TenantId_ItemId" ON "event_recurrences" ("TenantId", "ItemId");

CREATE INDEX "IX_event_recurrences_TenantId_ListId" ON "event_recurrences" ("TenantId", "ListId");

CREATE INDEX "IX_event_sources_TenantId_ItemId" ON "event_sources" ("TenantId", "ItemId");

CREATE UNIQUE INDEX "IX_event_sources_TenantId_ListId_Uid" ON "event_sources" ("TenantId", "ListId", "Uid");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001072500_Calendar', '11.0.0-rc.1.26425.128');


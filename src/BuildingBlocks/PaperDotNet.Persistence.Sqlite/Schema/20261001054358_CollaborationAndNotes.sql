CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "comments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_comments" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "ParentId" TEXT NULL,
    "Text" TEXT NOT NULL,
    "Mentions" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "item_activity" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_item_activity" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "ActorId" TEXT NULL,
    "Summary" TEXT NULL,
    "ChangedFields" TEXT NOT NULL,
    "DeduplicationKey" TEXT NULL,
    "At" TEXT NOT NULL,
    "AtUnixMs" INTEGER NOT NULL
);

CREATE INDEX "IX_comments_TenantId_ItemId" ON "comments" ("TenantId", "ItemId");

CREATE INDEX "IX_comments_TenantId_ParentId" ON "comments" ("TenantId", "ParentId");

CREATE UNIQUE INDEX "IX_item_activity_TenantId_DeduplicationKey" ON "item_activity" ("TenantId", "DeduplicationKey");

CREATE INDEX "IX_item_activity_TenantId_ItemId_AtUnixMs" ON "item_activity" ("TenantId", "ItemId", "AtUnixMs");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001054358_CollaborationAndNotes', '11.0.0-rc.1.26425.128');


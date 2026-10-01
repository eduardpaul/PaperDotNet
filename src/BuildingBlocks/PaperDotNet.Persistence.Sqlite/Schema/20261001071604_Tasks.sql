CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "task_checklists" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_task_checklists" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    "Text" TEXT NOT NULL,
    "Done" INTEGER NOT NULL
);

CREATE TABLE "task_links" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_task_links" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "TargetItemId" TEXT NOT NULL,
    "TargetWorkspaceId" TEXT NOT NULL,
    "TargetListId" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "task_recurrences" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_task_recurrences" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Rule" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE INDEX "IX_task_checklists_TenantId_ItemId_Position" ON "task_checklists" ("TenantId", "ItemId", "Position");

CREATE UNIQUE INDEX "IX_task_links_TenantId_ItemId_TargetItemId_Kind" ON "task_links" ("TenantId", "ItemId", "TargetItemId", "Kind");

CREATE INDEX "IX_task_links_TenantId_TargetItemId_Kind" ON "task_links" ("TenantId", "TargetItemId", "Kind");

CREATE UNIQUE INDEX "IX_task_recurrences_TenantId_ItemId" ON "task_recurrences" ("TenantId", "ItemId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001071604_Tasks', '11.0.0-rc.1.26425.128');


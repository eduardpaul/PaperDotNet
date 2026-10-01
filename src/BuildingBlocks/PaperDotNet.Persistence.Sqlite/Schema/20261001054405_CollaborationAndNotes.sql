CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "note_links" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_note_links" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "SourceItemId" TEXT NOT NULL,
    "SourceListId" TEXT NOT NULL,
    "Ordinal" INTEGER NOT NULL,
    "Target" TEXT NOT NULL,
    "NormalizedTarget" TEXT NOT NULL,
    "Heading" TEXT NULL,
    "Alias" TEXT NULL,
    "Embed" INTEGER NOT NULL,
    "TargetItemId" TEXT NULL
);

CREATE TABLE "notes" (
    "ItemId" TEXT NOT NULL CONSTRAINT "PK_notes" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "NormalizedTitle" TEXT NOT NULL
);

CREATE INDEX "IX_note_links_TenantId_SourceItemId" ON "note_links" ("TenantId", "SourceItemId");

CREATE INDEX "IX_note_links_TenantId_TargetItemId" ON "note_links" ("TenantId", "TargetItemId");

CREATE INDEX "IX_note_links_TenantId_WorkspaceId_NormalizedTarget" ON "note_links" ("TenantId", "WorkspaceId", "NormalizedTarget");

CREATE INDEX "IX_notes_TenantId_WorkspaceId_NormalizedTitle" ON "notes" ("TenantId", "WorkspaceId", "NormalizedTitle");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001054405_CollaborationAndNotes', '11.0.0-rc.1.26425.128');


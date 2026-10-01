CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "file_versions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_file_versions" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "Number" INTEGER NOT NULL,
    "IsCurrent" INTEGER NOT NULL,
    "StoredFileId" TEXT NOT NULL,
    "Sha256" TEXT NOT NULL,
    "Size" INTEGER NOT NULL,
    "MediaType" TEXT NOT NULL,
    "FileName" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "PageCount" INTEGER NULL,
    "TextLanguage" TEXT NULL,
    "Languages" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "group_inboxes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_group_inboxes" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "GroupId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "library_settings" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_library_settings" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "DuplicatePolicy" TEXT NOT NULL,
    "OcrLanguages" TEXT NULL,
    "Version" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE TABLE "stored_file_pages" (
    "StoredFileId" TEXT NOT NULL,
    "PageNumber" INTEGER NOT NULL,
    "TenantId" TEXT NOT NULL,
    "Text" TEXT NOT NULL,
    CONSTRAINT "PK_stored_file_pages" PRIMARY KEY ("StoredFileId", "PageNumber")
);

CREATE TABLE "stored_files" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_stored_files" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Sha256" TEXT NOT NULL,
    "Size" INTEGER NOT NULL,
    "MediaType" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "LastUsedAtUnixMs" INTEGER NOT NULL
);

CREATE UNIQUE INDEX "IX_file_versions_ItemId_Number" ON "file_versions" ("ItemId", "Number");

CREATE INDEX "IX_file_versions_TenantId_ItemId_IsCurrent" ON "file_versions" ("TenantId", "ItemId", "IsCurrent");

CREATE INDEX "IX_file_versions_TenantId_Sha256_IsCurrent" ON "file_versions" ("TenantId", "Sha256", "IsCurrent");

CREATE INDEX "IX_file_versions_TenantId_StoredFileId" ON "file_versions" ("TenantId", "StoredFileId");

CREATE UNIQUE INDEX "IX_group_inboxes_TenantId_GroupId" ON "group_inboxes" ("TenantId", "GroupId");

CREATE UNIQUE INDEX "IX_library_settings_TenantId_ListId" ON "library_settings" ("TenantId", "ListId");

CREATE INDEX "IX_stored_file_pages_TenantId_StoredFileId" ON "stored_file_pages" ("TenantId", "StoredFileId");

CREATE INDEX "IX_stored_files_TenantId_LastUsedAtUnixMs" ON "stored_files" ("TenantId", "LastUsedAtUnixMs");

CREATE UNIQUE INDEX "IX_stored_files_TenantId_Sha256" ON "stored_files" ("TenantId", "Sha256");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001173414_Documents', '11.0.0-rc.1.26425.128');


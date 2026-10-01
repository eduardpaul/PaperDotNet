CREATE TABLE "smart_folders" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_smart_folders" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "WorkspaceId" TEXT NULL,
    "OwnerId" TEXT NULL,
    "Definition" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE INDEX "IX_smart_folders_TenantId_OwnerId" ON "smart_folders" ("TenantId", "OwnerId");

CREATE INDEX "IX_smart_folders_TenantId_WorkspaceId" ON "smart_folders" ("TenantId", "WorkspaceId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001103823_SmartFolders', '11.0.0-rc.1.26425.128');


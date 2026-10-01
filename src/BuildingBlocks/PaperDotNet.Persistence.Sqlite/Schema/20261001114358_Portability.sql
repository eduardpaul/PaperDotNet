CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "portability_packages" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_portability_packages" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "WorkspaceId" TEXT NULL,
    "OperationId" TEXT NOT NULL,
    "BlobKey" TEXT NULL,
    "Size" INTEGER NOT NULL,
    "CreatedBy" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    "CreatedAtUnixMs" INTEGER NOT NULL,
    "ExpiresAtUnixMs" INTEGER NOT NULL
);

CREATE INDEX "IX_portability_packages_TenantId_CreatedBy_CreatedAtUnixMs" ON "portability_packages" ("TenantId", "CreatedBy", "CreatedAtUnixMs");

CREATE INDEX "IX_portability_packages_TenantId_ExpiresAtUnixMs" ON "portability_packages" ("TenantId", "ExpiresAtUnixMs");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001114358_Portability', '11.0.0-rc.1.26425.128');


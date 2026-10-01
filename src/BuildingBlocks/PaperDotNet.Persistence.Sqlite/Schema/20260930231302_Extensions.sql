CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "tenant_extensions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_tenant_extensions" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ExtensionId" TEXT NOT NULL,
    "Enabled" INTEGER NOT NULL,
    "Settings" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE UNIQUE INDEX "IX_tenant_extensions_TenantId_ExtensionId" ON "tenant_extensions" ("TenantId", "ExtensionId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930231302_Extensions', '11.0.0-rc.1.26425.128');


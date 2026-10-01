CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "ext_samples_invoices_approvals" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ext_samples_invoices_approvals" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "Amount" TEXT NOT NULL,
    "Comment" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL
);

CREATE INDEX "IX_ext_samples_invoices_approvals_TenantId_ItemId" ON "ext_samples_invoices_approvals" ("TenantId", "ItemId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001050157_Initial', '11.0.0-rc.1.26425.128');


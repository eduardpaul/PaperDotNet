CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "audit_entries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_audit_entries" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NULL,
    "Action" TEXT NOT NULL,
    "TargetId" TEXT NOT NULL,
    "ListId" TEXT NULL,
    "Summary" TEXT NULL,
    "OccurredAt" TEXT NOT NULL
);

CREATE INDEX "IX_audit_entries_TenantId_Id" ON "audit_entries" ("TenantId", "Id");

CREATE INDEX "IX_audit_entries_TenantId_TargetId" ON "audit_entries" ("TenantId", "TargetId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930191914_Initial', '11.0.0-rc.1.26425.128');


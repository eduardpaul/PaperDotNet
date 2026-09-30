CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "operations" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_operations" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Type" TEXT NOT NULL,
    "Status" TEXT NOT NULL,
    "PercentComplete" INTEGER NOT NULL,
    "Payload" TEXT NULL,
    "Result" TEXT NULL,
    "Error" TEXT NULL,
    "CreatedBy" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "StartedAt" TEXT NULL,
    "CompletedAt" TEXT NULL
);

CREATE TABLE "recurring_jobs" (
    "Name" TEXT NOT NULL CONSTRAINT "PK_recurring_jobs" PRIMARY KEY,
    "NextRunAt" TEXT NOT NULL,
    "LastRunAt" TEXT NULL,
    "LastStatus" TEXT NULL,
    "LastError" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE INDEX "IX_operations_TenantId_Status" ON "operations" ("TenantId", "Status");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930195100_JobsAndTenantStatus', '11.0.0-rc.1.26425.128');


CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "ai_calls" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_ai_calls" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Activity" TEXT NOT NULL,
    "Source" TEXT NOT NULL,
    "RunId" TEXT NULL,
    "Model" TEXT NOT NULL,
    "InputHash" TEXT NOT NULL,
    "InputTokens" INTEGER NOT NULL,
    "OutputTokens" INTEGER NOT NULL,
    "Cached" INTEGER NOT NULL,
    "Response" TEXT NULL,
    "Error" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedAtUnixMs" INTEGER NOT NULL
);

CREATE INDEX "IX_ai_calls_TenantId_CreatedAtUnixMs" ON "ai_calls" ("TenantId", "CreatedAtUnixMs");

CREATE INDEX "IX_ai_calls_TenantId_InputHash_CreatedAtUnixMs" ON "ai_calls" ("TenantId", "InputHash", "CreatedAtUnixMs");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001171037_AiWorkflows', '11.0.0-rc.1.26425.128');


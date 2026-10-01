CREATE TABLE "audit_log" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_audit_log" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "AtUnixMs" INTEGER NOT NULL,
    "UserId" TEXT NULL,
    "Action" TEXT NOT NULL,
    "EntityType" TEXT NOT NULL,
    "EntityId" TEXT NULL,
    "Properties" TEXT NULL,
    "TraceId" TEXT NULL
);

CREATE INDEX "IX_audit_log_TenantId_EntityId" ON "audit_log" ("TenantId", "EntityId");

CREATE INDEX "IX_audit_log_TenantId_Id" ON "audit_log" ("TenantId", "Id");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001225547_AuditLog', '11.0.0-rc.1.26425.128');


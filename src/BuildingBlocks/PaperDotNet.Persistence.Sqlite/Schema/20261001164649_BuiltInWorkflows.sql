ALTER TABLE "workflows" ADD "BuiltInKey" TEXT NULL;

ALTER TABLE "workflows" ADD "CopiedFrom" TEXT NULL;

ALTER TABLE "workflows" ADD "ListId" TEXT NULL;

ALTER TABLE "workflows" ADD "Parameters" TEXT NULL;

CREATE INDEX "IX_workflows_TenantId_WorkspaceId_BuiltInKey_ListId" ON "workflows" ("TenantId", "WorkspaceId", "BuiltInKey", "ListId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001164649_BuiltInWorkflows', '11.0.0-rc.1.26425.128');


DELETE FROM "workflow_runs"; DELETE FROM "workflow_versions"; DELETE FROM "workflows";

DROP INDEX "IX_workflows_TenantId_Enabled";

DROP INDEX "IX_workflows_TenantId_Name";

ALTER TABLE "workflows" ADD "WorkspaceId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE "workflow_runs" ADD "WorkspaceId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

CREATE INDEX "IX_workflows_TenantId_WorkspaceId_Enabled" ON "workflows" ("TenantId", "WorkspaceId", "Enabled");

CREATE UNIQUE INDEX "IX_workflows_TenantId_WorkspaceId_Name" ON "workflows" ("TenantId", "WorkspaceId", "Name");

CREATE INDEX "IX_workflow_runs_TenantId_WorkspaceId_Id" ON "workflow_runs" ("TenantId", "WorkspaceId", "Id");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930211002_WorkspaceLists', '11.0.0-rc.1.26425.128');


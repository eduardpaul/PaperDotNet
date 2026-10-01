ALTER TABLE "workflows" ADD "Key" TEXT NULL;

ALTER TABLE "workflow_runs" ADD "CompletedAtUnixMs" INTEGER NULL;

ALTER TABLE "workflow_runs" ADD "NodeAttempts" INTEGER NOT NULL DEFAULT 0;

CREATE INDEX "IX_workflows_TenantId_WorkspaceId_Key" ON "workflows" ("TenantId", "WorkspaceId", "Key");

CREATE INDEX "IX_workflow_runs_TenantId_Status_CompletedAtUnixMs" ON "workflow_runs" ("TenantId", "Status", "CompletedAtUnixMs");

CREATE INDEX "IX_workflow_runs_TenantId_WorkflowId_ItemId_Status" ON "workflow_runs" ("TenantId", "WorkflowId", "ItemId", "Status");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001161438_WorkflowKeysAndRetries', '11.0.0-rc.1.26425.128');


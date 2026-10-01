CREATE INDEX "IX_workflow_runs_TenantId_ItemId_Id" ON "workflow_runs" ("TenantId", "ItemId", "Id");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001212430_WorkflowRunsByItem', '11.0.0-rc.1.26425.128');


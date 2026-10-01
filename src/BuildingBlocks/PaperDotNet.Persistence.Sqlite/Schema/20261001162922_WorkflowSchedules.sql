CREATE TABLE "workflow_schedules" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workflow_schedules" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkflowVersion" INTEGER NOT NULL,
    "NextAtUnixMs" INTEGER NULL,
    "CheckedUntilUnixMs" INTEGER NULL,
    "Version" INTEGER NOT NULL
);

CREATE INDEX "IX_workflow_schedules_TenantId" ON "workflow_schedules" ("TenantId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001162922_WorkflowSchedules', '11.0.0-rc.1.26425.128');


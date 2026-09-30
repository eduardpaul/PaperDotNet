CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "workflows" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workflows" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Enabled" INTEGER NOT NULL,
    "CurrentVersion" INTEGER NOT NULL,
    "TriggerTypes" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "workflow_runs" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workflow_runs" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkflowId" TEXT NOT NULL,
    "WorkflowVersion" INTEGER NOT NULL,
    "Status" TEXT NOT NULL,
    "Node" TEXT NULL,
    "StepExecutionId" TEXT NULL,
    "NodesRun" INTEGER NOT NULL,
    "Trigger" TEXT NOT NULL,
    "ListId" TEXT NULL,
    "ItemId" TEXT NULL,
    "Data" TEXT NULL,
    "Outputs" TEXT NOT NULL,
    "Variables" TEXT NOT NULL,
    "Log" TEXT NOT NULL,
    "Error" TEXT NULL,
    "FailedNode" TEXT NULL,
    "StartedBy" TEXT NULL,
    "Depth" INTEGER NOT NULL,
    "StartedAt" TEXT NOT NULL,
    "CompletedAt" TEXT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_workflow_runs_workflows_WorkflowId" FOREIGN KEY ("WorkflowId") REFERENCES "workflows" ("Id") ON DELETE CASCADE
);

CREATE TABLE "workflow_versions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workflow_versions" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "WorkflowId" TEXT NOT NULL,
    "Number" INTEGER NOT NULL,
    "Definition" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_workflow_versions_workflows_WorkflowId" FOREIGN KEY ("WorkflowId") REFERENCES "workflows" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_workflow_runs_TenantId_WorkflowId_Id" ON "workflow_runs" ("TenantId", "WorkflowId", "Id");

CREATE INDEX "IX_workflow_runs_WorkflowId" ON "workflow_runs" ("WorkflowId");

CREATE UNIQUE INDEX "IX_workflow_versions_TenantId_WorkflowId_Number" ON "workflow_versions" ("TenantId", "WorkflowId", "Number");

CREATE INDEX "IX_workflow_versions_WorkflowId" ON "workflow_versions" ("WorkflowId");

CREATE INDEX "IX_workflows_TenantId_Enabled" ON "workflows" ("TenantId", "Enabled");

CREATE UNIQUE INDEX "IX_workflows_TenantId_Name" ON "workflows" ("TenantId", "Name");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930191919_Initial', '11.0.0-rc.1.26425.128');


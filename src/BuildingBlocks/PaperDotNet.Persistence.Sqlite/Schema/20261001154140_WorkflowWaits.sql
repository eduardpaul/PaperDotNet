ALTER TABLE "workflow_runs" ADD "WaitingOn" TEXT NULL;

CREATE TABLE "workflow_approvals" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workflow_approvals" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "RunId" TEXT NOT NULL,
    "Node" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ListId" TEXT NULL,
    "ItemId" TEXT NULL,
    "Title" TEXT NOT NULL,
    "Assignees" TEXT NOT NULL,
    "EscalateTo" TEXT NOT NULL,
    "DueAt" TEXT NULL,
    "DueAtUnixMs" INTEGER NULL,
    "Escalated" INTEGER NOT NULL,
    "Status" TEXT NOT NULL,
    "DecidedBy" TEXT NULL,
    "DecidedAt" TEXT NULL,
    "Comment" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "workflow_bookmarks" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workflow_bookmarks" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "RunId" TEXT NOT NULL,
    "Node" TEXT NOT NULL,
    "Kind" TEXT NOT NULL,
    "Key" TEXT NOT NULL,
    "ResumeAtUnixMs" INTEGER NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedAtUnixMs" INTEGER NOT NULL,
    "CompletedAt" TEXT NULL,
    "CompletedAtUnixMs" INTEGER NULL,
    "Payload" TEXT NULL,
    "Data" TEXT NULL,
    "RunAgain" INTEGER NOT NULL,
    "Version" INTEGER NOT NULL
);

CREATE INDEX "IX_workflow_approvals_TenantId_RunId" ON "workflow_approvals" ("TenantId", "RunId");

CREATE INDEX "IX_workflow_approvals_TenantId_Status_DueAtUnixMs" ON "workflow_approvals" ("TenantId", "Status", "DueAtUnixMs");

CREATE UNIQUE INDEX "IX_workflow_bookmarks_TenantId_Kind_Key" ON "workflow_bookmarks" ("TenantId", "Kind", "Key");

CREATE INDEX "IX_workflow_bookmarks_TenantId_ResumeAtUnixMs" ON "workflow_bookmarks" ("TenantId", "ResumeAtUnixMs");

CREATE INDEX "IX_workflow_bookmarks_TenantId_RunId" ON "workflow_bookmarks" ("TenantId", "RunId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001154140_WorkflowWaits', '11.0.0-rc.1.26425.128');


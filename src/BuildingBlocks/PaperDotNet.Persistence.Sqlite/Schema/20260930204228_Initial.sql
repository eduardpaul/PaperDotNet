CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "workspace_members" (
    "WorkspaceId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    "Role" TEXT NOT NULL,
    CONSTRAINT "PK_workspace_members" PRIMARY KEY ("WorkspaceId", "UserId")
);

CREATE TABLE "workspaces" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_workspaces" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "PersonalOwnerId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "DeletedAt" TEXT NULL,
    "DeletedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE INDEX "IX_workspace_members_TenantId_UserId" ON "workspace_members" ("TenantId", "UserId");

CREATE UNIQUE INDEX "IX_workspaces_TenantId_PersonalOwnerId" ON "workspaces" ("TenantId", "PersonalOwnerId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930204228_Initial', '11.0.0-rc.1.26425.128');


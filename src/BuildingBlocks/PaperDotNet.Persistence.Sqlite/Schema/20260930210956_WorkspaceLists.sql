DROP INDEX "IX_lists_TenantId_Name";

DROP INDEX "IX_list_items_TenantId_ListId_Title";

DELETE FROM "list_items"; DELETE FROM "lists";

ALTER TABLE "lists" ADD "WorkspaceId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE "lists" ADD "AllowFolders" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "lists" ADD "ContentTypeIds" TEXT NOT NULL DEFAULT '';

ALTER TABLE "lists" ADD "DeletedAt" TEXT NULL;

ALTER TABLE "lists" ADD "DeletedBy" TEXT NULL;

ALTER TABLE "lists" ADD "HasUniquePermissions" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "lists" ADD "Kind" TEXT NOT NULL DEFAULT '';

ALTER TABLE "lists" ADD "MaxVersions" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "lists" ADD "SystemKey" TEXT NULL;

ALTER TABLE "lists" ADD "TemplateKey" TEXT NULL;

ALTER TABLE "lists" ADD "Versioning" TEXT NOT NULL DEFAULT '';

ALTER TABLE "list_items" ADD "ContentTypeId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

ALTER TABLE "list_items" ADD "DeletedAt" TEXT NULL;

ALTER TABLE "list_items" ADD "DeletedBy" TEXT NULL;

ALTER TABLE "list_items" ADD "HasUniquePermissions" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "list_items" ADD "IsFolder" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "list_items" ADD "ParentId" TEXT NULL;

ALTER TABLE "list_items" ADD "ScopeId" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';

CREATE TABLE "acl_entries" (
    "ScopeId" TEXT NOT NULL,
    "PrincipalId" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    "PrincipalType" TEXT NOT NULL,
    "Level" INTEGER NOT NULL,
    "ListId" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    CONSTRAINT "PK_acl_entries" PRIMARY KEY ("ScopeId", "PrincipalId")
);

CREATE TABLE "content_types" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_content_types" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsBuiltIn" INTEGER NOT NULL,
    "Key" TEXT NULL,
    "ExtensionId" TEXT NULL,
    "Fields" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE INDEX "IX_lists_TenantId_WorkspaceId" ON "lists" ("TenantId", "WorkspaceId");

CREATE INDEX "IX_list_items_TenantId_ListId_ParentId_IsFolder_Title" ON "list_items" ("TenantId", "ListId", "ParentId", "IsFolder", "Title");

CREATE INDEX "IX_list_items_TenantId_ScopeId" ON "list_items" ("TenantId", "ScopeId");

CREATE INDEX "IX_acl_entries_TenantId_ListId" ON "acl_entries" ("TenantId", "ListId");

CREATE INDEX "IX_acl_entries_TenantId_PrincipalId_ListId" ON "acl_entries" ("TenantId", "PrincipalId", "ListId");

CREATE INDEX "IX_content_types_TenantId_Key" ON "content_types" ("TenantId", "Key");

CREATE UNIQUE INDEX "IX_content_types_TenantId_Name" ON "content_types" ("TenantId", "Name");

CREATE TABLE "ef_temp_lists" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_lists" PRIMARY KEY,
    "AllowFolders" INTEGER NOT NULL,
    "ContentTypeIds" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "DeletedAt" TEXT NULL,
    "DeletedBy" TEXT NULL,
    "Description" TEXT NULL,
    "HasUniquePermissions" INTEGER NOT NULL,
    "Kind" TEXT NOT NULL,
    "MaxVersions" INTEGER NOT NULL,
    "Name" TEXT NOT NULL,
    "SystemKey" TEXT NULL,
    "TemplateKey" TEXT NULL,
    "TenantId" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL,
    "Versioning" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL
);

INSERT INTO "ef_temp_lists" ("Id", "AllowFolders", "ContentTypeIds", "CreatedAt", "CreatedBy", "DeletedAt", "DeletedBy", "Description", "HasUniquePermissions", "Kind", "MaxVersions", "Name", "SystemKey", "TemplateKey", "TenantId", "UpdatedAt", "UpdatedBy", "Version", "Versioning", "WorkspaceId")
SELECT "Id", "AllowFolders", "ContentTypeIds", "CreatedAt", "CreatedBy", "DeletedAt", "DeletedBy", "Description", "HasUniquePermissions", "Kind", "MaxVersions", "Name", "SystemKey", "TemplateKey", "TenantId", "UpdatedAt", "UpdatedBy", "Version", "Versioning", "WorkspaceId"
FROM "lists";

PRAGMA foreign_keys = 0;

DROP TABLE "lists";

ALTER TABLE "ef_temp_lists" RENAME TO "lists";

PRAGMA foreign_keys = 1;

CREATE INDEX "IX_lists_TenantId_WorkspaceId" ON "lists" ("TenantId", "WorkspaceId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930210956_WorkspaceLists', '11.0.0-rc.1.26425.128');


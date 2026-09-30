CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "audit_entries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_audit_entries" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NULL,
    "Action" TEXT NOT NULL,
    "TargetId" TEXT NOT NULL,
    "ListId" TEXT NULL,
    "Summary" TEXT NULL,
    "OccurredAt" TEXT NOT NULL
);

CREATE TABLE "tenants" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_tenants" PRIMARY KEY,
    "Identifier" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE "lists" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_lists" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Fields" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_lists_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE CASCADE
);

CREATE TABLE "users" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_users" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserName" TEXT NOT NULL,
    "NormalizedUserName" TEXT NOT NULL,
    "DisplayName" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "IsAdmin" INTEGER NOT NULL,
    "IsDisabled" INTEGER NOT NULL,
    "SecurityStamp" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_users_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE CASCADE
);

CREATE TABLE "list_items" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_list_items" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Fields" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_list_items_lists_ListId" FOREIGN KEY ("ListId") REFERENCES "lists" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_audit_entries_TenantId_Id" ON "audit_entries" ("TenantId", "Id");

CREATE INDEX "IX_audit_entries_TenantId_TargetId" ON "audit_entries" ("TenantId", "TargetId");

CREATE INDEX "IX_list_items_ListId" ON "list_items" ("ListId");

CREATE INDEX "IX_list_items_TenantId_ListId_Id" ON "list_items" ("TenantId", "ListId", "Id");

CREATE INDEX "IX_list_items_TenantId_ListId_Title" ON "list_items" ("TenantId", "ListId", "Title");

CREATE UNIQUE INDEX "IX_lists_TenantId_Name" ON "lists" ("TenantId", "Name");

CREATE UNIQUE INDEX "IX_tenants_Identifier" ON "tenants" ("Identifier");

CREATE UNIQUE INDEX "IX_users_TenantId_NormalizedUserName" ON "users" ("TenantId", "NormalizedUserName");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930173822_Initial', '11.0.0-rc.1.26425.128');


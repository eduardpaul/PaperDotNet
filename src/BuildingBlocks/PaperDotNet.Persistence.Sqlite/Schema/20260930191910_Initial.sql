CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
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
    "Version" INTEGER NOT NULL
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

CREATE INDEX "IX_list_items_ListId" ON "list_items" ("ListId");

CREATE INDEX "IX_list_items_TenantId_ListId_Id" ON "list_items" ("TenantId", "ListId", "Id");

CREATE INDEX "IX_list_items_TenantId_ListId_Title" ON "list_items" ("TenantId", "ListId", "Title");

CREATE UNIQUE INDEX "IX_lists_TenantId_Name" ON "lists" ("TenantId", "Name");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930191910_Initial', '11.0.0-rc.1.26425.128');


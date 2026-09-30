CREATE TABLE "item_versions" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_item_versions" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ItemId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Number" INTEGER NOT NULL,
    "ContentTypeId" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Fields" TEXT NOT NULL,
    "ChangedFields" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL
);

CREATE UNIQUE INDEX "IX_item_versions_TenantId_ItemId_Number" ON "item_versions" ("TenantId", "ItemId", "Number");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930213101_ItemVersions', '11.0.0-rc.1.26425.128');


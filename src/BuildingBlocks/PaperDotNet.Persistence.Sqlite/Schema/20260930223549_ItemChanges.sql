CREATE TABLE "item_changes" (
    "Sequence" INTEGER NOT NULL CONSTRAINT "PK_item_changes" PRIMARY KEY AUTOINCREMENT,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "ItemId" TEXT NULL,
    "ScopeId" TEXT NULL,
    "FromScopeId" TEXT NULL,
    "Kind" TEXT NOT NULL,
    "At" INTEGER NOT NULL
);

CREATE INDEX "IX_item_changes_TenantId_At" ON "item_changes" ("TenantId", "At");

CREATE INDEX "IX_item_changes_TenantId_ListId_Sequence" ON "item_changes" ("TenantId", "ListId", "Sequence");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930223549_ItemChanges', '11.0.0-rc.1.26425.128');


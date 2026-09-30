CREATE TABLE "list_views" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_list_views" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Columns" TEXT NOT NULL,
    "Filter" TEXT NULL,
    "OrderBy" TEXT NULL,
    "GroupBy" TEXT NULL,
    "Layout" TEXT NOT NULL,
    "IsDefault" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_list_views_lists_ListId" FOREIGN KEY ("ListId") REFERENCES "lists" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_list_views_ListId" ON "list_views" ("ListId");

CREATE INDEX "IX_list_views_TenantId_ListId" ON "list_views" ("TenantId", "ListId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930220853_ListViews', '11.0.0-rc.1.26425.128');


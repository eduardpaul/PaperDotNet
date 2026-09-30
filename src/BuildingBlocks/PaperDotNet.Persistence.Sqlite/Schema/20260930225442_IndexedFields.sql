ALTER TABLE "lists" ADD "IndexPending" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "lists" ADD "IndexedFields" TEXT NOT NULL DEFAULT '[]';

ALTER TABLE "lists" ADD "NextValueField" INTEGER NOT NULL DEFAULT 16;

ALTER TABLE "list_items" ADD "Date1" TEXT NULL;

ALTER TABLE "list_items" ADD "Date10" TEXT NULL;

ALTER TABLE "list_items" ADD "Date2" TEXT NULL;

ALTER TABLE "list_items" ADD "Date3" TEXT NULL;

ALTER TABLE "list_items" ADD "Date4" TEXT NULL;

ALTER TABLE "list_items" ADD "Date5" TEXT NULL;

ALTER TABLE "list_items" ADD "Date6" TEXT NULL;

ALTER TABLE "list_items" ADD "Date7" TEXT NULL;

ALTER TABLE "list_items" ADD "Date8" TEXT NULL;

ALTER TABLE "list_items" ADD "Date9" TEXT NULL;

ALTER TABLE "list_items" ADD "Number1" REAL NULL;

ALTER TABLE "list_items" ADD "Number10" REAL NULL;

ALTER TABLE "list_items" ADD "Number2" REAL NULL;

ALTER TABLE "list_items" ADD "Number3" REAL NULL;

ALTER TABLE "list_items" ADD "Number4" REAL NULL;

ALTER TABLE "list_items" ADD "Number5" REAL NULL;

ALTER TABLE "list_items" ADD "Number6" REAL NULL;

ALTER TABLE "list_items" ADD "Number7" REAL NULL;

ALTER TABLE "list_items" ADD "Number8" REAL NULL;

ALTER TABLE "list_items" ADD "Number9" REAL NULL;

ALTER TABLE "list_items" ADD "Text1" TEXT NULL;

ALTER TABLE "list_items" ADD "Text10" TEXT NULL;

ALTER TABLE "list_items" ADD "Text2" TEXT NULL;

ALTER TABLE "list_items" ADD "Text3" TEXT NULL;

ALTER TABLE "list_items" ADD "Text4" TEXT NULL;

ALTER TABLE "list_items" ADD "Text5" TEXT NULL;

ALTER TABLE "list_items" ADD "Text6" TEXT NULL;

ALTER TABLE "list_items" ADD "Text7" TEXT NULL;

ALTER TABLE "list_items" ADD "Text8" TEXT NULL;

ALTER TABLE "list_items" ADD "Text9" TEXT NULL;

CREATE TABLE "item_values" (
    "ItemId" TEXT NOT NULL,
    "Field" INTEGER NOT NULL,
    "Value" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    "ListId" TEXT NOT NULL,
    CONSTRAINT "PK_item_values" PRIMARY KEY ("ItemId", "Field", "Value")
);

CREATE INDEX "IX_list_items_TenantId_ListId_Date10_Id" ON "list_items" ("TenantId", "ListId", "Date10", "Id") WHERE "Date10" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date1_Id" ON "list_items" ("TenantId", "ListId", "Date1", "Id") WHERE "Date1" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date2_Id" ON "list_items" ("TenantId", "ListId", "Date2", "Id") WHERE "Date2" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date3_Id" ON "list_items" ("TenantId", "ListId", "Date3", "Id") WHERE "Date3" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date4_Id" ON "list_items" ("TenantId", "ListId", "Date4", "Id") WHERE "Date4" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date5_Id" ON "list_items" ("TenantId", "ListId", "Date5", "Id") WHERE "Date5" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date6_Id" ON "list_items" ("TenantId", "ListId", "Date6", "Id") WHERE "Date6" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date7_Id" ON "list_items" ("TenantId", "ListId", "Date7", "Id") WHERE "Date7" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date8_Id" ON "list_items" ("TenantId", "ListId", "Date8", "Id") WHERE "Date8" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Date9_Id" ON "list_items" ("TenantId", "ListId", "Date9", "Id") WHERE "Date9" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number10_Id" ON "list_items" ("TenantId", "ListId", "Number10", "Id") WHERE "Number10" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number1_Id" ON "list_items" ("TenantId", "ListId", "Number1", "Id") WHERE "Number1" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number2_Id" ON "list_items" ("TenantId", "ListId", "Number2", "Id") WHERE "Number2" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number3_Id" ON "list_items" ("TenantId", "ListId", "Number3", "Id") WHERE "Number3" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number4_Id" ON "list_items" ("TenantId", "ListId", "Number4", "Id") WHERE "Number4" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number5_Id" ON "list_items" ("TenantId", "ListId", "Number5", "Id") WHERE "Number5" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number6_Id" ON "list_items" ("TenantId", "ListId", "Number6", "Id") WHERE "Number6" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number7_Id" ON "list_items" ("TenantId", "ListId", "Number7", "Id") WHERE "Number7" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number8_Id" ON "list_items" ("TenantId", "ListId", "Number8", "Id") WHERE "Number8" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Number9_Id" ON "list_items" ("TenantId", "ListId", "Number9", "Id") WHERE "Number9" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text10_Id" ON "list_items" ("TenantId", "ListId", "Text10", "Id") WHERE "Text10" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text1_Id" ON "list_items" ("TenantId", "ListId", "Text1", "Id") WHERE "Text1" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text2_Id" ON "list_items" ("TenantId", "ListId", "Text2", "Id") WHERE "Text2" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text3_Id" ON "list_items" ("TenantId", "ListId", "Text3", "Id") WHERE "Text3" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text4_Id" ON "list_items" ("TenantId", "ListId", "Text4", "Id") WHERE "Text4" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text5_Id" ON "list_items" ("TenantId", "ListId", "Text5", "Id") WHERE "Text5" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text6_Id" ON "list_items" ("TenantId", "ListId", "Text6", "Id") WHERE "Text6" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text7_Id" ON "list_items" ("TenantId", "ListId", "Text7", "Id") WHERE "Text7" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text8_Id" ON "list_items" ("TenantId", "ListId", "Text8", "Id") WHERE "Text8" IS NOT NULL;

CREATE INDEX "IX_list_items_TenantId_ListId_Text9_Id" ON "list_items" ("TenantId", "ListId", "Text9", "Id") WHERE "Text9" IS NOT NULL;

CREATE INDEX "IX_item_values_TenantId_ListId_Field_Value_ItemId" ON "item_values" ("TenantId", "ListId", "Field", "Value", "ItemId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930225442_IndexedFields', '11.0.0-rc.1.26425.128');


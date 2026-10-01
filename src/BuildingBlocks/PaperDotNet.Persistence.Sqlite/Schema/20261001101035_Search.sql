CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "search_documents" (
    "Key" INTEGER NOT NULL CONSTRAINT "PK_search_documents" PRIMARY KEY AUTOINCREMENT,
    "Id" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    "SourceType" TEXT NOT NULL,
    "WorkspaceId" TEXT NOT NULL,
    "ContainerId" TEXT NULL,
    "ContentTypeId" TEXT NULL,
    "ScopeId" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Keywords" TEXT NOT NULL,
    "Body" TEXT NOT NULL,
    "Language" TEXT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" INTEGER NOT NULL
);

CREATE TABLE "search_passages" (
    "Key" INTEGER NOT NULL CONSTRAINT "PK_search_passages" PRIMARY KEY AUTOINCREMENT,
    "Id" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    "DocumentId" TEXT NOT NULL,
    "Ordinal" INTEGER NOT NULL,
    "Page" INTEGER NULL,
    "Text" TEXT NOT NULL,
    "ContentHash" TEXT NOT NULL
);

CREATE TABLE "search_tags" (
    "DocumentId" TEXT NOT NULL,
    "TermId" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    CONSTRAINT "PK_search_tags" PRIMARY KEY ("DocumentId", "TermId")
);

CREATE UNIQUE INDEX "IX_search_documents_Id" ON "search_documents" ("Id");

CREATE INDEX "IX_search_documents_TenantId_ContainerId" ON "search_documents" ("TenantId", "ContainerId");

CREATE INDEX "IX_search_documents_TenantId_ScopeId" ON "search_documents" ("TenantId", "ScopeId");

CREATE INDEX "IX_search_documents_TenantId_SourceType" ON "search_documents" ("TenantId", "SourceType");

CREATE UNIQUE INDEX "IX_search_passages_Id" ON "search_passages" ("Id");

CREATE INDEX "IX_search_passages_TenantId_DocumentId" ON "search_passages" ("TenantId", "DocumentId");

CREATE INDEX "IX_search_tags_TenantId_TermId" ON "search_tags" ("TenantId", "TermId");

CREATE VIRTUAL TABLE "search_documents_fts" USING fts5("Title", "Keywords", "Body", content='search_documents', content_rowid='Key', tokenize='porter unicode61 remove_diacritics 2');
CREATE TRIGGER "search_documents_fts_ai" AFTER INSERT ON "search_documents" BEGIN
  INSERT INTO "search_documents_fts"(rowid, "Title", "Keywords", "Body") VALUES (new."Key", new."Title", new."Keywords", new."Body");
END;
CREATE TRIGGER "search_documents_fts_ad" AFTER DELETE ON "search_documents" BEGIN
  INSERT INTO "search_documents_fts"("search_documents_fts", rowid, "Title", "Keywords", "Body") VALUES ('delete', old."Key", old."Title", old."Keywords", old."Body");
END;
CREATE TRIGGER "search_documents_fts_au" AFTER UPDATE OF "Title", "Keywords", "Body" ON "search_documents" BEGIN
  INSERT INTO "search_documents_fts"("search_documents_fts", rowid, "Title", "Keywords", "Body") VALUES ('delete', old."Key", old."Title", old."Keywords", old."Body");
  INSERT INTO "search_documents_fts"(rowid, "Title", "Keywords", "Body") VALUES (new."Key", new."Title", new."Keywords", new."Body");
END;
INSERT INTO "search_documents_fts"("search_documents_fts") VALUES ('rebuild');

CREATE VIRTUAL TABLE "search_passages_fts" USING fts5("Text", content='search_passages', content_rowid='Key', tokenize='porter unicode61 remove_diacritics 2');
CREATE TRIGGER "search_passages_fts_ai" AFTER INSERT ON "search_passages" BEGIN
  INSERT INTO "search_passages_fts"(rowid, "Text") VALUES (new."Key", new."Text");
END;
CREATE TRIGGER "search_passages_fts_ad" AFTER DELETE ON "search_passages" BEGIN
  INSERT INTO "search_passages_fts"("search_passages_fts", rowid, "Text") VALUES ('delete', old."Key", old."Text");
END;
CREATE TRIGGER "search_passages_fts_au" AFTER UPDATE OF "Text" ON "search_passages" BEGIN
  INSERT INTO "search_passages_fts"("search_passages_fts", rowid, "Text") VALUES ('delete', old."Key", old."Text");
  INSERT INTO "search_passages_fts"(rowid, "Text") VALUES (new."Key", new."Text");
END;
INSERT INTO "search_passages_fts"("search_passages_fts") VALUES ('rebuild');

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001101035_Search', '11.0.0-rc.1.26425.128');


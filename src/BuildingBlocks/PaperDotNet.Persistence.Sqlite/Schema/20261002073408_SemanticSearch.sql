ALTER TABLE "search_passages" ADD "Embedding" BLOB NULL;

ALTER TABLE "search_passages" ADD "EmbeddingModel" TEXT NULL;

ALTER TABLE "search_passages" ADD "VectorStamp" INTEGER NOT NULL DEFAULT 0;

CREATE INDEX "IX_search_passages_TenantId_EmbeddingModel_VectorStamp" ON "search_passages" ("TenantId", "EmbeddingModel", "VectorStamp");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002073408_SemanticSearch', '11.0.0-rc.1.26425.128');


CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "term_groups" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_term_groups" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsSystem" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "term_sets" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_term_sets" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "GroupId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsOpen" INTEGER NOT NULL,
    "IsKeywords" INTEGER NOT NULL,
    "Key" TEXT NULL,
    "ExtensionId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "terms" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_terms" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "TermSetId" TEXT NOT NULL,
    "ParentId" TEXT NULL,
    "Name" TEXT NOT NULL,
    "NormalizedName" TEXT NOT NULL,
    "Path" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Color" TEXT NULL,
    "Labels" TEXT NOT NULL,
    "Synonyms" TEXT NOT NULL,
    "SearchText" TEXT NOT NULL,
    "SortOrder" INTEGER NOT NULL,
    "IsDeprecated" INTEGER NOT NULL,
    "MergedIntoId" TEXT NULL,
    "AvailableAsKeyword" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "CreatedBy" TEXT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "UpdatedBy" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE UNIQUE INDEX "IX_term_groups_TenantId_Name" ON "term_groups" ("TenantId", "Name");

CREATE UNIQUE INDEX "IX_term_sets_TenantId_GroupId_Name" ON "term_sets" ("TenantId", "GroupId", "Name");

CREATE UNIQUE INDEX "IX_term_sets_TenantId_Key" ON "term_sets" ("TenantId", "Key");

CREATE INDEX "IX_terms_TenantId_Path" ON "terms" ("TenantId", "Path");

CREATE INDEX "IX_terms_TenantId_TermSetId_ParentId_NormalizedName" ON "terms" ("TenantId", "TermSetId", "ParentId", "NormalizedName");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001074616_Taxonomy', '11.0.0-rc.1.26425.128');


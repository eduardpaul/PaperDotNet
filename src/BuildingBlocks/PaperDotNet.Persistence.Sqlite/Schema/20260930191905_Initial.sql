CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
    "ProductVersion" TEXT NOT NULL
);

CREATE TABLE "tenants" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_tenants" PRIMARY KEY,
    "Identifier" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
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

CREATE UNIQUE INDEX "IX_tenants_Identifier" ON "tenants" ("Identifier");

CREATE UNIQUE INDEX "IX_users_TenantId_NormalizedUserName" ON "users" ("TenantId", "NormalizedUserName");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930191905_Initial', '11.0.0-rc.1.26425.128');


ALTER TABLE "users" ADD "AccessFailedCount" INTEGER NOT NULL DEFAULT 0;

ALTER TABLE "users" ADD "DeletedAt" TEXT NULL;

ALTER TABLE "users" ADD "Email" TEXT NULL;

ALTER TABLE "users" ADD "LockoutEnd" TEXT NULL;

CREATE TABLE "api_tokens" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_api_tokens" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Prefix" TEXT NOT NULL,
    "Hash" BLOB NOT NULL,
    "Scopes" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ExpiresAt" TEXT NULL,
    "LastUsedAt" TEXT NULL,
    "RevokedAt" TEXT NULL
);

CREATE TABLE "group_closure" (
    "GroupId" TEXT NOT NULL,
    "AncestorId" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    CONSTRAINT "PK_group_closure" PRIMARY KEY ("GroupId", "AncestorId")
);

CREATE TABLE "group_members" (
    "GroupId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    CONSTRAINT "PK_group_members" PRIMARY KEY ("GroupId", "UserId")
);

CREATE TABLE "group_nestings" (
    "GroupId" TEXT NOT NULL,
    "MemberGroupId" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    CONSTRAINT "PK_group_nestings" PRIMARY KEY ("GroupId", "MemberGroupId")
);

CREATE TABLE "groups" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_groups" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "preferences" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_preferences" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "Language" TEXT NULL,
    "TimeZone" TEXT NULL,
    "DateFormat" TEXT NULL,
    "TimeFormat" TEXT NULL,
    "NumberFormat" TEXT NULL,
    "Theme" TEXT NULL,
    "DocumentLanguages" TEXT NULL,
    "Version" INTEGER NOT NULL
);

CREATE TABLE "role_assignments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_role_assignments" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "RoleId" TEXT NOT NULL,
    "PrincipalId" TEXT NOT NULL,
    "PrincipalType" TEXT NOT NULL
);

CREATE TABLE "roles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_roles" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "IsBuiltIn" INTEGER NOT NULL,
    "GrantsAllScopes" INTEGER NOT NULL,
    "Scopes" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Version" INTEGER NOT NULL
);

CREATE UNIQUE INDEX "IX_api_tokens_Hash" ON "api_tokens" ("Hash");

CREATE INDEX "IX_api_tokens_TenantId_UserId" ON "api_tokens" ("TenantId", "UserId");

CREATE INDEX "IX_group_closure_TenantId_AncestorId_GroupId" ON "group_closure" ("TenantId", "AncestorId", "GroupId");

CREATE INDEX "IX_group_members_TenantId_UserId" ON "group_members" ("TenantId", "UserId");

CREATE INDEX "IX_group_nestings_TenantId_MemberGroupId" ON "group_nestings" ("TenantId", "MemberGroupId");

CREATE UNIQUE INDEX "IX_groups_TenantId_Name" ON "groups" ("TenantId", "Name");

CREATE UNIQUE INDEX "IX_preferences_TenantId_UserId" ON "preferences" ("TenantId", "UserId");

CREATE UNIQUE INDEX "IX_role_assignments_RoleId_PrincipalId_PrincipalType" ON "role_assignments" ("RoleId", "PrincipalId", "PrincipalType");

CREATE INDEX "IX_role_assignments_TenantId_PrincipalId" ON "role_assignments" ("TenantId", "PrincipalId");

CREATE UNIQUE INDEX "IX_roles_TenantId_Name" ON "roles" ("TenantId", "Name");

CREATE TABLE "ef_temp_users" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_users" PRIMARY KEY,
    "AccessFailedCount" INTEGER NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "DeletedAt" TEXT NULL,
    "DisplayName" TEXT NOT NULL,
    "Email" TEXT NULL,
    "IsDisabled" INTEGER NOT NULL,
    "LockoutEnd" TEXT NULL,
    "NormalizedUserName" TEXT NOT NULL,
    "PasswordHash" TEXT NOT NULL,
    "SecurityStamp" TEXT NOT NULL,
    "TenantId" TEXT NOT NULL,
    "UserName" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_users_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE CASCADE
);

INSERT INTO "ef_temp_users" ("Id", "AccessFailedCount", "CreatedAt", "DeletedAt", "DisplayName", "Email", "IsDisabled", "LockoutEnd", "NormalizedUserName", "PasswordHash", "SecurityStamp", "TenantId", "UserName", "Version")
SELECT "Id", "AccessFailedCount", "CreatedAt", "DeletedAt", "DisplayName", "Email", "IsDisabled", "LockoutEnd", "NormalizedUserName", "PasswordHash", "SecurityStamp", "TenantId", "UserName", "Version"
FROM "users";

PRAGMA foreign_keys = 0;

DROP TABLE "users";

ALTER TABLE "ef_temp_users" RENAME TO "users";

PRAGMA foreign_keys = 1;

CREATE UNIQUE INDEX "IX_users_TenantId_NormalizedUserName" ON "users" ("TenantId", "NormalizedUserName");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930201333_GroupsRolesTokens', '11.0.0-rc.1.26425.128');


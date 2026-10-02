ALTER TABLE "users" ADD "IsServiceAccount" INTEGER NOT NULL DEFAULT 0;

CREATE TABLE "oauth_clients" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_oauth_clients" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "ClientId" TEXT NOT NULL,
    "DisplayName" TEXT NOT NULL,
    "ClientType" TEXT NOT NULL,
    "SecretHash" TEXT NULL,
    "GrantTypes" TEXT NOT NULL,
    "Scopes" TEXT NOT NULL,
    "RedirectUris" TEXT NOT NULL,
    "PostLogoutRedirectUris" TEXT NOT NULL,
    "ServiceUserId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE TABLE "oauth_codes" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_oauth_codes" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "CodeHash" TEXT NOT NULL,
    "ClientId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "RedirectUri" TEXT NOT NULL,
    "CodeChallenge" TEXT NOT NULL,
    "Scope" TEXT NOT NULL,
    "Nonce" TEXT NULL,
    "SecurityStamp" TEXT NOT NULL,
    "ExpiresAt" INTEGER NOT NULL
);

CREATE TABLE "server_keys" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_server_keys" PRIMARY KEY,
    "Use" TEXT NOT NULL,
    "ProtectedKey" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX "IX_oauth_clients_TenantId_ClientId" ON "oauth_clients" ("TenantId", "ClientId");

CREATE UNIQUE INDEX "IX_oauth_codes_CodeHash" ON "oauth_codes" ("CodeHash");

CREATE INDEX "IX_oauth_codes_ExpiresAt" ON "oauth_codes" ("ExpiresAt");

CREATE INDEX "IX_server_keys_Use" ON "server_keys" ("Use");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002083047_OAuthServer', '11.0.0-rc.1.26425.128');


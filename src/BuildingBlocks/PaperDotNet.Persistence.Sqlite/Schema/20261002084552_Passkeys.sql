CREATE TABLE "user_passkeys" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_user_passkeys" PRIMARY KEY,
    "TenantId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    "CredentialId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "PublicKey" BLOB NOT NULL,
    "SignCount" INTEGER NOT NULL,
    "Transports" TEXT NOT NULL,
    "IsUserVerified" INTEGER NOT NULL,
    "IsBackupEligible" INTEGER NOT NULL,
    "IsBackedUp" INTEGER NOT NULL,
    "AttestationObject" BLOB NOT NULL,
    "ClientDataJson" BLOB NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE UNIQUE INDEX "IX_user_passkeys_TenantId_CredentialId" ON "user_passkeys" ("TenantId", "CredentialId");

CREATE INDEX "IX_user_passkeys_TenantId_UserId" ON "user_passkeys" ("TenantId", "UserId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261002084552_Passkeys', '11.0.0-rc.1.26425.128');


CREATE TABLE "tenant_hosts" (
    "Host" TEXT NOT NULL CONSTRAINT "PK_tenant_hosts" PRIMARY KEY,
    "TenantId" TEXT NOT NULL
);

CREATE INDEX "IX_tenant_hosts_TenantId" ON "tenant_hosts" ("TenantId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001232459_TenantHosts', '11.0.0-rc.1.26425.128');


ALTER TABLE "tenants" ADD "Status" TEXT NOT NULL DEFAULT 'active';

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260930195048_JobsAndTenantStatus', '11.0.0-rc.1.26425.128');


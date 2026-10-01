CREATE UNIQUE INDEX "IX_lists_TenantId_WorkspaceId_SystemKey" ON "lists" ("TenantId", "WorkspaceId", "SystemKey");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261001220845_HomeLibraries', '11.0.0-rc.1.26425.128');


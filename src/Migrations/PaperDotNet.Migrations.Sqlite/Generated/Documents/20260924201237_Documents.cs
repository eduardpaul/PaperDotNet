using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Documents
{
    /// <inheritdoc />
    public partial class Documents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    at = table.Column<long>(type: "INTEGER", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    properties = table.Column<string>(type: "TEXT", nullable: false),
                    trace_id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "documents_file_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    is_current = table.Column<bool>(type: "INTEGER", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_file_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "documents_library_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    duplicate_policy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_library_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "documents_stored_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    media_type = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_used_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_stored_files", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_audit_log_entity_id",
                table: "documents_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_audit_log_tenant_id",
                table: "documents_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_audit_log_tenant_id_at",
                table: "documents_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_versions_item_id_number",
                table: "documents_file_versions",
                columns: new[] { "item_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_versions_stored_file_id",
                table: "documents_file_versions",
                column: "stored_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_versions_tenant_id",
                table: "documents_file_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_versions_tenant_id_sha256_is_current",
                table: "documents_file_versions",
                columns: new[] { "tenant_id", "sha256", "is_current" });

            migrationBuilder.CreateIndex(
                name: "ix_documents_library_settings_tenant_id",
                table: "documents_library_settings",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_library_settings_tenant_id_list_id",
                table: "documents_library_settings",
                columns: new[] { "tenant_id", "list_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_documents_stored_files_tenant_id",
                table: "documents_stored_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_stored_files_tenant_id_sha256",
                table: "documents_stored_files",
                columns: new[] { "tenant_id", "sha256" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents_audit_log");

            migrationBuilder.DropTable(
                name: "documents_file_versions");

            migrationBuilder.DropTable(
                name: "documents_library_settings");

            migrationBuilder.DropTable(
                name: "documents_stored_files");
        }
    }
}

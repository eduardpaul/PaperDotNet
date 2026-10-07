using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Dav
{
    /// <inheritdoc />
    public partial class WebDav : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dav_audit_log",
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
                    table.PrimaryKey("pk_dav_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dav_locks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    state_token = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    href = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    recursive = table.Column<bool>(type: "INTEGER", nullable: false),
                    access_type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    share_mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    timeout_seconds = table.Column<long>(type: "INTEGER", nullable: false),
                    owner = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    owner_href = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    issued = table.Column<long>(type: "INTEGER", nullable: false),
                    last_refresh = table.Column<long>(type: "INTEGER", nullable: true),
                    expiration = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dav_locks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "dav_transient_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    name_key = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    blob_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    size = table.Column<long>(type: "INTEGER", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    original_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    version_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    released = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    expires_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dav_transient_files", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dav_audit_log_entity_id",
                table: "dav_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_dav_audit_log_tenant_id",
                table: "dav_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_dav_audit_log_tenant_id_at",
                table: "dav_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_dav_locks_expiration",
                table: "dav_locks",
                column: "expiration");

            migrationBuilder.CreateIndex(
                name: "ix_dav_locks_state_token",
                table: "dav_locks",
                column: "state_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dav_locks_tenant_id",
                table: "dav_locks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_dav_transient_files_expires_at",
                table: "dav_transient_files",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_dav_transient_files_tenant_id",
                table: "dav_transient_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_dav_transient_files_user_id_parent_id_name_key",
                table: "dav_transient_files",
                columns: new[] { "user_id", "parent_id", "name_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dav_audit_log");

            migrationBuilder.DropTable(
                name: "dav_locks");

            migrationBuilder.DropTable(
                name: "dav_transient_files");
        }
    }
}

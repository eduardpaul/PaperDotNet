using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Dav
{
    /// <inheritdoc />
    public partial class WebDav : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dav");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "dav",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    properties = table.Column<List<string>>(type: "text[]", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "locks",
                schema: "dav",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_token = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    path = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    href = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    recursive = table.Column<bool>(type: "boolean", nullable: false),
                    access_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    share_mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    timeout_seconds = table.Column<long>(type: "bigint", nullable: false),
                    owner = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    owner_href = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issued = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_refresh = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expiration = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_locks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transient_files",
                schema: "dav",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    blob_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    released = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transient_files", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "dav",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "dav",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "dav",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_locks_expiration",
                schema: "dav",
                table: "locks",
                column: "expiration");

            migrationBuilder.CreateIndex(
                name: "ix_locks_state_token",
                schema: "dav",
                table: "locks",
                column: "state_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_locks_tenant_id",
                schema: "dav",
                table: "locks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_transient_files_expires_at",
                schema: "dav",
                table: "transient_files",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_transient_files_tenant_id",
                schema: "dav",
                table: "transient_files",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_transient_files_user_id_parent_id_name_key",
                schema: "dav",
                table: "transient_files",
                columns: new[] { "user_id", "parent_id", "name_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "dav");

            migrationBuilder.DropTable(
                name: "locks",
                schema: "dav");

            migrationBuilder.DropTable(
                name: "transient_files",
                schema: "dav");
        }
    }
}

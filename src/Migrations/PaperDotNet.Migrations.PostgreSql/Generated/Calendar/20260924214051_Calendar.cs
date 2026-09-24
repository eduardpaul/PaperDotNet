using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Calendar
{
    /// <inheritdoc />
    public partial class Calendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "calendar");

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "calendar",
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
                name: "feeds",
                schema: "calendar",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: true),
                    list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    secret_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_feeds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "occurrence_changes",
                schema: "calendar",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    master_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    override_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_occurrence_changes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "recurrences",
                schema: "calendar",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    time_zone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recurrences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                schema: "calendar",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uid = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sources", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "calendar",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "calendar",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "calendar",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_feeds_secret_hash",
                schema: "calendar",
                table: "feeds",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_feeds_tenant_id",
                schema: "calendar",
                table: "feeds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_feeds_user_id",
                schema: "calendar",
                table: "feeds",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_occurrence_changes_master_item_id_original_start",
                schema: "calendar",
                table: "occurrence_changes",
                columns: new[] { "master_item_id", "original_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_occurrence_changes_override_item_id",
                schema: "calendar",
                table: "occurrence_changes",
                column: "override_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_occurrence_changes_tenant_id",
                schema: "calendar",
                table: "occurrence_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurrences_item_id",
                schema: "calendar",
                table: "recurrences",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_recurrences_list_id",
                schema: "calendar",
                table: "recurrences",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_recurrences_tenant_id",
                schema: "calendar",
                table: "recurrences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sources_item_id",
                schema: "calendar",
                table: "sources",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_sources_list_id_uid",
                schema: "calendar",
                table: "sources",
                columns: new[] { "list_id", "uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sources_tenant_id",
                schema: "calendar",
                table: "sources",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "calendar");

            migrationBuilder.DropTable(
                name: "feeds",
                schema: "calendar");

            migrationBuilder.DropTable(
                name: "occurrence_changes",
                schema: "calendar");

            migrationBuilder.DropTable(
                name: "recurrences",
                schema: "calendar");

            migrationBuilder.DropTable(
                name: "sources",
                schema: "calendar");
        }
    }
}

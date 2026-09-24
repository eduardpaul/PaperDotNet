using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Calendar
{
    /// <inheritdoc />
    public partial class Calendar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_audit_log",
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
                    table.PrimaryKey("pk_calendar_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_feeds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    user_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    secret_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_feeds", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_occurrence_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    master_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    original_start = table.Column<long>(type: "INTEGER", nullable: false),
                    override_item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_occurrence_changes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_recurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    rule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    time_zone = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_recurrences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_sources",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    uid = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_sources", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_audit_log_entity_id",
                table: "calendar_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_audit_log_tenant_id",
                table: "calendar_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_audit_log_tenant_id_at",
                table: "calendar_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_calendar_feeds_secret_hash",
                table: "calendar_feeds",
                column: "secret_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_feeds_tenant_id",
                table: "calendar_feeds",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_feeds_user_id",
                table: "calendar_feeds",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_occurrence_changes_master_item_id_original_start",
                table: "calendar_occurrence_changes",
                columns: new[] { "master_item_id", "original_start" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_occurrence_changes_override_item_id",
                table: "calendar_occurrence_changes",
                column: "override_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_occurrence_changes_tenant_id",
                table: "calendar_occurrence_changes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_recurrences_item_id",
                table: "calendar_recurrences",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_recurrences_list_id",
                table: "calendar_recurrences",
                column: "list_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_recurrences_tenant_id",
                table: "calendar_recurrences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_item_id",
                table: "calendar_sources",
                column: "item_id");

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_list_id_uid",
                table: "calendar_sources",
                columns: new[] { "list_id", "uid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_calendar_sources_tenant_id",
                table: "calendar_sources",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_audit_log");

            migrationBuilder.DropTable(
                name: "calendar_feeds");

            migrationBuilder.DropTable(
                name: "calendar_occurrence_changes");

            migrationBuilder.DropTable(
                name: "calendar_recurrences");

            migrationBuilder.DropTable(
                name: "calendar_sources");
        }
    }
}

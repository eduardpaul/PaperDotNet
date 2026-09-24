using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Tasks
{
    /// <inheritdoc />
    public partial class Tasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tasks_audit_log",
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
                    table.PrimaryKey("pk_tasks_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tasks_checklist",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    done = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks_checklist", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tasks_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    target_list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks_links", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tasks_recurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    rule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tasks_recurrences", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_audit_log_entity_id",
                table: "tasks_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_audit_log_tenant_id",
                table: "tasks_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_audit_log_tenant_id_at",
                table: "tasks_audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_checklist_item_id_position",
                table: "tasks_checklist",
                columns: new[] { "item_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_checklist_tenant_id",
                table: "tasks_checklist",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_links_item_id_target_item_id_kind",
                table: "tasks_links",
                columns: new[] { "item_id", "target_item_id", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_links_target_item_id_kind",
                table: "tasks_links",
                columns: new[] { "target_item_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_tasks_links_tenant_id",
                table: "tasks_links",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_tasks_recurrences_item_id",
                table: "tasks_recurrences",
                column: "item_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_tasks_recurrences_tenant_id",
                table: "tasks_recurrences",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tasks_audit_log");

            migrationBuilder.DropTable(
                name: "tasks_checklist");

            migrationBuilder.DropTable(
                name: "tasks_links");

            migrationBuilder.DropTable(
                name: "tasks_recurrences");
        }
    }
}

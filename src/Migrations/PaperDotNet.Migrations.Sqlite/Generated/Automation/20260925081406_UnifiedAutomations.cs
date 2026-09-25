using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Automation
{
    /// <inheritdoc />
    public partial class UnifiedAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_rule_runs");

            migrationBuilder.DropTable(
                name: "automation_rules");

            migrationBuilder.DropTable(
                name: "automation_workflow_runs");

            migrationBuilder.DropTable(
                name: "automation_workflow_versions");

            migrationBuilder.DropTable(
                name: "automation_workflows");

            migrationBuilder.CreateTable(
                name: "automation_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    current_version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    automation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    automation_version = table.Column<int>(type: "INTEGER", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    data = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    outcomes = table.Column<string>(type: "TEXT", nullable: false),
                    log = table.Column<string>(type: "TEXT", nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    waiting_for = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    resume_at = table.Column<long>(type: "INTEGER", nullable: true),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    started_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    automation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    definition = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id",
                table: "automation_definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_name",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_definitions_tenant_id_workspace_id_trigger",
                table: "automation_definitions",
                columns: new[] { "tenant_id", "workspace_id", "trigger" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_automation_id_event_id",
                table: "automation_runs",
                columns: new[] { "automation_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_status_resume_at",
                table: "automation_runs",
                columns: new[] { "status", "resume_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_tenant_id",
                table: "automation_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_tenant_id_automation_id_started_at",
                table: "automation_runs",
                columns: new[] { "tenant_id", "automation_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_tenant_id_item_id",
                table: "automation_runs",
                columns: new[] { "tenant_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_tenant_id_status_completed_at",
                table: "automation_runs",
                columns: new[] { "tenant_id", "status", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_versions_automation_id_number",
                table: "automation_versions",
                columns: new[] { "automation_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_versions_tenant_id",
                table: "automation_versions",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_definitions");

            migrationBuilder.DropTable(
                name: "automation_runs");

            migrationBuilder.DropTable(
                name: "automation_versions");

            migrationBuilder.CreateTable(
                name: "automation_rule_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    rule_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_rule_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    definition = table.Column<string>(type: "TEXT", nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_workflow_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    depth = table.Column<int>(type: "INTEGER", nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    log = table.Column<string>(type: "TEXT", nullable: false),
                    outcomes = table.Column<string>(type: "TEXT", nullable: false),
                    position = table.Column<int>(type: "INTEGER", nullable: false),
                    resume_at = table.Column<long>(type: "INTEGER", nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    started_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    waiting_for = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_workflow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_workflow_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    definition = table.Column<string>(type: "TEXT", nullable: false),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_workflow_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_workflows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    current_version = table.Column<int>(type: "INTEGER", nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_workflows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_rule_runs_rule_id_event_id",
                table: "automation_rule_runs",
                columns: new[] { "rule_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_rule_runs_rule_id_started_at",
                table: "automation_rule_runs",
                columns: new[] { "rule_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_rule_runs_tenant_id",
                table: "automation_rule_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_rules_tenant_id",
                table: "automation_rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_rules_tenant_id_workspace_id_name",
                table: "automation_rules",
                columns: new[] { "tenant_id", "workspace_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_rules_tenant_id_workspace_id_trigger",
                table: "automation_rules",
                columns: new[] { "tenant_id", "workspace_id", "trigger" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflow_runs_status_resume_at",
                table: "automation_workflow_runs",
                columns: new[] { "status", "resume_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflow_runs_tenant_id",
                table: "automation_workflow_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflow_runs_tenant_id_definition_id_started_at",
                table: "automation_workflow_runs",
                columns: new[] { "tenant_id", "definition_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflow_runs_tenant_id_item_id",
                table: "automation_workflow_runs",
                columns: new[] { "tenant_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflow_versions_definition_id_number",
                table: "automation_workflow_versions",
                columns: new[] { "definition_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflow_versions_tenant_id",
                table: "automation_workflow_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflows_tenant_id",
                table: "automation_workflows",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_workflows_tenant_id_workspace_id_name",
                table: "automation_workflows",
                columns: new[] { "tenant_id", "workspace_id", "name" },
                unique: true);
        }
    }
}

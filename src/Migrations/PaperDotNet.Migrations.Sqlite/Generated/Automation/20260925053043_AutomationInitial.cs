using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Automation
{
    /// <inheritdoc />
    public partial class AutomationInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_approvals",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    step_name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    assignees = table.Column<string>(type: "TEXT", nullable: false),
                    escalate_to = table.Column<string>(type: "TEXT", nullable: false),
                    due_at = table.Column<long>(type: "INTEGER", nullable: true),
                    escalated = table.Column<bool>(type: "INTEGER", nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    decided_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    decided_at = table.Column<long>(type: "INTEGER", nullable: true),
                    comment = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_audit_log",
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
                    table.PrimaryKey("pk_automation_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_rule_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    rule_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    event_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true)
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
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    trigger = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    definition = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
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
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_version = table.Column<int>(type: "INTEGER", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    list_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("pk_automation_workflow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_workflow_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    definition_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    definition = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    current_version = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_workflows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_approvals_run_id",
                table: "automation_approvals",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_approvals_tenant_id",
                table: "automation_approvals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_approvals_tenant_id_status_due_at",
                table: "automation_approvals",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_audit_log_entity_id",
                table: "automation_audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_audit_log_tenant_id",
                table: "automation_audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_audit_log_tenant_id_at",
                table: "automation_audit_log",
                columns: new[] { "tenant_id", "at" });

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_approvals");

            migrationBuilder.DropTable(
                name: "automation_audit_log");

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
        }
    }
}

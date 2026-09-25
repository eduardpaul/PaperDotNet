using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Automation
{
    /// <inheritdoc />
    public partial class AutomationInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "automation");

            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    step_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    assignees = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    escalate_to = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    escalated = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approvals", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_log",
                schema: "automation",
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
                name: "rule_runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rule_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "rules",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    trigger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_rules", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_version = table.Column<int>(type: "integer", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    outcomes = table.Column<string>(type: "text", nullable: false),
                    log = table.Column<string>(type: "text", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    waiting_for = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resume_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_versions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflows",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflows", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_run_id",
                schema: "automation",
                table: "approvals",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_tenant_id",
                schema: "automation",
                table: "approvals",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_approvals_tenant_id_status_due_at",
                schema: "automation",
                table: "approvals",
                columns: new[] { "tenant_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entity_id",
                schema: "automation",
                table: "audit_log",
                column: "entity_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id",
                schema: "automation",
                table: "audit_log",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_at",
                schema: "automation",
                table: "audit_log",
                columns: new[] { "tenant_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_runs_rule_id_event_id",
                schema: "automation",
                table: "rule_runs",
                columns: new[] { "rule_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rule_runs_rule_id_started_at",
                schema: "automation",
                table: "rule_runs",
                columns: new[] { "rule_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_rule_runs_tenant_id",
                schema: "automation",
                table: "rule_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_rules_tenant_id",
                schema: "automation",
                table: "rules",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_rules_tenant_id_workspace_id_name",
                schema: "automation",
                table: "rules",
                columns: new[] { "tenant_id", "workspace_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_rules_tenant_id_workspace_id_trigger",
                schema: "automation",
                table: "rules",
                columns: new[] { "tenant_id", "workspace_id", "trigger" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_status_resume_at",
                schema: "automation",
                table: "workflow_runs",
                columns: new[] { "status", "resume_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_tenant_id",
                schema: "automation",
                table: "workflow_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_tenant_id_definition_id_started_at",
                schema: "automation",
                table: "workflow_runs",
                columns: new[] { "tenant_id", "definition_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_runs_tenant_id_item_id",
                schema: "automation",
                table: "workflow_runs",
                columns: new[] { "tenant_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_versions_definition_id_number",
                schema: "automation",
                table: "workflow_versions",
                columns: new[] { "definition_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_versions_tenant_id",
                schema: "automation",
                table: "workflow_versions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflows_tenant_id",
                schema: "automation",
                table: "workflows",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_workflows_tenant_id_workspace_id_name",
                schema: "automation",
                table: "workflows",
                columns: new[] { "tenant_id", "workspace_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "audit_log",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "rule_runs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "rules",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_runs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflow_versions",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "workflows",
                schema: "automation");
        }
    }
}

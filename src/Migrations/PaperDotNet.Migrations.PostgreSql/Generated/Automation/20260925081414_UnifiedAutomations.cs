using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Automation
{
    /// <inheritdoc />
    public partial class UnifiedAutomations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

            migrationBuilder.CreateTable(
                name: "definitions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    trigger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_definitions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_version = table.Column<int>(type: "integer", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    data = table.Column<string>(type: "text", nullable: true),
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
                    table.PrimaryKey("pk_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "versions",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    automation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    definition = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_versions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id",
                schema: "automation",
                table: "definitions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_name",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_definitions_tenant_id_workspace_id_trigger",
                schema: "automation",
                table: "definitions",
                columns: new[] { "tenant_id", "workspace_id", "trigger" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_automation_id_event_id",
                schema: "automation",
                table: "runs",
                columns: new[] { "automation_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_status_resume_at",
                schema: "automation",
                table: "runs",
                columns: new[] { "status", "resume_at" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_tenant_id",
                schema: "automation",
                table: "runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_runs_tenant_id_automation_id_started_at",
                schema: "automation",
                table: "runs",
                columns: new[] { "tenant_id", "automation_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_tenant_id_item_id",
                schema: "automation",
                table: "runs",
                columns: new[] { "tenant_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_runs_tenant_id_status_completed_at",
                schema: "automation",
                table: "runs",
                columns: new[] { "tenant_id", "status", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_versions_automation_id_number",
                schema: "automation",
                table: "versions",
                columns: new[] { "automation_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_versions_tenant_id",
                schema: "automation",
                table: "versions",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "definitions",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "runs",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "versions",
                schema: "automation");

            migrationBuilder.CreateTable(
                name: "rule_runs",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    definition = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_version = table.Column<int>(type: "integer", nullable: false),
                    depth = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    log = table.Column<string>(type: "text", nullable: false),
                    outcomes = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    resume_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    waiting_for = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    definition = table.Column<string>(type: "text", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflows", x => x.id);
                });

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
    }
}

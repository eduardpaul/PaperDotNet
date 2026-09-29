using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowFlowsAndBookmarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_runs_status_resume_at",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "resume_at",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "waiting_for",
                table: "automation_runs");

            migrationBuilder.RenameColumn(
                name: "position",
                table: "automation_runs",
                newName: "node_attempts");

            migrationBuilder.RenameColumn(
                name: "outcomes",
                table: "automation_runs",
                newName: "variables");

            migrationBuilder.AddColumn<int>(
                name: "executed",
                table: "automation_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "failed_node",
                table: "automation_runs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "node",
                table: "automation_runs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outputs",
                table: "automation_runs",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "waiting_on",
                table: "automation_runs",
                type: "TEXT",
                nullable: true);

            // ADR-0036: runs now follow flows (a node instead of a position) and wait on bookmarks. Runs in progress cannot be
            // mapped onto nodes, so they are cancelled; the state of finished runs starts empty.
            migrationBuilder.Sql("UPDATE automation_approvals SET status = 'Cancelled' WHERE status = 'Pending';");
            migrationBuilder.Sql("UPDATE automation_runs SET status = 'Cancelled', completed_at = last_activity_at, error = 'Stopped by the upgrade to flows (ADR-0036); start the workflow again.' WHERE status IN ('Running', 'Waiting');");
            migrationBuilder.Sql("UPDATE automation_runs SET node_attempts = 0, variables = '{}', outputs = '{}';");

            migrationBuilder.CreateTable(
                name: "automation_bookmarks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    node = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    resume_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    payload = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_bookmarks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_bookmarks_completed_at_resume_at",
                table: "automation_bookmarks",
                columns: new[] { "completed_at", "resume_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_bookmarks_run_id",
                table: "automation_bookmarks",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_bookmarks_tenant_id",
                table: "automation_bookmarks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_bookmarks_tenant_id_kind_key",
                table: "automation_bookmarks",
                columns: new[] { "tenant_id", "kind", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_bookmarks");

            migrationBuilder.DropColumn(
                name: "executed",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "failed_node",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "node",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "outputs",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "waiting_on",
                table: "automation_runs");

            migrationBuilder.RenameColumn(
                name: "variables",
                table: "automation_runs",
                newName: "outcomes");

            migrationBuilder.RenameColumn(
                name: "node_attempts",
                table: "automation_runs",
                newName: "position");

            migrationBuilder.AddColumn<long>(
                name: "resume_at",
                table: "automation_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "waiting_for",
                table: "automation_runs",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_status_resume_at",
                table: "automation_runs",
                columns: new[] { "status", "resume_at" });
        }
    }
}

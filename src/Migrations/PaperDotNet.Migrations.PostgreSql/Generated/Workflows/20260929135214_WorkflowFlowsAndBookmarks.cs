using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowFlowsAndBookmarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_runs_status_resume_at",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "resume_at",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "waiting_for",
                schema: "automation",
                table: "runs");

            migrationBuilder.RenameColumn(
                name: "position",
                schema: "automation",
                table: "runs",
                newName: "node_attempts");

            migrationBuilder.RenameColumn(
                name: "outcomes",
                schema: "automation",
                table: "runs",
                newName: "variables");

            migrationBuilder.AddColumn<int>(
                name: "executed",
                schema: "automation",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "failed_node",
                schema: "automation",
                table: "runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "node",
                schema: "automation",
                table: "runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "outputs",
                schema: "automation",
                table: "runs",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "waiting_on",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            // ADR-0036: runs now follow flows (a node instead of a position) and wait on bookmarks. Runs in progress cannot be
            // mapped onto nodes, so they are cancelled; the state of finished runs starts empty.
            migrationBuilder.Sql("UPDATE automation.approvals SET status = 'Cancelled' WHERE status = 'Pending';");
            migrationBuilder.Sql("UPDATE automation.runs SET status = 'Cancelled', completed_at = last_activity_at, error = 'Stopped by the upgrade to flows (ADR-0036); start the workflow again.' WHERE status IN ('Running', 'Waiting');");
            migrationBuilder.Sql("UPDATE automation.runs SET node_attempts = 0, variables = '{}', outputs = '{}';");

            migrationBuilder.CreateTable(
                name: "bookmarks",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    resume_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payload = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bookmarks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bookmarks_completed_at_resume_at",
                schema: "automation",
                table: "bookmarks",
                columns: new[] { "completed_at", "resume_at" });

            migrationBuilder.CreateIndex(
                name: "ix_bookmarks_run_id",
                schema: "automation",
                table: "bookmarks",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookmarks_tenant_id",
                schema: "automation",
                table: "bookmarks",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookmarks_tenant_id_kind_key",
                schema: "automation",
                table: "bookmarks",
                columns: new[] { "tenant_id", "kind", "key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bookmarks",
                schema: "automation");

            migrationBuilder.DropColumn(
                name: "executed",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "failed_node",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "node",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "outputs",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "waiting_on",
                schema: "automation",
                table: "runs");

            migrationBuilder.RenameColumn(
                name: "variables",
                schema: "automation",
                table: "runs",
                newName: "outcomes");

            migrationBuilder.RenameColumn(
                name: "node_attempts",
                schema: "automation",
                table: "runs",
                newName: "position");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "resume_at",
                schema: "automation",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "waiting_for",
                schema: "automation",
                table: "runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_status_resume_at",
                schema: "automation",
                table: "runs",
                columns: new[] { "status", "resume_at" });
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowAiBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "run_again",
                table: "automation_bookmarks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "automation_ai_batch_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    activity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    input_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    pending_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    question = table.Column<string>(type: "TEXT", nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    batch_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    response = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    waiters = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    deadline_at = table.Column<long>(type: "INTEGER", nullable: false),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_ai_batch_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "automation_ai_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    provider_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    lines = table.Column<int>(type: "INTEGER", nullable: false),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    submitted_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_ai_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_batch_requests_batch_id",
                table: "automation_ai_batch_requests",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_batch_requests_tenant_id",
                table: "automation_ai_batch_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_batch_requests_tenant_id_pending_hash",
                table: "automation_ai_batch_requests",
                columns: new[] { "tenant_id", "pending_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_batch_requests_tenant_id_status_created_at",
                table: "automation_ai_batch_requests",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_batches_tenant_id",
                table: "automation_ai_batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_batches_tenant_id_status",
                table: "automation_ai_batches",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_ai_batch_requests");

            migrationBuilder.DropTable(
                name: "automation_ai_batches");

            migrationBuilder.DropColumn(
                name: "run_again",
                table: "automation_bookmarks");
        }
    }
}

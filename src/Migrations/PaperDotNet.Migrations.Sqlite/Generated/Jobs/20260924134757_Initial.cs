using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Jobs
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "jobs_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "INTEGER", nullable: false),
                    percent_complete = table.Column<int>(type: "INTEGER", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false),
                    result = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", nullable: true),
                    started_at = table.Column<long>(type: "INTEGER", nullable: true),
                    completed_at = table.Column<long>(type: "INTEGER", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs_operations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs_recurring_jobs",
                columns: table => new
                {
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    next_run_at = table.Column<long>(type: "INTEGER", nullable: false),
                    last_run_at = table.Column<long>(type: "INTEGER", nullable: true),
                    last_status = table.Column<string>(type: "TEXT", nullable: true),
                    last_error = table.Column<string>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs_recurring_jobs", x => x.name);
                });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_operations_status_completed_at",
                table: "jobs_operations",
                columns: new[] { "status", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_operations_tenant_id",
                table: "jobs_operations",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "jobs_operations");

            migrationBuilder.DropTable(
                name: "jobs_recurring_jobs");
        }
    }
}

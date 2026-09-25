using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Automation
{
    /// <inheritdoc />
    public partial class AutomationRunLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "automation_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "last_activity_at",
                table: "automation_runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                table: "automation_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "lease_until",
                table: "automation_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "next_check_at",
                table: "automation_runs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "step_execution_id",
                table: "automation_runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_automation_runs_status_last_activity_at",
                table: "automation_runs",
                columns: new[] { "status", "last_activity_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_automation_runs_status_last_activity_at",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "last_activity_at",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "lease_id",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "lease_until",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "next_check_at",
                table: "automation_runs");

            migrationBuilder.DropColumn(
                name: "step_execution_id",
                table: "automation_runs");
        }
    }
}

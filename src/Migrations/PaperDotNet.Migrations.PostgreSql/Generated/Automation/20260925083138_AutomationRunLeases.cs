using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Automation
{
    /// <inheritdoc />
    public partial class AutomationRunLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                schema: "automation",
                table: "runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_activity_at",
                schema: "automation",
                table: "runs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "lease_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_until",
                schema: "automation",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_check_at",
                schema: "automation",
                table: "runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "step_execution_id",
                schema: "automation",
                table: "runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_runs_status_last_activity_at",
                schema: "automation",
                table: "runs",
                columns: new[] { "status", "last_activity_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_runs_status_last_activity_at",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "attempts",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "last_activity_at",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "lease_id",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "lease_until",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "next_check_at",
                schema: "automation",
                table: "runs");

            migrationBuilder.DropColumn(
                name: "step_execution_id",
                schema: "automation",
                table: "runs");
        }
    }
}

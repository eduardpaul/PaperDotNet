using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows;

/// <inheritdoc />
public partial class _20261001162922_WorkflowSchedules : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "workflow_schedules",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                WorkflowVersion = table.Column<int>(type: "INTEGER", nullable: false),
                NextAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                CheckedUntilUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_schedules", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_schedules_TenantId",
            table: "workflow_schedules",
            column: "TenantId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workflow_schedules");
    }
}

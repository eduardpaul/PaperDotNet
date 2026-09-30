using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Jobs;

/// <inheritdoc />
public partial class _20260930195100_JobsAndTenantStatus : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "operations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Type = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                PercentComplete = table.Column<int>(type: "INTEGER", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: true),
                Result = table.Column<string>(type: "TEXT", nullable: true),
                Error = table.Column<string>(type: "TEXT", nullable: true),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operations", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "recurring_jobs",
            columns: table => new
            {
                Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                NextRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                LastRunAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                LastStatus = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                LastError = table.Column<string>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_recurring_jobs", x => x.Name);
            });

        migrationBuilder.CreateIndex(
            name: "IX_operations_TenantId_Status",
            table: "operations",
            columns: new[] { "TenantId", "Status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "operations");

        migrationBuilder.DropTable(
            name: "recurring_jobs");
    }
}

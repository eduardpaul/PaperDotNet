using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows;

/// <inheritdoc />
public partial class _20261001154140_WorkflowWaits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "WaitingOn",
            table: "workflow_runs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "workflow_approvals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Node = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                ListId = table.Column<Guid>(type: "TEXT", nullable: true),
                ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                Title = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                Assignees = table.Column<string>(type: "TEXT", nullable: false),
                EscalateTo = table.Column<string>(type: "TEXT", nullable: false),
                DueAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                DueAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                Escalated = table.Column<bool>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                DecidedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                Comment = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_approvals", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "workflow_bookmarks",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                Node = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ResumeAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                CompletedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: true),
                Payload = table.Column<string>(type: "TEXT", nullable: true),
                Data = table.Column<string>(type: "TEXT", nullable: true),
                RunAgain = table.Column<bool>(type: "INTEGER", nullable: false),
                Version = table.Column<uint>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_workflow_bookmarks", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_approvals_TenantId_RunId",
            table: "workflow_approvals",
            columns: new[] { "TenantId", "RunId" });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_approvals_TenantId_Status_DueAtUnixMs",
            table: "workflow_approvals",
            columns: new[] { "TenantId", "Status", "DueAtUnixMs" });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_bookmarks_TenantId_Kind_Key",
            table: "workflow_bookmarks",
            columns: new[] { "TenantId", "Kind", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_workflow_bookmarks_TenantId_ResumeAtUnixMs",
            table: "workflow_bookmarks",
            columns: new[] { "TenantId", "ResumeAtUnixMs" });

        migrationBuilder.CreateIndex(
            name: "IX_workflow_bookmarks_TenantId_RunId",
            table: "workflow_bookmarks",
            columns: new[] { "TenantId", "RunId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "workflow_approvals");

        migrationBuilder.DropTable(
            name: "workflow_bookmarks");

        migrationBuilder.DropColumn(
            name: "WaitingOn",
            table: "workflow_runs");
    }
}

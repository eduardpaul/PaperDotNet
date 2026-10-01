using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows;

/// <inheritdoc />
public partial class _20261001164649_BuiltInWorkflows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "BuiltInKey",
            table: "workflows",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "CopiedFrom",
            table: "workflows",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "ListId",
            table: "workflows",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Parameters",
            table: "workflows",
            type: "TEXT",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_workflows_TenantId_WorkspaceId_BuiltInKey_ListId",
            table: "workflows",
            columns: new[] { "TenantId", "WorkspaceId", "BuiltInKey", "ListId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_workflows_TenantId_WorkspaceId_BuiltInKey_ListId",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "BuiltInKey",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "CopiedFrom",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "ListId",
            table: "workflows");

        migrationBuilder.DropColumn(
            name: "Parameters",
            table: "workflows");
    }
}

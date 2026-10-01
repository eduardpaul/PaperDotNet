using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.AiWorkflows;

/// <inheritdoc />
public partial class _20261001171037_AiWorkflows : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ai_calls",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                Activity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                Source = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                InputHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                Cached = table.Column<bool>(type: "INTEGER", nullable: false),
                Response = table.Column<string>(type: "TEXT", nullable: true),
                Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                CreatedAtUnixMs = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ai_calls", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ai_calls_TenantId_CreatedAtUnixMs",
            table: "ai_calls",
            columns: new[] { "TenantId", "CreatedAtUnixMs" });

        migrationBuilder.CreateIndex(
            name: "IX_ai_calls_TenantId_InputHash_CreatedAtUnixMs",
            table: "ai_calls",
            columns: new[] { "TenantId", "InputHash", "CreatedAtUnixMs" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ai_calls");
    }
}

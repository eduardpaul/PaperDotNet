using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowAiCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "automation_ai_calls",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    activity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    input_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    input_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    output_tokens = table.Column<long>(type: "INTEGER", nullable: false),
                    cached = table.Column<bool>(type: "INTEGER", nullable: false),
                    response = table.Column<string>(type: "TEXT", nullable: true),
                    error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_automation_ai_calls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_calls_tenant_id",
                table: "automation_ai_calls",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_calls_tenant_id_created_at",
                table: "automation_ai_calls",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_automation_ai_calls_tenant_id_input_hash_created_at",
                table: "automation_ai_calls",
                columns: new[] { "tenant_id", "input_hash", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "automation_ai_calls");
        }
    }
}

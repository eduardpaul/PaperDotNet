using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowAiCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_calls",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    input_tokens = table.Column<long>(type: "bigint", nullable: false),
                    output_tokens = table.Column<long>(type: "bigint", nullable: false),
                    cached = table.Column<bool>(type: "boolean", nullable: false),
                    response = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_calls", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_tenant_id",
                schema: "automation",
                table: "ai_calls",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_tenant_id_created_at",
                schema: "automation",
                table: "ai_calls",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_calls_tenant_id_input_hash_created_at",
                schema: "automation",
                table: "ai_calls",
                columns: new[] { "tenant_id", "input_hash", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_calls",
                schema: "automation");
        }
    }
}

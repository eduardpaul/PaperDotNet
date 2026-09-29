using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Workflows
{
    /// <inheritdoc />
    public partial class WorkflowAiBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "run_again",
                schema: "automation",
                table: "bookmarks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ai_batch_requests",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    activity = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    input_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pending_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    question = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    response = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    waiters = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_batch_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_batches",
                schema: "automation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    provider_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    lines = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_batches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_batch_requests_batch_id",
                schema: "automation",
                table: "ai_batch_requests",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_batch_requests_tenant_id",
                schema: "automation",
                table: "ai_batch_requests",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_batch_requests_tenant_id_pending_hash",
                schema: "automation",
                table: "ai_batch_requests",
                columns: new[] { "tenant_id", "pending_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ai_batch_requests_tenant_id_status_created_at",
                schema: "automation",
                table: "ai_batch_requests",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_ai_batches_tenant_id",
                schema: "automation",
                table: "ai_batches",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_ai_batches_tenant_id_status",
                schema: "automation",
                table: "ai_batches",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_batch_requests",
                schema: "automation");

            migrationBuilder.DropTable(
                name: "ai_batches",
                schema: "automation");

            migrationBuilder.DropColumn(
                name: "run_again",
                schema: "automation",
                table: "bookmarks");
        }
    }
}

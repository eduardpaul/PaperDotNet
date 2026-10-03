using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Documents
{
    /// <inheritdoc />
    public partial class StorageOptimization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "file_candidates",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    promoted_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_json = table.Column<string>(type: "text", nullable: false),
                    metrics_json = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_file_candidates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_file_versions_tenant_id_item_id",
                schema: "documents",
                table: "file_versions",
                columns: new[] { "tenant_id", "item_id" },
                unique: true,
                filter: "\"is_current\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ix_file_candidates_run_id",
                schema: "documents",
                table: "file_candidates",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_candidates_tenant_id",
                schema: "documents",
                table: "file_candidates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_file_candidates_tenant_id_source_version_id",
                schema: "documents",
                table: "file_candidates",
                columns: new[] { "tenant_id", "source_version_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "file_candidates",
                schema: "documents");

            migrationBuilder.DropIndex(
                name: "ix_file_versions_tenant_id_item_id",
                schema: "documents",
                table: "file_versions");
        }
    }
}

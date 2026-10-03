using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Documents
{
    /// <inheritdoc />
    public partial class StorageOptimization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents_file_candidates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    item_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    source_stored_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    promoted_version_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    source_json = table.Column<string>(type: "TEXT", nullable: false),
                    metrics_json = table.Column<string>(type: "TEXT", nullable: false),
                    state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_file_candidates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_versions_tenant_id_item_id",
                table: "documents_file_versions",
                columns: new[] { "tenant_id", "item_id" },
                unique: true,
                filter: "\"is_current\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_candidates_run_id",
                table: "documents_file_candidates",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_candidates_tenant_id",
                table: "documents_file_candidates",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_file_candidates_tenant_id_source_version_id",
                table: "documents_file_candidates",
                columns: new[] { "tenant_id", "source_version_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents_file_candidates");

            migrationBuilder.DropIndex(
                name: "ix_documents_file_versions_tenant_id_item_id",
                table: "documents_file_versions");
        }
    }
}

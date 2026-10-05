using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Documents
{
    /// <inheritdoc />
    public partial class StagedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents_staged_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    owner = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    has_prepared_text = table.Column<bool>(type: "INTEGER", nullable: false),
                    page_count = table.Column<int>(type: "INTEGER", nullable: true),
                    published_version_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    file_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    languages = table.Column<string>(type: "TEXT", nullable: true),
                    state = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_staged_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "documents_staged_file_references",
                columns: table => new
                {
                    staged_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_staged_file_references", x => new { x.staged_file_id, x.stored_file_id });
                    table.ForeignKey(
                        name: "fk_documents_staged_file_references_documents_staged_files_staged_file_id",
                        column: x => x.staged_file_id,
                        principalTable: "documents_staged_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_staged_file_references_tenant_id",
                table: "documents_staged_file_references",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_documents_staged_files_tenant_id",
                table: "documents_staged_files",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents_staged_file_references");

            migrationBuilder.DropTable(
                name: "documents_staged_files");
        }
    }
}

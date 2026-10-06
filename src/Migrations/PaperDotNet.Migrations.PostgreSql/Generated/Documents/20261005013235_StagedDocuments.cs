using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Documents
{
    /// <inheritdoc />
    public partial class StagedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staged_files",
                schema: "documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    has_prepared_text = table.Column<bool>(type: "boolean", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: true),
                    published_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    languages = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staged_files", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "staged_file_references",
                schema: "documents",
                columns: table => new
                {
                    staged_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_staged_file_references", x => new { x.staged_file_id, x.stored_file_id });
                    table.ForeignKey(
                        name: "fk_staged_file_references_staged_files_staged_file_id",
                        column: x => x.staged_file_id,
                        principalSchema: "documents",
                        principalTable: "staged_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staged_file_references_tenant_id",
                schema: "documents",
                table: "staged_file_references",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_staged_files_tenant_id",
                schema: "documents",
                table: "staged_files",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staged_file_references",
                schema: "documents");

            migrationBuilder.DropTable(
                name: "staged_files",
                schema: "documents");
        }
    }
}

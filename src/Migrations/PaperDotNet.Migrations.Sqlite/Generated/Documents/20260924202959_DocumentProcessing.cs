using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Documents
{
    /// <inheritdoc />
    public partial class DocumentProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_process",
                table: "documents_library_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_languages",
                table: "documents_library_settings",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                defaultValue: "eng");

            migrationBuilder.AddColumn<string>(
                name: "ocr_mode",
                table: "documents_library_settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                table: "documents_file_versions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_count",
                table: "documents_file_versions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_error",
                table: "documents_file_versions",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_status",
                table: "documents_file_versions",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "text_language",
                table: "documents_file_versions",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "documents_stored_file_pages",
                columns: table => new
                {
                    stored_file_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    page_number = table.Column<int>(type: "INTEGER", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_documents_stored_file_pages", x => new { x.stored_file_id, x.page_number });
                });

            migrationBuilder.CreateIndex(
                name: "ix_documents_stored_file_pages_tenant_id",
                table: "documents_stored_file_pages",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents_stored_file_pages");

            migrationBuilder.DropColumn(
                name: "auto_process",
                table: "documents_library_settings");

            migrationBuilder.DropColumn(
                name: "ocr_languages",
                table: "documents_library_settings");

            migrationBuilder.DropColumn(
                name: "ocr_mode",
                table: "documents_library_settings");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "documents_file_versions");

            migrationBuilder.DropColumn(
                name: "page_count",
                table: "documents_file_versions");

            migrationBuilder.DropColumn(
                name: "processing_error",
                table: "documents_file_versions");

            migrationBuilder.DropColumn(
                name: "processing_status",
                table: "documents_file_versions");

            migrationBuilder.DropColumn(
                name: "text_language",
                table: "documents_file_versions");
        }
    }
}

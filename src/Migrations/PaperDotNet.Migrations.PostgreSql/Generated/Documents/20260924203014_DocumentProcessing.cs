using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Documents
{
    /// <inheritdoc />
    public partial class DocumentProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_process",
                schema: "documents",
                table: "library_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ocr_languages",
                schema: "documents",
                table: "library_settings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "eng");

            migrationBuilder.AddColumn<string>(
                name: "ocr_mode",
                schema: "documents",
                table: "library_settings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Auto");

            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                schema: "documents",
                table: "file_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "page_count",
                schema: "documents",
                table: "file_versions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_error",
                schema: "documents",
                table: "file_versions",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_status",
                schema: "documents",
                table: "file_versions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "text_language",
                schema: "documents",
                table: "file_versions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "stored_file_pages",
                schema: "documents",
                columns: table => new
                {
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_file_pages", x => new { x.stored_file_id, x.page_number });
                });

            migrationBuilder.CreateIndex(
                name: "ix_stored_file_pages_tenant_id",
                schema: "documents",
                table: "stored_file_pages",
                column: "tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stored_file_pages",
                schema: "documents");

            migrationBuilder.DropColumn(
                name: "auto_process",
                schema: "documents",
                table: "library_settings");

            migrationBuilder.DropColumn(
                name: "ocr_languages",
                schema: "documents",
                table: "library_settings");

            migrationBuilder.DropColumn(
                name: "ocr_mode",
                schema: "documents",
                table: "library_settings");

            migrationBuilder.DropColumn(
                name: "operation_id",
                schema: "documents",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "page_count",
                schema: "documents",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "processing_error",
                schema: "documents",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "processing_status",
                schema: "documents",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "text_language",
                schema: "documents",
                table: "file_versions");
        }
    }
}

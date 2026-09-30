using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Documents
{
    /// <inheritdoc />
    public partial class DocumentsComposedFromWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_process",
                table: "documents_library_settings");

            migrationBuilder.DropColumn(
                name: "ocr_mode",
                table: "documents_library_settings");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "documents_file_versions");

            migrationBuilder.DropColumn(
                name: "processing_error",
                table: "documents_file_versions");

            migrationBuilder.DropColumn(
                name: "processing_status",
                table: "documents_file_versions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_process",
                table: "documents_library_settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ocr_mode",
                table: "documents_library_settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                table: "documents_file_versions",
                type: "TEXT",
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
                defaultValue: "");
        }
    }
}

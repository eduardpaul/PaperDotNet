using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Documents
{
    /// <inheritdoc />
    public partial class DocumentsComposedFromWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_process",
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
                name: "processing_error",
                schema: "documents",
                table: "file_versions");

            migrationBuilder.DropColumn(
                name: "processing_status",
                schema: "documents",
                table: "file_versions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_process",
                schema: "documents",
                table: "library_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ocr_mode",
                schema: "documents",
                table: "library_settings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                schema: "documents",
                table: "file_versions",
                type: "uuid",
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
                defaultValue: "");
        }
    }
}

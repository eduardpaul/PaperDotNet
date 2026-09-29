using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class ScopeTrimming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_document_principals");

            migrationBuilder.AddColumn<Guid>(
                name: "scope_id",
                table: "search_documents",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_search_documents_scope_id_id",
                table: "search_documents",
                columns: new[] { "scope_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_documents_scope_id_id",
                table: "search_documents");

            migrationBuilder.DropColumn(
                name: "scope_id",
                table: "search_documents");

            migrationBuilder.CreateTable(
                name: "search_document_principals",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    principal = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_document_principals", x => new { x.document_id, x.principal });
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_document_principals_principal_document_id",
                table: "search_document_principals",
                columns: new[] { "principal", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_document_principals_tenant_id",
                table: "search_document_principals",
                column: "tenant_id");
        }
    }
}

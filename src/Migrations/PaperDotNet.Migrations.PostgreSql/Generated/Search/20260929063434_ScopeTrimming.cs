using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class ScopeTrimming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_principals",
                schema: "search");

            migrationBuilder.AddColumn<Guid>(
                name: "scope_id",
                schema: "search",
                table: "documents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "ix_documents_scope_id_id",
                schema: "search",
                table: "documents",
                columns: new[] { "scope_id", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_documents_scope_id_id",
                schema: "search",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "scope_id",
                schema: "search",
                table: "documents");

            migrationBuilder.CreateTable(
                name: "document_principals",
                schema: "search",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    principal = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_principals", x => new { x.document_id, x.principal });
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_principals_principal_document_id",
                schema: "search",
                table: "document_principals",
                columns: new[] { "principal", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_principals_tenant_id",
                schema: "search",
                table: "document_principals",
                column: "tenant_id");
        }
    }
}

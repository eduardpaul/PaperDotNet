using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchFieldValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_document_fields",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    number = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_document_fields", x => new { x.document_id, x.name, x.kind, x.ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_document_fields_tenant_id",
                table: "search_document_fields",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_document_fields_tenant_id_name_kind_number",
                table: "search_document_fields",
                columns: new[] { "tenant_id", "name", "kind", "number" });

            migrationBuilder.CreateIndex(
                name: "ix_search_document_fields_tenant_id_name_kind_text",
                table: "search_document_fields",
                columns: new[] { "tenant_id", "name", "kind", "text" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_document_fields");
        }
    }
}

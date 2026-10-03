using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchFieldValues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_fields",
                schema: "search",
                columns: table => new
                {
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    number = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_document_fields", x => new { x.document_id, x.name, x.kind, x.ordinal });
                });

            migrationBuilder.CreateIndex(
                name: "ix_document_fields_tenant_id",
                schema: "search",
                table: "document_fields",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_fields_tenant_id_name_kind_number",
                schema: "search",
                table: "document_fields",
                columns: new[] { "tenant_id", "name", "kind", "number" });

            migrationBuilder.CreateIndex(
                name: "ix_document_fields_tenant_id_name_kind_text",
                schema: "search",
                table: "document_fields",
                columns: new[] { "tenant_id", "name", "kind", "text" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_fields",
                schema: "search");
        }
    }
}

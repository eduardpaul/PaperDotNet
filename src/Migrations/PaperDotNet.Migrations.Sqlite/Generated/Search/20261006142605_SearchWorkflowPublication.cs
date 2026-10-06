using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchWorkflowPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "search_container_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    included = table.Column<bool>(type: "INTEGER", nullable: false),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_container_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_enrichments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    text = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_enrichments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_generations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    payload = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_generations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "search_publications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    workspace_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    container_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    generation_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    revision = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    run_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    published_at = table.Column<long>(type: "INTEGER", nullable: true),
                    chunks = table.Column<int>(type: "INTEGER", nullable: false),
                    truncated = table.Column<bool>(type: "INTEGER", nullable: false),
                    embedding_model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    source_stamp = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    settings = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_search_publications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_search_container_policies_tenant_id",
                table: "search_container_policies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_enrichments_tenant_id",
                table: "search_enrichments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_generations_tenant_id",
                table: "search_generations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_publications_tenant_id",
                table: "search_publications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_search_publications_tenant_id_container_id",
                table: "search_publications",
                columns: new[] { "tenant_id", "container_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "search_container_policies");

            migrationBuilder.DropTable(
                name: "search_enrichments");

            migrationBuilder.DropTable(
                name: "search_generations");

            migrationBuilder.DropTable(
                name: "search_publications");
        }
    }
}

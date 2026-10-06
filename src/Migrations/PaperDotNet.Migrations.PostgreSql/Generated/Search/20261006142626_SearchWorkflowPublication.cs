using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Search
{
    /// <inheritdoc />
    public partial class SearchWorkflowPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "container_policies",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    included = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_container_policies", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "enrichments",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrichments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "generations",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_generations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "publications",
                schema: "search",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    generation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    chunks = table.Column<int>(type: "integer", nullable: false),
                    truncated = table.Column<bool>(type: "boolean", nullable: false),
                    embedding_model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_stamp = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    settings = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_publications", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_container_policies_tenant_id",
                schema: "search",
                table: "container_policies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_enrichments_tenant_id",
                schema: "search",
                table: "enrichments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_generations_tenant_id",
                schema: "search",
                table: "generations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_publications_tenant_id",
                schema: "search",
                table: "publications",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_publications_tenant_id_container_id",
                schema: "search",
                table: "publications",
                columns: new[] { "tenant_id", "container_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_policies",
                schema: "search");

            migrationBuilder.DropTable(
                name: "enrichments",
                schema: "search");

            migrationBuilder.DropTable(
                name: "generations",
                schema: "search");

            migrationBuilder.DropTable(
                name: "publications",
                schema: "search");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Taxonomy
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "taxonomy_groups",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_system = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxonomy_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "taxonomy_term_sets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    is_open = table.Column<bool>(type: "INTEGER", nullable: false),
                    is_keywords = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxonomy_term_sets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "taxonomy_terms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    term_set_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    parent_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    path = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    description = table.Column<string>(type: "TEXT", nullable: true),
                    color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    synonyms = table.Column<string>(type: "TEXT", nullable: false),
                    search_text = table.Column<string>(type: "TEXT", nullable: false),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    is_deprecated = table.Column<bool>(type: "INTEGER", nullable: false),
                    merged_into_id = table.Column<Guid>(type: "TEXT", nullable: true),
                    created_at = table.Column<long>(type: "INTEGER", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    updated_at = table.Column<long>(type: "INTEGER", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: true),
                    version = table.Column<uint>(type: "INTEGER", nullable: false),
                    labels = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_taxonomy_terms", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_groups_tenant_id",
                table: "taxonomy_groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_groups_tenant_id_name",
                table: "taxonomy_groups",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_term_sets_group_id_name",
                table: "taxonomy_term_sets",
                columns: new[] { "group_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_term_sets_tenant_id",
                table: "taxonomy_term_sets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_terms_path",
                table: "taxonomy_terms",
                column: "path");

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_terms_tenant_id",
                table: "taxonomy_terms",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_taxonomy_terms_term_set_id_parent_id_normalized_name",
                table: "taxonomy_terms",
                columns: new[] { "term_set_id", "parent_id", "normalized_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "taxonomy_groups");

            migrationBuilder.DropTable(
                name: "taxonomy_term_sets");

            migrationBuilder.DropTable(
                name: "taxonomy_terms");
        }
    }
}

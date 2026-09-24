using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Taxonomy
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "taxonomy");

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "taxonomy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "term_sets",
                schema: "taxonomy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    is_open = table.Column<bool>(type: "boolean", nullable: false),
                    is_keywords = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_term_sets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "terms",
                schema: "taxonomy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    term_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    path = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    color = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    synonyms = table.Column<List<string>>(type: "text[]", nullable: false),
                    search_text = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_deprecated = table.Column<bool>(type: "boolean", nullable: false),
                    merged_into_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_terms", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_groups_tenant_id",
                schema: "taxonomy",
                table: "groups",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_groups_tenant_id_name",
                schema: "taxonomy",
                table: "groups",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_term_sets_group_id_name",
                schema: "taxonomy",
                table: "term_sets",
                columns: new[] { "group_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_term_sets_tenant_id",
                schema: "taxonomy",
                table: "term_sets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_terms_path",
                schema: "taxonomy",
                table: "terms",
                column: "path");

            migrationBuilder.CreateIndex(
                name: "ix_terms_tenant_id",
                schema: "taxonomy",
                table: "terms",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_terms_term_set_id_parent_id_normalized_name",
                schema: "taxonomy",
                table: "terms",
                columns: new[] { "term_set_id", "parent_id", "normalized_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "groups",
                schema: "taxonomy");

            migrationBuilder.DropTable(
                name: "term_sets",
                schema: "taxonomy");

            migrationBuilder.DropTable(
                name: "terms",
                schema: "taxonomy");
        }
    }
}

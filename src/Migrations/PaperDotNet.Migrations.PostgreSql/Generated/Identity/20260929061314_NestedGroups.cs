using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.PostgreSql.Generated.Identity
{
    /// <inheritdoc />
    public partial class NestedGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_closure",
                schema: "identity",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ancestor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_closure", x => new { x.group_id, x.ancestor_id });
                });

            migrationBuilder.CreateTable(
                name: "group_nestings",
                schema: "identity",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    member_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_nestings", x => new { x.group_id, x.member_group_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_closure_ancestor_id_group_id",
                schema: "identity",
                table: "group_closure",
                columns: new[] { "ancestor_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_group_closure_tenant_id",
                schema: "identity",
                table: "group_closure",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_nestings_member_group_id",
                schema: "identity",
                table: "group_nestings",
                column: "member_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_nestings_tenant_id",
                schema: "identity",
                table: "group_nestings",
                column: "tenant_id");

            // Every existing group is its own first entry in the closure (ADR-0035).
            migrationBuilder.Sql("INSERT INTO identity.group_closure (group_id, ancestor_id, tenant_id) SELECT id, id, tenant_id FROM identity.groups;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_closure",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "group_nestings",
                schema: "identity");
        }
    }
}

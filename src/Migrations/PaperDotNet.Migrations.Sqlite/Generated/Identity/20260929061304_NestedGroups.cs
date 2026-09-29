using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaperDotNet.Migrations.Sqlite.Generated.Identity
{
    /// <inheritdoc />
    public partial class NestedGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "identity_group_closure",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ancestor_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_group_closure", x => new { x.group_id, x.ancestor_id });
                });

            migrationBuilder.CreateTable(
                name: "identity_group_nestings",
                columns: table => new
                {
                    group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    member_group_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_identity_group_nestings", x => new { x.group_id, x.member_group_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_identity_group_closure_ancestor_id_group_id",
                table: "identity_group_closure",
                columns: new[] { "ancestor_id", "group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_identity_group_closure_tenant_id",
                table: "identity_group_closure",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_group_nestings_member_group_id",
                table: "identity_group_nestings",
                column: "member_group_id");

            migrationBuilder.CreateIndex(
                name: "ix_identity_group_nestings_tenant_id",
                table: "identity_group_nestings",
                column: "tenant_id");

            // Every existing group is its own first entry in the closure (ADR-0035).
            migrationBuilder.Sql("INSERT INTO identity_group_closure (group_id, ancestor_id, tenant_id) SELECT id, id, tenant_id FROM identity_groups;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "identity_group_closure");

            migrationBuilder.DropTable(
                name: "identity_group_nestings");
        }
    }
}
